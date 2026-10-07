// The full-window views: Board, Tasks (outline), Done, Reports, the full-size task, the timer, the command palette,
// and the break screen. Runs after dashboard.spec.js (it adds a group, which that file's tab check doesn't expect).
import { test, expect } from '@playwright/test';

const token = 'e2e-token-0123456789abcdefghijklmnop';
const auth = { Authorization: `Bearer ${token}` };

test.describe.configure({ mode: 'serial' });

let group;

async function open(page, request, hash = '') {
  const launch = await (await request.post('/api/launch', { headers: auth, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await expect(page.locator('#board')).toBeVisible();
  // Only this file's tasks: its own group.
  await page.locator('#tabs .tab', { hasText: group.name }).click();
  if (hash) await page.evaluate((h) => { location.hash = h; }, hash);
}

const add = async (request, title, extra = {}) =>
  (await (await request.post('/api/items', { headers: auth, data: { title, groupId: group.id, ...extra } })).json());

const tree = async (request) => (await (await request.get(`/api/tree?group=${group.id}`, { headers: auth })).json());

function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  return errors;
}

// Each attempt (CI retries the whole serial file) gets its own group, so it never sees the last attempt's tasks.
test.beforeAll(async ({ request }, testInfo) => {
  const name = testInfo.retry ? `Again ${testInfo.retry}` : 'Views';
  group = await (await request.post('/api/groups', { headers: auth, data: { name } })).json();
});

test('Board: cards move between columns by drag and drop and Alt+arrows; Doing shows its limit', async ({ page, request }) => {
  const errors = watchErrors(page);
  await add(request, 'Plan the trip', { stage: 'inbox' });
  await add(request, 'Book hotel', { stage: 'next' });
  await open(page, request);
  await page.locator('#nav-views').getByRole('link', { name: /Board/ }).click();
  await expect(page).toHaveURL(/#\/board$/);
  await expect(page.locator('#view-title')).toHaveText('Board');

  const lane = (id) => page.locator(`.lane[data-column="${id}"]`);
  await expect(lane('inbox').locator('.kcard')).toHaveText([/Plan the trip/]);
  await expect(lane('next').locator('.kcard')).toHaveText([/Book hotel/]);

  // Typing in a column's box adds the card there.
  await lane('doing').getByRole('textbox', { name: 'Add a task to Doing' }).fill('Pack bags');
  await lane('doing').getByRole('textbox', { name: 'Add a task to Doing' }).press('Enter');
  await expect(lane('doing').locator('.kcard')).toHaveText([/Pack bags/]);
  await expect(lane('doing').locator('.column-head')).toContainText('1/3');

  // Drag and drop: inbox → next, above "Book hotel".
  await lane('inbox').locator('.kcard', { hasText: 'Plan the trip' }).dragTo(lane('next').locator('.kcard', { hasText: 'Book hotel' }), { targetPosition: { x: 20, y: 5 } });
  await expect(lane('next').locator('.kcard')).toHaveText([/Plan the trip/, /Book hotel/]);
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Plan the trip')?.stage).toBe('next');

  // Keyboard: Alt+→ moves the focused card to the next column (keeping its place in the shared order: it was added
  // before "Pack bags"), and focus stays on it.
  await lane('next').locator('.kcard', { hasText: 'Book hotel' }).focus();
  await page.keyboard.press('Alt+ArrowRight');
  await expect(lane('doing').locator('.kcard')).toHaveText([/Book hotel/, /Pack bags/]);
  await expect(page.locator('.kcard:focus')).toHaveText(/Book hotel/);
  await expect(lane('doing').locator('.column-head')).toContainText('2/3');

  // Into Done: the task is finished.
  await page.keyboard.press('Alt+ArrowRight');
  await expect(lane('done').locator('.kcard')).toHaveText([/Book hotel/]);
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Book hotel')?.done).toBe(true);

  // Two quick presses move a card two columns (each move starts from where the last one left it).
  await lane('inbox').getByRole('textbox', { name: 'Add a task to Inbox' }).fill('Quick mover');
  await lane('inbox').getByRole('textbox', { name: 'Add a task to Inbox' }).press('Enter');
  await lane('inbox').locator('.kcard', { hasText: 'Quick mover' }).focus();
  await page.keyboard.press('Alt+ArrowRight');
  await page.keyboard.press('Alt+ArrowRight');
  await expect(lane('doing').locator('.kcard', { hasText: 'Quick mover' })).toHaveCount(1);
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Quick mover')?.stage).toBe('doing');
  expect(errors).toEqual([]);
});

test('Board: the timer starts from a card, shows in the top bar, and stops there', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request, '#/board');
  const card = page.locator('.lane[data-column="next"] .kcard', { hasText: 'Plan the trip' });
  await card.getByRole('button', { name: 'Start timing Plan the trip' }).click();

  // Starting the timer moves the card to Doing.
  await expect(page.locator('.lane[data-column="doing"] .kcard.is-timing')).toHaveText(/Plan the trip/);
  const chip = page.locator('#timer');
  await expect(chip).toBeVisible();
  await expect(chip).toContainText('Plan the trip');
  await expect(chip.locator('.timer-clock')).toHaveText(/^0:0\d$/);

  await chip.getByRole('button', { name: 'Stop timing Plan the trip' }).click();
  await expect(chip).toBeHidden();
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Plan the trip')?.timing).toBe(false);
  expect(errors).toEqual([]);
});

test('Tasks: write an outline with the keyboard (add, nest, rename, finish) and drag rows', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request, '#/tasks');
  await expect(page.locator('#view-title')).toHaveText('Tasks');
  const outline = page.locator('#view-tasks .outline');
  const rowOf = (title) => outline.locator(`.orow[data-id][aria-label="${title}"]`);
  await expect(outline.locator('.orow[data-id]').first()).toBeVisible();

  // The New task button, then Enter keeps adding rows below.
  await page.getByRole('button', { name: 'New task' }).click();
  const editor = outline.getByRole('textbox', { name: 'New task' });
  await editor.fill('Launch website');
  await editor.press('Enter');
  await expect(rowOf('Launch website')).toHaveCount(1);
  await outline.getByRole('textbox', { name: 'New task' }).fill('Write the copy');
  await outline.getByRole('textbox', { name: 'New task' }).press('Enter');
  await expect(rowOf('Write the copy')).toHaveCount(1);
  await outline.getByRole('textbox', { name: 'New task' }).press('Escape');

  // Tab nests "Write the copy" under "Launch website".
  const copyRow = () => rowOf('Write the copy');
  await copyRow().focus();
  await page.keyboard.press('Tab');
  await expect(copyRow()).toHaveAttribute('aria-level', '2');
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Launch website')?.children.map((c) => c.title)).toEqual(['Write the copy']);

  // Enter renames in place.
  await expect(copyRow()).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.type('Write the landing copy');
  await page.keyboard.press('Escape'); // Escape cancels...
  await expect(copyRow().locator('.otitle')).toHaveValue('Write the copy');
  await page.keyboard.press('Enter');
  const title = copyRow().locator('.otitle');
  await title.fill('Write the landing copy');
  await title.press('Tab'); // ...leaving the field saves.
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Launch website')?.children[0]?.title).toBe('Write the landing copy');

  // Shift+Tab takes it back out to the top level, right after its old parent.
  const landing = rowOf('Write the landing copy');
  await landing.focus();
  await page.keyboard.press('Shift+Tab');
  await expect(landing).toHaveAttribute('aria-level', '1');
  await expect.poll(async () => (await tree(request)).map((n) => n.title).filter((t) => /Launch website|landing copy/.test(t))).toEqual(['Launch website', 'Write the landing copy']);

  // Drag and drop onto the middle of a row nests it there.
  await landing.dragTo(rowOf('Launch website'));
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Launch website')?.children.map((c) => c.title)).toEqual(['Write the landing copy']);

  // Space finishes the focused task.
  const nested = rowOf('Write the landing copy');
  await nested.focus();
  await page.keyboard.press('Space');
  await expect.poll(async () => (await tree(request)).find((n) => n.title === 'Launch website')?.children[0]?.done).toBe(true);
  // Finished tasks are hidden: the keyboard moves to the row above, not off the page.
  await expect(rowOf('Launch website')).toBeFocused();

  // The tree is one stop for Tab; Escape leaves it without changing anything.
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'New task' })).toBeFocused();
  await expect(outline.locator('.orow[data-id][tabindex="0"]')).toHaveCount(1);
  expect(errors).toEqual([]);
});

