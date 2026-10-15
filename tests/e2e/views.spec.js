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
  // Not caused by this PR (seen on PR #28's CI at 23:50 UTC): "Started" defaults to 30 minutes ago, so 45 minutes ended
  // 15 minutes in the future. Run in the last quarter hour of a day, the entry crossed midnight and the Reports test
  // (range ending today) showed only the part before it ("40 min"). Starting an hour ago keeps it in the past.
  const started = new Date(Date.now() - 60 * 60_000);
  const local = new Date(started.getTime() - started.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
  await time.getByLabel('Started at').fill(local);
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
  await expect(screen.getByRole('button', { name: 'Start next focus now' })).toBeFocused();
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
  await page.keyboard.press('Alt+7');
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
  const menu = page.getByRole('dialog', { name: 'Tags and labels' });
  await expect(menu).toContainText('Labels');
  const deep = menu.getByRole('button', { name: /#deep work/ });
  await deep.click();
  await expect(page.locator('#filter')).toHaveValue('#"deep work"');
  // Shows as on right away, and keeps focus for the next pick; Esc goes back to the button.
  await expect(deep).toHaveAttribute('aria-pressed', 'true');
  await expect(deep).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(page.getByRole('button', { name: 'Filter by tag or label' })).toBeFocused();

  // The board follows the filter too.
  await page.locator('#nav-views').getByRole('link', { name: /Board/ }).click();
  const next = page.locator('.lane[data-column="next"]');
  await expect(next.locator('.kcard', { hasText: 'Write the deck' })).toBeVisible();
  await expect(next.locator('.kcard', { hasText: 'Order lunch' })).toHaveCount(0);

  // Picked again: out of the filter.
  await page.getByRole('button', { name: 'Filter by tag or label' }).click();
  await expect(deep).toHaveAttribute('aria-pressed', 'true');
  await deep.click();
  await expect(deep).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('#filter')).toHaveValue('');
  await expect(next.locator('.kcard', { hasText: 'Order lunch' })).toBeVisible();

  // The details show tags separated by commas (no quotes needed).
  await next.locator('.kcard', { hasText: 'Write the deck' }).getByRole('button', { name: /Open/ }).click();
  await expect(page.locator('#drawer').getByRole('textbox', { name: 'Tags' })).toHaveValue('deep work');
  await expect(page.locator('#drawer')).toContainText('Labels are colored categories');
  expect(errors).toEqual([]);
});test('Copy for Loop on a task: its details, subtasks, notes and pictures, from its details panel', async ({ page, request }) => {
  const errors = watchErrors(page);
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const task = await add(request, 'Ship the brochure');
  await add(request, 'Proof read', { parentId: task.id });
  await request.post(`/api/items/${task.id}/notes`, { headers: auth, data: { text: 'Printer booked for Friday' } });
  const launch = await (await request.post('/api/launch', { headers: auth, data: { return: `/?item=${task.id}` } })).json();
  await page.goto(launch.url);
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.title-input')).toHaveValue('Ship the brochure');

  // A screenshot pasted into the details.
  await page.evaluate(() => {
    const bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='), (c) => c.charCodeAt(0));
    const data = new DataTransfer();
    data.items.add(new File([bytes], 'image.png', { type: 'image/png' }));
    const box = document.querySelector('#drawer textarea[data-field="details"]');
    box.focus();
    box.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  await expect(drawer.locator('.embeds img')).toHaveCount(1);
  await expect(drawer.locator('#save-state')).toHaveText('Saved');

  await drawer.getByRole('button', { name: 'Copy for Loop' }).click();
  await expect(page.locator('#toast')).toContainText('with 1 picture');
  const copied = await page.evaluate(async () => {
    const [item] = await navigator.clipboard.read();
    return { types: item.types, html: await (await item.getType('text/html')).text(), text: await (await item.getType('text/plain')).text() };
  });
  expect(copied.text).toContain('## Ship the brochure');
  expect(copied.text).toContain('- [ ] Proof read');
  expect(copied.text).toMatch(/Printer booked for Friday/);
  expect(copied.html).toContain('src="data:image/png;base64,');

  expect(errors).toEqual([]);
});

test('Later: quick choices with times, a time typed in words, and after another task', async ({ page, request }) => {
  const errors = watchErrors(page);
  const keys = await add(request, 'Get the keys');
  const move = await add(request, 'Move in');
  const trip = await add(request, 'Plan the summer trip');

  // A time typed in words: the preview says when (or why not) before snoozing.
  let launch = await (await request.post('/api/launch', { headers: auth, data: { return: `/?item=${trip.id}` } })).json();
  await page.goto(launch.url);
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.title-input')).toHaveValue('Plan the summer trip');
  await drawer.getByRole('button', { name: 'Later', exact: true }).click();
  const menu = drawer.getByRole('menu');
  await expect(menu.getByRole('menuitem', { name: /^In a week/ })).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: /^Next Monday/ })).toContainText(/Mon/);
  await menu.getByRole('menuitem', { name: 'Pick a time…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Snooze Plan the summer trip until' });
  await expect(sheet).toBeVisible();
  const when = sheet.getByRole('textbox', { name: 'When' });
  await expect(when).toBeFocused();
  await when.fill('when pigs fly');
  await expect(sheet.locator('.later-preview.bad')).toContainText('tell when');
  await sheet.getByRole('button', { name: 'next week' }).click();
  await expect(sheet.locator('.later-preview.ok')).toContainText(/Mon.* 9:00 · in /);
  await when.press('Enter');
  await expect(sheet).toBeHidden();
  await expect(drawer.locator('.waiting-banner')).toContainText(/Snoozed until Mon.* 9:00/);
  const snoozed = await (await request.get(`/api/items/${trip.id}`, { headers: auth })).json();
  expect(new Date(snoozed.nextActionAt).getDay()).toBe(1);

  // After another task: find it, pick it; the task waits and says for what.
  launch = await (await request.post('/api/launch', { headers: auth, data: { return: `/?item=${move.id}` } })).json();
  await page.goto(launch.url);
  await expect(drawer.locator('.title-input')).toHaveValue('Move in');
  await drawer.getByRole('button', { name: 'Later', exact: true }).click();
  await drawer.getByRole('menuitem', { name: 'After another task…' }).click();
  const picker = page.getByRole('dialog', { name: 'Move in waits for' });
  await picker.getByRole('combobox').fill('keys');
  await picker.getByRole('option', { name: /Get the keys/ }).click();
  await expect(picker).toBeHidden();
  await expect(drawer.locator('.waiting-banner')).toContainText('Waits for “Get the keys”');
  await page.keyboard.press('Escape');

  // On Today it waits, saying for what; finishing that task brings it back.
  await page.locator('#tabs .tab', { hasText: group.name }).click();
  await page.locator('#sec-waiting > summary').click();
  await expect(page.locator('#sec-waiting .item', { hasText: 'Move in' }).locator('.meta')).toContainText('after “Get the keys”');
  await request.post(`/api/items/${keys.id}/complete`, { headers: auth });
  await page.reload();
  await expect(page.locator('#sec-waiting .item', { hasText: 'Move in' })).toHaveCount(0);
  const back = await (await request.get(`/api/items/${move.id}`, { headers: auth })).json();
  expect(back.state).toBe('actionable');
  expect(errors).toEqual([]);
});

