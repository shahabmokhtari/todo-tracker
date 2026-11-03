// The web app staying steady: only the newest answer shows, extras switched off elsewhere go here too, signing in
// with the token loads everything, the task panel keeps the keyboard, groups have a ⋯ menu, and a lost connection
// shows one notice instead of an error every few seconds. Runs last (it adds groups the dashboard tests don't expect).
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const token = 'e2e-token-0123456789abcdefghijklmnop';
const auth = { Authorization: `Bearer ${token}` };

test.describe.configure({ mode: 'serial' });

let group;
let other;

async function open(page, request, where = '/') {
  const launch = await (await request.post('/api/launch', { headers: auth, data: { return: where } })).json();
  await page.goto(launch.url);
  await expect(page.locator('#board')).toBeVisible();
}

const add = async (request, title, groupId = group.id) =>
  (await (await request.post('/api/items', { headers: auth, data: { title, groupId } })).json());

// Extras are switched on and off from another window: the owner's own (as the desktop apps open it).
let owner;
async function setExtra(id, enabled) {
  const status = await owner.evaluate(([x, on]) => fetch(`/api/plugins/${x}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-OneThing-Client': 'web' }, body: JSON.stringify({ enabled: on }),
  }).then((r) => r.status), [id, enabled]);
  expect(status).toBe(200);
}

function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  return errors;
}

test.beforeAll(async ({ request, browser }, testInfo) => {
  const ownerKey = fs.readFileSync(path.join(process.env.ONETHING_E2E_DATA, 'owner-key'), 'utf8').trim();
  const launch = await (await request.post('/api/launch', { headers: { ...auth, 'X-OneThing-Owner-Key': ownerKey }, data: { return: '/' } })).json();
  owner = await (await browser.newContext()).newPage();
  await owner.goto(launch.url);
  const suffix = testInfo.retry ? ` ${testInfo.retry}` : '';
  group = await (await request.post('/api/groups', { headers: auth, data: { name: `Steady${suffix}` } })).json();
  other = await (await request.post('/api/groups', { headers: auth, data: { name: `Elsewhere${suffix}` } })).json();
});

test('Today: a slow answer for a group you already left doesn’t replace the one you’re on', async ({ page, request }) => {
  const errors = watchErrors(page);
  await add(request, 'Only in Steady');
  await add(request, 'Only in Elsewhere', other.id);
  await open(page, request);

  // Elsewhere's answer comes late: by then you're on Steady.
  await page.route(`**/api/dashboard?group=${other.id}*`, async (route) => {
    await new Promise((resolve) => { setTimeout(resolve, 1500); });
    await route.continue();
  });
  const late = page.waitForResponse((r) => r.url().includes(`group=${other.id}`));
  await page.locator('#tabs .tab', { hasText: other.name }).click();
  await page.locator('#tabs .tab', { hasText: group.name }).click();
  await expect(page.locator('#board')).toContainText('Only in Steady');
  await late;
  await page.waitForTimeout(300);

  await expect(page.locator('#tabs .tab', { hasText: group.name })).toHaveAttribute('aria-current', 'true');
  await expect(page.locator('#board')).toContainText('Only in Steady');
  await expect(page.locator('#board')).not.toContainText('Only in Elsewhere');
  expect(errors).toEqual([]);
});

test('Signing in with the token loads the extras too', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/');
  await expect(page.locator('#login')).toBeVisible();
  await page.locator('#login-token').fill(token);
  await page.getByRole('button', { name: 'Connect' }).click();

  await expect(page.locator('#board')).toBeVisible();
  await expect(page.locator('#plugin-buttons').getByRole('button', { name: 'Copy for Loop' })).toBeVisible();
  await expect(page.locator('#plugin-buttons').getByRole('button', { name: 'Ask AI' })).toBeVisible();
  expect(errors.filter((e) => !/401/.test(e))).toEqual([]);
});

test('An extra switched off in another window leaves this one too, without a reload', async ({ page, request }) => {
  await open(page, request);
  const loop = page.locator('#plugin-buttons').getByRole('button', { name: 'Copy for Loop' });
  await expect(loop).toBeVisible();
  try {
    await setExtra('loop', false);
    // The app checks every 15 seconds.
    await expect(loop).toBeHidden({ timeout: 20_000 });
    await setExtra('loop', true);
    await expect(loop).toBeVisible({ timeout: 20_000 });
  } finally {
    await setExtra('loop', true);
  }
});

test('With Plan off, a link to Plan opens Today', async ({ page, request }) => {
  try {
    await setExtra('plan', false);
    await open(page, request);
    await expect(page.locator('.nav-link[data-view="plan"]')).toBeHidden();
    await page.evaluate(() => { location.hash = '#/plan'; });
    await expect(page.locator('#view-title')).toHaveText('Today');
    await expect(page.locator('#view-plan')).toBeHidden();
    await expect(page).toHaveURL(/#\/today$/);
  } finally {
    await setExtra('plan', true);
  }
});

test('With the focus timer off, cards have no Focus button', async ({ page, request }) => {
  await add(request, 'Focus-free task');
  try {
    await setExtra('focus-timer', false);
    await open(page, request);
    await page.locator('#tabs .tab', { hasText: group.name }).click();
    await expect(page.locator('#board')).toContainText('Focus-free task');
    await expect(page.locator('#pomodoro')).toBeHidden();
    await expect(page.locator('#board').getByRole('button', { name: 'Focus', exact: true })).toHaveCount(0);
  } finally {
    await setExtra('focus-timer', true);
  }
  // Back on: the button comes back.
  await page.reload();
  await expect(page.locator('#board').getByRole('button', { name: 'Focus', exact: true }).first()).toBeVisible();
});

test('Task details: the keyboard goes in, stays in, and comes back when it closes', async ({ page, request }) => {
  const errors = watchErrors(page);
  await add(request, 'Keyboard round trip');
  await open(page, request);
  await page.locator('#tabs .tab', { hasText: group.name }).click();
  const opener = page.locator('#board').getByRole('button', { name: 'Keyboard round trip' }).first();
  await opener.focus();
  await page.keyboard.press('Enter');

  const drawer = page.getByRole('dialog', { name: 'Task details' });
  await expect(drawer).toBeVisible();
  await expect(drawer.locator('.title-input')).toBeFocused();

  // Tab from the last control goes round to the first one; Shift+Tab from the first goes to the last.
  const inside = () => page.evaluate(() => document.getElementById('drawer').contains(document.activeElement));
  const focusEnd = (last) => page.evaluate((end) => {
    const all = [...document.querySelectorAll('#drawer a[href], #drawer button, #drawer input, #drawer select, #drawer textarea, #drawer summary')]
      .filter((el) => !el.disabled && el.tabIndex >= 0 && el.getClientRects().length > 0);
    (end ? all[all.length - 1] : all[0]).focus();
  }, last);
  await focusEnd(false);
  await page.keyboard.press('Shift+Tab');
  expect(await inside()).toBe(true);
  await focusEnd(true);
  await page.keyboard.press('Tab');
  expect(await inside()).toBe(true);

  await page.keyboard.press('Escape');
  await expect(drawer).toBeHidden();
  await expect(page.locator('#board').getByRole('button', { name: 'Keyboard round trip' }).first()).toBeFocused();
  expect(errors).toEqual([]);
});

test('Groups: ⋯ renames a group and deletes it (its tasks move, after asking)', async ({ page, request }) => {
  const errands = await (await request.post('/api/groups', { headers: auth, data: { name: 'Errands' } })).json();
  const task = await add(request, 'Post the parcel', errands.id);
  await open(page, request);

  const more = page.getByRole('button', { name: 'More for Errands' });
  await more.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('menuitem', { name: 'Rename…' })).toBeFocused();
  page.once('dialog', (d) => d.accept('Chores'));
  await page.keyboard.press('Enter');
  await expect(page.locator('#tabs .tab', { hasText: 'Chores' })).toBeVisible();

  await page.getByRole('button', { name: 'More for Chores' }).click();
  let asked = '';
  page.once('dialog', (d) => { asked = d.message(); d.accept(); });
  await page.getByRole('menuitem', { name: 'Delete…' }).click();
  await expect(page.locator('#tabs .tab', { hasText: 'Chores' })).toHaveCount(0);
  expect(asked).toContain('none are lost');
  const moved = await (await request.get(`/api/items/${task.id}`, { headers: auth })).json();
  expect(moved.title).toBe('Post the parcel');
  expect(moved.groupId).not.toBe(errands.id);
});

test('No connection: one lasting notice, no error popping up again and again; it goes when One Thing answers', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request);
  await page.route('**/api/dashboard*', (route) => route.abort('connectionrefused'));
  // Coming back to the window checks at once (as the 15 s check does).
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.locator('#offline')).toBeVisible();
  await expect(page.locator('#offline')).toContainText('Can’t reach One Thing');
  await expect(page.locator('#toast')).toBeHidden();

  await page.unroute('**/api/dashboard*');
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.locator('#offline')).toBeHidden();
  expect(errors).toEqual([]);
});
