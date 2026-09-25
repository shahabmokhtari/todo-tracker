import { test, expect } from '@playwright/test';

const token = 'e2e-token-0123456789abcdefghijklmnop';

test.describe.configure({ mode: 'serial' });

test('dashboard: capture, rollout steps, gating, groups, and report', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));

  // Unauthenticated visitors get the connect screen.
  await page.goto('/');
  await expect(page.locator('#login')).toBeVisible();

  // The desktop "Open dashboard" link authenticates via a one-time redirect.
  await page.goto(`/auth?token=${token}`);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.locator('#board')).toBeVisible();
  await expect(page.locator('#tabs .tab')).toHaveText([/All/, /Work/, /Personal/, '+']);
  errors.length = 0; // the 401 from the unauthenticated visit above is expected

  // Quick capture with priority syntax becomes the focus card.
  await page.fill('#capture-input', 'Roll out feature X !!');
  await page.press('#capture-input', 'Enter');
  await expect(page.locator('.focus-title')).toHaveText('Roll out feature X');

  // Add gated rollout steps from the task drawer.
  await page.click('.focus-title');
  const drawer = page.locator('#drawer');
  await expect(drawer).toBeVisible();
  await drawer.locator('summary', { hasText: 'Add rollout steps' }).click();
  await drawer.locator('textarea[placeholder^="Rollout steps"]').fill('Ring 0\nRing 1');
  await drawer.getByRole('button', { name: 'Add steps' }).click();
  await expect(drawer.locator('.subtasks li')).toHaveCount(2);
  await page.keyboard.press('Escape');

  await expect(page.locator('.focus-title')).toHaveText('Ring 0');
  await expect(page.locator('.focus-card .meta')).toContainText('Step 1 of 2');
  // Hidden panels must not block clicks or render (regression: CSS display overrode [hidden]).
  await expect(page.locator('#drawer')).toBeHidden();
  await expect(page.locator('.menu').first()).toBeHidden();
  await expect(page.locator('.inline-note').first()).toBeHidden();

  // Log a note, then finish the step: the next step is gated for 24h and waits.
  await page.locator('.focus-card').getByRole('button', { name: '✎ Note' }).click();
  await page.locator('.focus-card .inline-note input').fill('Ring 0 healthy');
  await page.locator('.focus-card .inline-note button').click();
  await page.locator('#sec-notes > summary').click();
  await expect(page.locator('#sec-notes .note').first()).toContainText('Ring 0 healthy');

  await page.locator('.focus-card').getByRole('button', { name: '✓ Done' }).click();
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
  await page.locator('.focus-card').getByRole('button', { name: '⏰ Later' }).click();
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