test('Break: start the next focus from the break, or when the break is over', async ({ page, request }) => {
  const errors = watchErrors(page);
  await page.clock.install();
  const task = await add(request, 'Second deep work block');
  await request.post('/api/pomodoro/start', { headers: auth, data: { itemId: task.id } });
  // Only the browser's clock moves here, so the server is asked but not obeyed: the request is what's checked
  // (the server side is covered by the API tests).
  const asked = [];
  await page.route('**/api/pomodoro/next', async (route) => { asked.push(route.request().postDataJSON()); await route.fulfill({ json: {} }); });
  await open(page, request);

  // During the break: start the next one now, on the same task.
  await page.clock.fastForward('25:05');
  const screen = page.locator('#break');
  await expect(screen.getByRole('heading')).toHaveText(/Time for a/);
  await expect(screen.locator('.break-next')).toHaveText('Next: Second deep work block');
  await page.clock.fastForward('00:02');
  await screen.getByRole('button', { name: 'Start next focus now' }).click();
  await expect(screen).toBeHidden();
  expect(asked).toHaveLength(1);

  // A break hidden with "I'm taking it" asks when it's over (the server still says focus: nothing really started).
  await page.reload();
  await page.clock.fastForward('00:02');
  await expect(screen).toBeHidden(); // the one just answered stays away…
  await page.evaluate(() => sessionStorage.clear());
  await page.reload();
  await page.clock.fastForward('00:02');
  await expect(screen.getByRole('heading')).toHaveText(/Time for a/); // …a fresh window shows it again
  await screen.getByRole('button', { name: 'I’m taking it' }).click({ delay: 900 });
  await expect(screen).toBeHidden();
  await page.clock.fastForward('05:00');
  await expect(screen.getByRole('heading')).toHaveText('Break’s over');
  await expect(screen.locator('.break-next')).toHaveText('Next: Second deep work block');
  await page.clock.fastForward('00:02');
  await screen.getByRole('button', { name: 'Not now' }).click();
  await expect(screen).toBeHidden();
  await page.clock.fastForward('00:30');
  await expect(screen).toBeHidden();
  expect(asked).toHaveLength(1);
  expect(errors).toEqual([]);
});
test('Board: dragging the background moves around it; dragging a card still moves the card', async ({ page, request }) => {
  const errors = watchErrors(page);
  await add(request, 'Pan around', { stage: 'next' });
  await open(page, request, '#/board');
  // Narrow, so the columns don't all fit.
  await page.setViewportSize({ width: 700, height: 600 });
  const board = page.locator('#view-board .board');
  await expect(board.locator('.kcard', { hasText: 'Pan around' })).toBeVisible();
  const wide = await board.evaluate((b) => b.scrollWidth > b.clientWidth);
  expect(wide).toBe(true);

  // Press on the first column's header (not a card, a button or a field) and drag left: the board scrolls right.
  const head = board.locator('.lane .column-head').first();
  const box = await head.boundingBox();
  const start = { x: box.x + box.width - 40, y: box.y + box.height / 2 };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x - 120, start.y, { steps: 6 });
  await expect(board).toHaveClass(/panning/);
  await page.mouse.up();
  await expect(board).not.toHaveClass(/panning/);
  expect(await board.evaluate((b) => b.scrollLeft)).toBeGreaterThan(60);

  // Pressing on a card doesn't pan (cards drag to move them).
  const card = await board.locator('.kcard', { hasText: 'Pan around' }).boundingBox();
  await page.mouse.move(card.x + 20, card.y + 10);
  await page.mouse.down();
  await page.mouse.move(card.x + 20, card.y + 40, { steps: 4 });
  await expect(board).not.toHaveClass(/panning/);
  await page.mouse.up();
  await page.setViewportSize({ width: 1280, height: 720 });
  expect(errors).toEqual([]);
});

