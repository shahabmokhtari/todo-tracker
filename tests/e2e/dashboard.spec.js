import { test, expect } from '@playwright/test';

const token = 'e2e-token-0123456789abcdefghijklmnop';

test.describe.configure({ mode: 'serial' });

test('dashboard: capture, rollout steps, gating, groups, and report', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));

  // Unauthenticated visitors get the connect screen.
  await page.goto('/');
  await expect(page.locator('#login')).toBeVisible();

  // The desktop "Open dashboard" link is a single-use launch code (the API token never appears in URLs).
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/' } })).json();
  expect(launch.url).not.toContain(token);
  await page.goto(launch.url);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.locator('#board')).toBeVisible();
  await expect(page.locator('#tabs .tab')).toHaveText([/All/, /Work/, /Personal/, '']);
  errors.length = 0; // the 401 from the unauthenticated visit above is expected

  // Quick capture with priority syntax becomes the focus card.
  await page.fill('#capture-input', 'Roll out feature X !!');
  await page.press('#capture-input', 'Enter');
  await expect(page.locator('.focus-title')).toHaveText('Roll out feature X');

  // Add gated rollout steps from the task drawer.
  await page.click('.focus-title');
  const drawer = page.locator('#drawer');
  await expect(drawer).toBeVisible();
  await drawer.locator('summary', { hasText: 'rollout steps' }).click();
  await drawer.locator('textarea[placeholder^="Rollout steps"]').fill('Ring 0\nRing 1');
  await drawer.getByRole('button', { name: 'Add steps' }).click();
  await expect(drawer.locator('.subtasks li')).toHaveCount(2);
  await page.keyboard.press('Escape');

  await expect(page.locator('.focus-title')).toHaveText('Ring 0');
  await expect(page.locator('.focus-card .focus-sub')).toContainText('Step 1 of 2');
  // Hidden panels must not block clicks or render (regression: CSS display overrode [hidden]).
  await expect(page.locator('#drawer')).toBeHidden();
  await expect(page.locator('.menu').first()).toBeHidden();
  await expect(page.locator('.inline-note').first()).toBeHidden();

  // Log a note, then finish the step: the next step is gated for 24h and waits.
  await page.locator('.focus-card').getByRole('button', { name: 'Note', exact: true }).click();
  await page.locator('.focus-card .inline-note input').fill('Ring 0 healthy');
  await page.locator('.focus-card .inline-note button').click();
  await page.locator('#sec-notes > summary').click();
  await expect(page.locator('#sec-notes .note').first()).toContainText('Ring 0 healthy');

  await page.locator('.focus-card').getByRole('button', { name: 'Done', exact: true }).click();
  await expect(page.locator('.focus-card')).toContainText('Nothing is due');
  await page.locator('#sec-waiting > summary').click();
  await expect(page.locator('#sec-waiting .item')).toContainText('Ring 1');
  await expect(page.locator('#sec-waiting .item .meta')).toContainText('back in 1d');
  await expect(page.locator('#sec-overview .item')).toContainText('1/2 done');

  // Groups behave like tabs.
  await page.locator('#tabs .tab', { hasText: 'Personal' }).click();
  await page.fill('#capture-input', 'Book dentist');
  await page.press('#capture-input', 'Enter');
  await expect(page.locator('.focus-title')).toHaveText('Book dentist');
  await page.locator('#tabs .tab', { hasText: 'Work' }).click();
  await expect(page.locator('.focus-title')).not.toHaveText('Book dentist');

  page.once('dialog', (d) => d.accept('Side project'));
  await page.locator('#tabs .tab.add').click();
  await expect(page.locator('#tabs .tab', { hasText: 'Side project' })).toBeVisible();

  // Snooze from the focus card via the menu.
  await page.locator('#tabs .tab', { hasText: 'Personal' }).click();
  await page.locator('.focus-card').getByRole('button', { name: 'Later', exact: true }).click();
  // Regression (UI review): every menu item must be on top at its center, not clipped by the card or covered by panels.
  const covered = await page.locator('.focus-card .menu [role="menuitem"]').evaluateAll((items) => items
    .filter((el) => { const r = el.getBoundingClientRect(); const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return !el.contains(hit); })
    .map((el) => el.textContent));
  expect(covered).toEqual([]);
  await page.locator('.focus-card .menu').getByRole('menuitem', { name: '1 hour' }).click();
  await expect(page.locator('.focus-card')).toContainText('Nothing is due');

  // Full report opens with the timeline.
  await page.locator('#tabs .tab', { hasText: 'All' }).click();
  const reportHref = await page.locator('#sec-overview .report-link').first().getAttribute('href');
  await page.goto(reportHref);
  await expect(page.locator('h1')).toHaveText(/Roll out feature X|Book dentist/);
  await expect(page.locator('.timeline li').first()).toBeVisible();

  expect(errors).toEqual([]);
});

