import { test, expect, chromium } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';

const token = 'e2e-token-0123456789abcdefghijklmnop';
const extensionDir = path.resolve('../../extension');

// The real, unpacked extension in Chromium (extensions only load in a persistent profile, and branded Edge/Chrome
// builds ignore --load-extension, so this always uses Playwright's Chromium).
test('Browser extension: loads unpacked, pairs with a code, and shows what to do now', async ({ request, baseURL }) => {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'tt-ext-'));
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium',
    args: [`--disable-extensions-except=${extensionDir}`, `--load-extension=${extensionDir}`],
  });
  try {
    const auth = { Authorization: `Bearer ${token}` };
    await request.post('/api/items', { headers: auth, data: { title: 'Seen from the extension' } });
    const isOurs = (w) => w.url().endsWith('/background.js');
    const worker = context.serviceWorkers().find(isOurs) ?? await context.waitForEvent('serviceworker', { predicate: isOurs });
    const id = new URL(worker.url()).host;

    const panel = await context.newPage();
    const errors = [];
    panel.on('pageerror', (e) => errors.push(e.message));
    await panel.goto(`chrome-extension://${id}/sidepanel.html`);
    await expect(panel.locator('#setup')).toBeVisible();

    // The code the dashboard's setup guide shows.
    const { code } = await (await request.post('/api/plugins/browser-extension/pair', { headers: auth })).json();
    await panel.locator('#advanced summary').click();
    await panel.locator('#server').fill(baseURL);
    await panel.locator('#code').fill(`${code.slice(0, 3)} ${code.slice(3)}`);
    await panel.getByRole('button', { name: 'Connect' }).click();

    await expect(panel.locator('#main')).toBeVisible();
    await expect(panel.locator('body')).toContainText('Seen from the extension');
    // The token never had to be typed, and the code is used up.
    await expect(panel.locator('#token')).toHaveValue('');
    const reuse = await request.post('/api/plugins/browser-extension/claim', { headers: { 'X-TodoTracker-Client': 'browser-extension' }, data: { code } });
    expect(reuse.status()).toBe(400);
    expect(errors).toEqual([]);
  } finally {
    await context.close();
    fs.rmSync(profile, { recursive: true, force: true });
  }
});

test('Browser extension: the setup guide walks through the steps for this browser', async ({ page, request }) => {
  const launch = await (await request.post('/api/launch', { headers: { Authorization: `Bearer ${token}` }, data: { return: '/?extension=1' } })).json();
  await page.goto(launch.url);
  const drawer = page.locator('#drawer');
  await expect(drawer).toContainText('Load unpacked');
  const download = page.waitForEvent('download');
  await drawer.getByRole('link', { name: 'Download the extension' }).click();
  expect((await download).suggestedFilename()).toBe('todo-tracker-extension.zip');

  await drawer.getByRole('tab', { name: 'Safari (Mac)' }).click();
  await expect(drawer).toContainText('Allow Unsigned Extensions');

  await drawer.getByRole('button', { name: 'Get a pairing code' }).click();
  await expect(drawer.getByLabel('Pairing code')).toHaveText(/^\d{3} \d{3}$/);
});