test('Today: what got done today shows with a cheer, where it belongs, and can be reopened', async ({ page, request }) => {
  const errors = watchErrors(page);
  const parent = await add(request, 'Move house');
  const step = await add(request, 'Pack the kitchen', { parentId: parent.id });
  await request.post(`/api/items/${step.id}/complete`, { headers: auth });
  await open(page, request);
  const done = page.locator('#sec-done');
  await expect(done.locator('.cheer')).toHaveText(/done today|Nice start/);
  const row = done.locator('.done-row', { hasText: 'Pack the kitchen' });
  await expect(row).toContainText('Move house');
  await row.hover();
  await row.getByRole('button', { name: 'Reopen Pack the kitchen' }).click();
  await expect(done.locator('.done-row', { hasText: 'Pack the kitchen' })).toHaveCount(0);
  const item = await (await request.get(`/api/items/${step.id}`, { headers: auth })).json();
  expect(item.completedAt).toBeNull();
  expect(errors).toEqual([]);
});

test('Where a task lives: add it to another group, see its parent and siblings, move it under another task, show it in the tree', async ({ page, request }) => {
  const errors = watchErrors(page);
  const groups = await (await request.get('/api/groups', { headers: auth })).json();
  const other = groups.find((g) => g.id !== group.id);
  await open(page, request);

  // Quick add straight into another group (the tab stays where it is).
  await page.locator('#capture-group').selectOption(other.id);
  await page.locator('#capture-input').fill('Renew passport');
  await page.locator('#capture-input').press('Enter');
  await expect(page.locator('#toast')).toContainText(`Added to ${other.name}`);
  const all = await (await request.get('/api/items', { headers: auth })).json();
  expect(all.find((i) => i.title === 'Renew passport').groupId).toBe(other.id);

  // A subtask shows what it's part of, with the steps next to it.
  const trip = await add(request, 'Plan the island trip');
  const flights = await add(request, 'Book flights', { parentId: trip.id });
  await add(request, 'Book hotel near the beach', { parentId: trip.id });
  const loose = await add(request, 'Pack sunscreen');
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, flights.id);
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.where-path')).toContainText('Plan the island trip');
  await expect(drawer.locator('.part-of')).toContainText('0/2 done');
  await expect(drawer.locator('.siblings')).toContainText('Book hotel near the beach');

  // Move a loose task under the trip.
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, loose.id);
  await drawer.getByRole('button', { name: 'Move…' }).click();
  const dialog = page.locator('.move-dialog');
  await dialog.getByRole('searchbox', { name: 'Find a task' }).fill('island');
  await dialog.getByRole('option', { name: /Plan the island trip/ }).click();
  await dialog.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(drawer.locator('.where-path')).toContainText('Plan the island trip');
  const moved = await (await request.get(`/api/items/${loose.id}`, { headers: auth })).json();
  expect(moved.parentId).toBe(trip.id);

  // And out again, to the top level (Esc first: it closes the dialog, not the task behind it).
  await drawer.getByRole('button', { name: 'Move…' }).click();
  await page.keyboard.press('Escape');
  await expect(page.locator('.move-dialog')).toHaveCount(0);
  await expect(drawer).toBeVisible();
  await drawer.getByRole('button', { name: 'Move…' }).click();
  // The keyboard picks "On its own": Home, then Enter moves it.
  await page.locator('.move-dialog [role="option"][tabindex="0"]').focus();
  await page.keyboard.press('Home');
  await expect(page.locator('.move-dialog [role="option"]').first()).toBeFocused();
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await (await request.get(`/api/items/${loose.id}`, { headers: auth })).json()).parentId).toBeNull();

  // Show in tree: the tree opens with the subtask's parent expanded and the subtask highlighted.
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, flights.id);
  await expect(drawer.locator('.title-input')).toHaveValue('Book flights');
  await drawer.getByRole('button', { name: 'Show in tree' }).click();
  await expect(page).toHaveURL(/#\/tasks$/);
  await expect(page.locator(`#view-tasks .orow[data-id="${flights.id}"]`)).toBeVisible();
  await expect(page.locator(`#view-tasks .orow[data-id="${flights.id}"]`)).toBeFocused();
  expect(errors).toEqual([]);
});