test('details autosave, tags and labels filter, notes save as you type, files attach', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await expect(page.locator('#board')).toBeVisible();

  await page.fill('#capture-input', 'Write the launch plan');
  await page.press('#capture-input', 'Enter');
  await page.locator('.focus-title', { hasText: 'Write the launch plan' }).click();
  const drawer = page.locator('#drawer');

  // No save button: edits save themselves.
  await expect(drawer.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0);
  await drawer.locator('.title-input').fill('Write the launch plan v2');
  await drawer.getByLabel('Tags').fill('#launch #docs');
  await drawer.locator('.label-input').fill('Deep work');
  await drawer.locator('.label-input').press('Enter');
  await expect(drawer.locator('#save-state')).toHaveText('Saved');

  // Notes save while typing and stay one note.
  await drawer.locator('textarea[placeholder^="Note"]').fill('Outline done');
  await expect(drawer.locator('#save-state')).toHaveText('Saved');
  await drawer.locator('textarea[placeholder^="Note"]').fill('Outline done, next: review');
  await expect(drawer.locator('#save-state')).toHaveText('Saved');

  // Attach a file: the panel refreshes, but the note being written stays in the box (and stays the same note).
  await drawer.locator('input[type=file]').setInputFiles({ name: 'plan.md', mimeType: 'text/markdown', buffer: Buffer.from('# Plan') });
  await expect(drawer.locator('.attachments')).toContainText('plan.md');
  const noteBox = drawer.locator('textarea[placeholder^="Note"]');
  await expect(noteBox).toHaveValue('Outline done, next: review');
  await noteBox.fill('Outline done, next: review, sent to Bob');
  await expect(drawer.locator('#save-state')).toHaveText('Saved');

  // Someone else renames the task meanwhile; typing details here must not put the old title back.
  const id = await drawer.getAttribute('data-item-id');
  await request.patch(`/api/items/${id}`, { headers: { Authorization: `Bearer ${token}` }, data: { title: 'Launch plan (renamed elsewhere)' } });
  await drawer.getByPlaceholder(/^Details/).fill('Audience: partners');
  await expect(drawer.locator('#save-state')).toHaveText('Saved');
  const saved = await (await request.get(`/api/items/${id}`, { headers: { Authorization: `Bearer ${token}` } })).json();
  expect(saved.title).toBe('Launch plan (renamed elsewhere)');
  expect(saved.details).toBe('Audience: partners');
  await request.patch(`/api/items/${id}`, { headers: { Authorization: `Bearer ${token}` }, data: { title: 'Write the launch plan v2' } });

  await page.keyboard.press('Escape');
  await page.reload();
  await page.locator('#sec-notes > summary').click();
  await expect(page.locator('#sec-notes .note').filter({ hasText: 'Outline done' })).toHaveCount(1);
  await expect(page.locator('#sec-notes .note').first()).toContainText('sent to Bob');

  // Clicking a tag filters everything to it; Escape-clearing brings the rest back.
  const card = page.locator('.item, .focus-card').filter({ hasText: 'Write the launch plan v2' }).first();
  await expect(card.locator('.chip.label')).toContainText('Deep work');
  await card.locator('.chip.tag', { hasText: '#launch' }).click();
  await expect(page.locator('#filter')).toHaveValue('#launch');
  await expect(page.locator('.focus-title')).toHaveText('Write the launch plan v2');
  await page.locator('#filter-clear').click();
  await expect(page.locator('#filter')).toHaveValue('');

  // Where the files live is always visible.
  await expect(page.locator('#vault')).toContainText('Saved as markdown in');

  // Connecting an AI app is one click and one copy.
  await page.getByRole('button', { name: 'Connect an AI app' }).click();
  await expect(drawer.locator('details.connect[open]')).toContainText('Claude Code');
  await expect(drawer.locator('details.connect[open] pre')).toContainText('claude mcp add todo-tracker');
  await drawer.locator('details.connect', { hasText: 'VS Code' }).locator('summary').click();
  await expect(drawer.locator('details.connect', { hasText: 'VS Code' }).locator('pre')).toContainText('"servers"');
  await page.keyboard.press('Escape');
  await expect(drawer).toBeHidden();

  expect(errors).toEqual([]);
});