test('Full-size task: opens from a link, logs time by hand, and closes back to the view', async ({ page, request }) => {
  const errors = watchErrors(page);
  const task = await add(request, 'Quarterly taxes');
  await open(page, request, '#/board');
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, task.id);
  const drawer = page.locator('#drawer');
  await expect(drawer).toHaveClass(/full/);
  await expect(drawer.locator('.title-input')).toHaveValue('Quarterly taxes');

  const time = drawer.locator('.time-section');
  await time.getByText('Add time by hand').click();
  await time.getByRole('spinbutton', { name: 'Minutes' }).fill('45'); // any whole number of minutes
  await time.getByRole('button', { name: 'Add' }).click();
  await expect(time.locator('.time-total')).toContainText('45 min');
  await expect(time.locator('.time-list li')).toHaveCount(1);

  // Back closes it (it had its own address).
  await page.goBack();
  await expect(drawer).toBeHidden();
  await expect(page).toHaveURL(/#\/board$/);

  // A link opens just that task full size; other tasks still open in the side panel.
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, task.id);
  await expect(drawer).toHaveClass(/full/);
  await page.keyboard.press('Alt+2'); // (full size covers the navigation; Alt+number still switches views)
  await expect(drawer).toBeHidden();
  await page.locator('.kcard', { hasText: 'Quarterly taxes' }).click();
  await expect(drawer).toBeVisible();
  await expect(drawer).not.toHaveClass(/full/);
  await expect(page).toHaveURL(/#\/board$/);

  // Full size and back to the side panel, then closed: the address follows.
  await drawer.getByRole('button', { name: 'Full size' }).click();
  await expect(drawer).toHaveClass(/full/);
  await expect(page).toHaveURL(new RegExp(`#/task/${task.id}$`));
  await drawer.getByRole('button', { name: 'Smaller' }).click();
  await expect(drawer).not.toHaveClass(/full/);
  await drawer.getByRole('button', { name: 'Close' }).click();
  await expect(drawer).toBeHidden();
  await expect(page).toHaveURL(/#\/board$/);
  expect(errors).toEqual([]);
});

test('Done: finished tasks can be archived, found in Archive, and brought back', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request, '#/done');
  const view = page.locator('#view-done');
  await expect(view.locator('.done-row', { hasText: 'Book hotel' })).toBeVisible();

  await view.locator('.done-row', { hasText: 'Book hotel' }).getByRole('button', { name: 'Archive' }).click();
  await expect(view.locator('.done-row', { hasText: 'Book hotel' })).toHaveCount(0);

  await view.getByRole('tab', { name: /Archive/ }).click();
  const archived = view.locator('.done-row', { hasText: 'Book hotel' });
  await expect(archived).toBeVisible();
  await archived.getByRole('button', { name: 'Unarchive' }).click();
  await expect(archived).toHaveCount(0);
  await view.getByRole('tab', { name: /Done/ }).click();
  await expect(view.locator('.done-row', { hasText: 'Book hotel' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('Reports: totals, charts, and the timeline show the tracked time', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request, '#/reports');
  const view = page.locator('#view-reports');
  await expect(view.locator('.kpi').first()).toBeVisible();
  await expect(view.locator('.kpi-value').first()).not.toHaveText('0 s');
  await expect(view.locator('.report-card h3')).toContainText(['Where the time went']);
  await expect(view.locator('.hbars li', { hasText: 'Quarterly taxes' })).toContainText('45 min');
  await expect(view.locator('svg').first()).toBeVisible();
  await expect(view.locator('.report-card.wide')).toContainText('Quarterly taxes');

  await view.getByRole('button', { name: '3 months' }).click();
  await expect(view.getByRole('button', { name: '3 months' })).toHaveAttribute('aria-pressed', 'true');
  await expect(view.locator('.hbars li', { hasText: 'Quarterly taxes' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('Command palette: Ctrl+K finds a task and runs commands', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request);
  await page.keyboard.press('Control+k');
  const palette = page.locator('#palette');
  await expect(palette).toBeVisible();
  // Typed fast and Enter right away: the existing task opens (no copy is added).
  await page.keyboard.type('Quarterly taxes');
  await page.keyboard.press('Enter');
  await expect(palette).toBeHidden();
  await expect(page.locator('#drawer .title-input')).toHaveValue('Quarterly taxes');
  expect((await tree(request)).filter((n) => n.title === 'Quarterly taxes')).toHaveLength(1);
  await page.keyboard.press('Escape');
  await expect(page.locator('#drawer')).toBeHidden();

  await page.keyboard.press('Control+k');
  await page.keyboard.type('go to rep');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#\/reports$/);

  // Alt+number switches views too.
  await page.keyboard.press('Alt+3');
  await expect(page).toHaveURL(/#\/tasks$/);
  expect(errors).toEqual([]);
});

test('Break: when a focus session ends, a full-screen break shows and can be skipped', async ({ page, request }) => {
  const errors = watchErrors(page);
  await page.clock.install();
  const task = await add(request, 'Deep work block');
  await request.post('/api/pomodoro/start', { headers: auth, data: { itemId: task.id } });
  await open(page, request);

  // 25 minutes later in the browser (the server's clock doesn't move: it still says "focus").
  await page.clock.fastForward('25:05');
  const screen = page.locator('#break');
  await expect(screen).toBeVisible();
  await expect(screen.getByRole('heading')).toHaveText(/Time for a/);

  // A key still being typed when it appears doesn't answer it, and nothing behind it reacts.
  await expect(screen.locator('.break-card')).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.press('n');
  await expect(screen).toBeVisible();
  await expect(page.locator('#capture-input')).not.toBeFocused();

  // Focus stays inside it.
  await page.keyboard.press('Tab');
  await expect(screen.getByRole('button', { name: 'I’m taking it' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(screen.getByRole('button', { name: 'Skip the break' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(screen.getByRole('button', { name: 'I’m taking it' })).toBeFocused();

  // Skipped, it stays away (the server-side catch-up is covered by the core tests).
  await page.clock.fastForward('00:02');
  await screen.getByRole('button', { name: 'Skip the break' }).click();
  await expect(screen).toBeHidden();
  await expect.poll(async () => (await (await request.get('/api/dashboard', { headers: auth })).json()).pomodoro.phase).not.toBe('focus');
  await page.clock.fastForward('00:20');
  await expect(screen).toBeHidden();
  expect(errors).toEqual([]);
});

test('Desktop app window: #/ask opens the chat over the view, and the desktop breaks turn the web one off', async ({ page, request }) => {
  const errors = watchErrors(page);
  await page.addInitScript(() => { window.__ttNativeBreaks = true; });
  await page.clock.install();
  const task = await add(request, 'Native break block');
  await request.post('/api/pomodoro/start', { headers: auth, data: { itemId: task.id } });
  await open(page, request, '#/board');
  await expect(page.locator('#view-title')).toHaveText('Board');

  await page.evaluate(() => { location.hash = '#/ask'; });
  await expect(page.locator('#drawer .chat')).toBeVisible();
  await expect(page).toHaveURL(/#\/board$/);
  await expect(page.locator('#view-title')).toHaveText('Board');

  // The desktop covers every screen itself: no second break inside the window.
  await page.clock.fastForward('25:05');
  await page.clock.fastForward('00:02');
  await expect(page.locator('#break')).toBeHidden();
  await request.post('/api/pomodoro/reset', { headers: auth });
  expect(errors).toEqual([]);
});
test('Copy for Loop: a group as a checklist to paste into a Loop page', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request);
  await page.getByRole('button', { name: 'Copy for Loop' }).click();
  const out = page.getByRole('textbox', { name: 'Checklist to paste' });
  await expect(out).toHaveValue(new RegExp(`^## ${group.name}\\n`));
  await expect(out).toHaveValue(/- \[ \] Plan the trip/);
  await expect(out).not.toHaveValue(/Book hotel/);
  await page.getByRole('checkbox', { name: 'Include finished' }).check();
  await expect(out).toHaveValue(/- \[x\] Book hotel/);
  expect(errors).toEqual([]);
});
test('Settings: light or dark for every window, and connected apps switch on at once', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request);
  await page.keyboard.press('Alt+6');
  await expect(page).toHaveURL(/#\/settings$/);
  const settings = page.locator('#view-settings');

  await settings.getByRole('button', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect.poll(async () => (await (await request.get('/api/settings', { headers: auth })).json()).theme).toBe('dark');
  // Kept (no flash of the other theme on the next load).
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.locator('#view-settings').getByRole('button', { name: 'Light' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.locator('#view-settings').getByRole('button', { name: 'System' }).click();
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', /./);

  // Notion: switched on here, its setup shows right away (no restart).
  const use = page.locator('#view-settings').getByRole('checkbox', { name: 'Use Notion' });
  await use.check();
  await expect(page.locator('#view-settings').getByRole('textbox', { name: 'Notion integration token' })).toBeVisible();
  await page.locator('#view-settings').getByRole('checkbox', { name: 'Use Notion' }).uncheck();
  await expect(page.locator('#view-settings').getByRole('textbox', { name: 'Notion integration token' })).toHaveCount(0);
  expect(errors).toEqual([]);
});test('Tags and labels: several-word tags, and the picker by the search box filters every view', async ({ page, request }) => {
  const errors = watchErrors(page);
  await add(request, 'Write the deck', { tags: ['deep work'], stage: 'next' });
  await add(request, 'Order lunch', { stage: 'next' });
  await open(page, request);

  await page.getByRole('button', { name: 'Filter by tag or label' }).click();
  const menu = page.getByRole('menu', { name: 'Tags and labels' });
  await expect(menu).toContainText('Labels');
  await menu.getByRole('menuitemcheckbox', { name: /#deep work/ }).click();
  await expect(page.locator('#filter')).toHaveValue('#"deep work"');

  // The board follows the filter too.
  await page.locator('#nav-views').getByRole('link', { name: /Board/ }).click();
  const next = page.locator('.lane[data-column="next"]');
  await expect(next.locator('.kcard', { hasText: 'Write the deck' })).toBeVisible();
  await expect(next.locator('.kcard', { hasText: 'Order lunch' })).toHaveCount(0);

  // Picked again: out of the filter.
  await page.getByRole('button', { name: 'Filter by tag or label' }).click();
  await expect(menu.getByRole('menuitemcheckbox', { name: /#deep work/ })).toHaveAttribute('aria-checked', 'true');
  await menu.getByRole('menuitemcheckbox', { name: /#deep work/ }).click();
  await expect(page.locator('#filter')).toHaveValue('');
  await expect(next.locator('.kcard', { hasText: 'Order lunch' })).toBeVisible();

  // The details show the tag as typed, quotes and all.
  await next.locator('.kcard', { hasText: 'Write the deck' }).getByRole('button', { name: /Open/ }).click();
  await expect(page.locator('#drawer').getByRole('textbox', { name: 'Tags' })).toHaveValue('#"deep work"');
  await expect(page.locator('#drawer')).toContainText('Labels are colored categories');
  expect(errors).toEqual([]);
});