test('Look: a task gets an icon and a color from its details, cards show them, and labels can be recolored', async ({ page, request }) => {
  const errors = watchErrors(page);
  const task = await add(request, 'Ship the newsletter', { labels: ['Writing'] });
  await open(page, request);
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, task.id);
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.title-input')).toHaveValue('Ship the newsletter');

  // Icon: from the grid.
  await drawer.getByRole('button', { name: 'Add an icon' }).click();
  await page.locator('.look-dialog').getByRole('button', { name: '🚀' }).click();
  await expect(drawer.getByRole('button', { name: 'Icon 🚀: change it' })).toBeVisible();

  // Color: a swatch; the card shows both.
  await drawer.getByRole('button', { name: '#10b981' }).click();
  await expect.poll(async () => (await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).color).toBe('#10b981');
  await page.keyboard.press('Escape');
  const card = page.locator('#sec-now .item', { hasText: 'Ship the newsletter' }).or(page.locator('.focus-card', { hasText: 'Ship the newsletter' })).first();
  await expect(card.locator('.task-icon')).toHaveText('🚀');
  await expect(card).toHaveClass(/has-accent/);

  // Auto: back to its label's color.
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, task.id);
  await drawer.getByRole('button', { name: /Auto/ }).click();
  await expect.poll(async () => (await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).color ?? null).toBeNull();

  // Settings > Labels: a new color for the label.
  await page.keyboard.press('Escape');
  await page.evaluate(() => { location.hash = '#/settings'; });
  const color = page.getByLabel('Color of Writing');
  await color.evaluate((el) => { el.value = '#ec4899'; el.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(async () => (await (await request.get('/api/labels', { headers: auth })).json()).find((l) => l.name === 'Writing')?.color).toBe('#ec4899');
  expect(errors).toEqual([]);
});