test('order Do now and subtasks with arrows, Alt+arrow keys, and drag and drop', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const auth = { Authorization: `Bearer ${token}` };
  const launch = await (await request.post('/api/launch', { headers: auth, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await expect(page.locator('#board')).toBeVisible();
  for (const title of ['Order alpha', 'Order bravo', 'Order charlie']) {
    await page.fill('#capture-input', title);
    await page.press('#capture-input', 'Enter');
    await expect(page.locator('.title, .focus-title', { hasText: title })).toHaveCount(1);
  }
  await page.fill('#filter', 'order');
  await page.press('#filter', 'Enter');
  const focus = page.locator('.focus-title');
  const rows = page.locator('#sec-now .item .title');
  const nowTitles = async () => (await (await request.get('/api/dashboard?q=order', { headers: auth })).json()).now.map((c) => c.title);
  await expect(focus).toHaveText('Order alpha');

  // Arrow button: charlie moves above bravo.
  const charlie = page.locator('#sec-now .item', { hasText: 'Order charlie' });
  await charlie.hover();
  await charlie.getByRole('button', { name: 'Move up' }).click();
  await expect(rows).toHaveText(['Order charlie', 'Order bravo']);
  await expect.poll(nowTitles).toEqual(['Order alpha', 'Order charlie', 'Order bravo']);

  // Keyboard: Alt+↑ on charlie makes it the focus.
  await page.locator('#sec-now .item .title', { hasText: 'Order charlie' }).focus();
  await page.keyboard.press('Alt+ArrowUp');
  await expect(focus).toHaveText('Order charlie');

  // The keyboard stays on the moved task, so it can keep moving (even after the save redraws the board).
  await page.keyboard.press('Alt+ArrowDown');
  await expect(rows).toHaveText(['Order charlie', 'Order bravo']);
  await expect.poll(nowTitles).toEqual(['Order alpha', 'Order charlie', 'Order bravo']);
  await page.keyboard.press('Alt+ArrowDown');
  await expect(rows).toHaveText(['Order bravo', 'Order charlie']);
  await expect.poll(nowTitles).toEqual(['Order alpha', 'Order bravo', 'Order charlie']);

  // Drag and drop: bravo dropped on the focus card (anywhere on it) becomes the focus.
  await page.locator('#sec-now .item', { hasText: 'Order bravo' }).dragTo(page.locator('.focus-card'), { targetPosition: { x: 20, y: 60 } });
  await expect(focus).toHaveText('Order bravo');
  await expect.poll(nowTitles).toEqual(['Order bravo', 'Order alpha', 'Order charlie']);

  // The focus card has its own way down.
  await page.getByRole('button', { name: 'Do something else first' }).click();
  await expect(focus).toHaveText('Order alpha');
  await expect.poll(nowTitles).toEqual(['Order alpha', 'Order bravo', 'Order charlie']);

  // Subtasks keep their parent and can be reordered in the panel.
  const parent = await (await request.post('/api/items', { headers: auth, data: { title: 'Order trip' } })).json();
  for (const t of ['Flights', 'Hotel', 'Visa']) await request.post('/api/items', { headers: auth, data: { title: t, parentId: parent.id } });
  await page.goto(`/?item=${parent.id}`);
  const subs = page.locator('#drawer .subtasks > li .sub-row .link');
  await expect(subs).toHaveText(['Flights', 'Hotel', 'Visa']);
  const visa = page.locator('#drawer .subtasks > li', { hasText: 'Visa' });
  await visa.hover();
  await visa.getByRole('button', { name: 'Move up' }).click();
  await expect(subs).toHaveText(['Flights', 'Visa', 'Hotel']);
  // HTML5 drag and drop in headless Chromium on Linux sometimes drops the gesture: retry it (dropping again is a no-op).
  await expect(async () => {
    await page.locator('#drawer .subtasks > li', { hasText: 'Hotel' }).dragTo(page.locator('#drawer .subtasks > li', { hasText: 'Flights' }), { targetPosition: { x: 20, y: 2 } });
    await expect(subs).toHaveText(['Hotel', 'Flights', 'Visa'], { timeout: 2000 });
  }).toPass({ timeout: 20000 });

  expect(errors).toEqual([]);
});

test('Ask AI: chat with the agent, approve a change, and see the answer', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await page.getByRole('button', { name: 'Ask AI' }).click();
  const drawer = page.locator('#drawer');
  await expect(drawer.locator('.chat')).toBeVisible();
  await expect(drawer.locator('.chat-picker')).toBeHidden(); // only one agent: no picker

  const input = drawer.getByRole('textbox', { name: 'Message' });
  await input.fill('hi');
  await input.press('Enter');
  await expect(drawer.locator('.msg.agent').last()).toHaveText('Hello, there.');

  await input.fill('add Milk');
  await input.press('Enter');
  const ask = drawer.locator('.ask').last();
  await expect(ask).toContainText('Change your tasks');
  await ask.getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(drawer.locator('.msg.agent').last()).toHaveText('Added "Milk".');
  await expect(ask).toContainText('Allowed');

  await drawer.getByRole('button', { name: 'New chat' }).click();
  await expect(drawer.locator('.msg')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(drawer).toBeHidden();
  expect(errors).toEqual([]);
});

test('Plugins: every extra can be switched off (after a restart)', async ({ page, request }) => {
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await page.getByRole('button', { name: 'Plugins' }).click();
  const drawer = page.locator('#drawer');
  for (const name of ['Ask AI', 'Connect AI apps', 'Focus timer', 'Version history', 'Obsidian', 'Teams reminders']) {
    await expect(drawer.getByRole('checkbox', { name })).toBeChecked();
  }
  await drawer.getByRole('checkbox', { name: 'Teams reminders' }).uncheck();
  await expect(drawer).toContainText('Restart Todo Tracker to apply');
  const plugins = await (await request.get('/api/plugins', { headers: { Authorization: `Bearer ${token}` } })).json();
  expect(plugins.find((p) => p.id === 'teams').enabled).toBe(true); // still running until the restart
  await drawer.getByRole('checkbox', { name: 'Teams reminders' }).check();
});

test('Sync: the panel says where it syncs and sync can be switched off', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/' } })).json();
  await page.goto(launch.url);
  await page.getByRole('button', { name: /^Sync/ }).click();
  const drawer = page.locator('#drawer');
  // The test server sees no cloud drives (TODOTRACKER_CLOUD=off).
  await expect(drawer).toContainText('Nothing to sync with');
  await drawer.getByRole('combobox', { name: 'Sync with' }).selectOption('off');
  await expect(drawer).toContainText('Sync is off');
  await drawer.getByRole('combobox', { name: 'Sync with' }).selectOption('auto');
  await expect(drawer).toContainText('Nothing to sync with');
  expect(errors).toEqual([]);
});