test('Repeat: a task set to repeat from its details moves on when done, counts as done today, and can be put back', async ({ page, request }) => {
  const errors = watchErrors(page);
  const task = await add(request, 'Water the plants');
  await open(page, request);
  await page.evaluate((id) => { location.hash = `#/task/${id}`; }, task.id);
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.title-input')).toHaveValue('Water the plants');

  // A rule that isn't understood says why and isn't saved.
  const rule = drawer.getByRole('combobox', { name: 'Repeat' });
  await rule.fill('now and then');
  await rule.press('Enter');
  await expect.poll(() => rule.evaluate((el) => el.validationMessage)).toContain('every weekday');
  expect((await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).repeat ?? null).toBeNull();

  await rule.fill('Every 2 days');
  await rule.press('Enter');
  await expect(drawer.getByRole('combobox', { name: 'Repeat' })).toHaveValue('every 2 days');
  await page.keyboard.press('Escape');

  // The card shows it repeats; done moves it on (it waits for its next time) and it shows in Done today.
  const card = page.locator('#sec-now .item', { hasText: 'Water the plants' }).or(page.locator('.focus-card', { hasText: 'Water the plants' })).first();
  await expect(card).toContainText('🔁');
  await request.post(`/api/items/${task.id}/complete`, { headers: auth });
  await page.reload();
  const done = page.locator('#sec-done');
  const row = done.locator('.done-row', { hasText: 'Water the plants' });
  await expect(row).toBeVisible();
  const moved = await (await request.get(`/api/items/${task.id}`, { headers: auth })).json();
  expect(moved.completedAt).toBeNull();
  expect(new Date(moved.nextActionAt).getTime()).toBeGreaterThan(Date.now());

  // Not done after all: back now.
  await row.hover();
  await row.getByRole('button', { name: 'Reopen Water the plants' }).click();
  await expect(done.locator('.done-row', { hasText: 'Water the plants' })).toHaveCount(0);
  await expect.poll(async () => (await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).nextActionAt ?? null).toBeNull();
  expect(errors).toEqual([]);
});

test('Hooks: Settings shows where hooks live, a sample to start from, and can try an event', async ({ page, request }) => {
  const errors = watchErrors(page);
  await open(page, request);
  await page.evaluate(() => { location.hash = '#/settings'; });
  const hooks = page.locator('#settings-hooks');
  await expect(hooks).toContainText('hooks.json');
  await expect(hooks.locator('.hook-sample')).toContainText('"taskCompleted"');
  await hooks.getByRole('combobox', { name: 'Event to try' }).selectOption('reminderDue');
  await hooks.getByRole('button', { name: 'Try it' }).click();
  await expect(page.locator('#toast')).toContainText('No hook runs for that event');
  expect(errors).toEqual([]);
});

test('Plan: drag a task onto a time, see the block, open it, and take it off the plan from its details', async ({ page, request }) => {
  const errors = watchErrors(page);
  const task = await add(request, 'Write the quarterly report');
  await open(page, request);
  await page.evaluate(() => { location.hash = '#/plan'; });
  const plan = page.locator('#view-plan');
  await plan.getByRole('group', { name: 'Show' }).getByRole('button', { name: 'Day' }).click();
  await expect(plan.locator('.plan-day')).toHaveCount(1);
  // Tomorrow: a block still to come (one already over goes back to "Not planned").
  await plan.getByRole('button', { name: 'Next day' }).click();
  await expect(plan.locator('.plan-day.today')).toHaveCount(0);

  const item = plan.locator('.plan-task', { hasText: 'Write the quarterly report' });
  await item.dragTo(plan.locator('.plan-day'), { targetPosition: { x: 40, y: 120 } });
  await expect.poll(async () => (await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).plannedAt ?? null).not.toBeNull();
  const block = plan.locator('.plan-block', { hasText: 'Write the quarterly report' });
  await expect(block).toBeVisible();
  await expect(plan.locator('.plan-task', { hasText: 'Write the quarterly report' })).toHaveCount(0);

  await block.click();
  const drawer = page.locator('#drawer');
  await expect(drawer.getByLabel('Planned start')).not.toHaveValue('');
  await drawer.getByRole('button', { name: 'Off the plan' }).click();
  await expect.poll(async () => (await (await request.get(`/api/items/${task.id}`, { headers: auth })).json()).plannedAt ?? null).toBeNull();
  expect(errors).toEqual([]);
});
