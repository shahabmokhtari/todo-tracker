// Send feedback: the sheet in the web app (and in the Windows and Linux apps, which open it with /?feedback=button).
// One Thing passes the note to the issues relay; here that's a stand-in on this computer (playwright.config.js points
// the server at it), so nothing ever reaches issues.awesomehq.app.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';

const token = 'e2e-token-0123456789abcdefghijklmnop';
const auth = { Authorization: `Bearer ${token}` };
const relayPort = Number(process.env.E2E_RELAY_PORT || 5397);

test.describe.configure({ mode: 'serial' });

let relay;
/** What the stand-in relay got, and what it answers next (202 when nothing is queued). */
const received = [];
const answers = [];

test.beforeAll(async () => {
  relay = http.createServer((req, res) => {
    let body = '';
    req.on('data', (chunk) => { body += chunk; });
    req.on('end', () => {
      received.push({ method: req.method, url: req.url, headers: req.headers, body: JSON.parse(body) });
      const status = answers.shift() ?? 202;
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(status < 300 ? { ref: 'IS-E2E00001', status: 'queued' } : { error: 'stand-in' }));
    });
  });
  await new Promise((resolve) => relay.listen(relayPort, '127.0.0.1', resolve));
});

test.afterAll(async () => {
  await new Promise((resolve) => relay.close(resolve));
});

/** The app as the person at this computer opens it (the Windows app and the tray do the same with the owner key). */
async function openAsOwner(page, request, where) {
  const ownerKey = fs.readFileSync(path.join(process.env.ONETHING_E2E_DATA, 'owner-key'), 'utf8').trim();
  const launch = await (await request.post('/api/launch', { headers: { ...auth, 'X-OneThing-Owner-Key': ownerKey }, data: { return: where } })).json();
  await page.goto(launch.url);
}

test('Send feedback: a note that couldn’t go keeps its words and goes on the next try, once', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  received.length = 0;
  await openAsOwner(page, request, '/?feedback=button');

  const sheet = page.locator('#drawer');
  await expect(sheet.locator('.drawer-head')).toContainText('Send feedback');
  await expect(sheet).toContainText('We’ll include the app version, your OS and its version, device model, language and screen size.');
  const send = sheet.getByRole('button', { name: 'Send' });
  await expect(send).toBeDisabled();
  // Opened once: a reload doesn't open it again.
  expect(new URL(page.url()).searchParams.has('feedback')).toBe(false);

  await sheet.getByRole('button', { name: 'Idea' }).click();
  await expect(sheet.getByRole('button', { name: 'Idea' })).toHaveAttribute('aria-pressed', 'true');
  await sheet.getByLabel('Your note').fill('A dark widget, please');
  await expect(sheet.getByPlaceholder('Only if you’d like a reply. It’s shown with your note on GitHub.')).toBeVisible();
  await sheet.getByLabel('Email (optional)').fill('me@example.com');

  answers.push(503);
  await send.click();
  await expect(sheet.locator('.feedback-status')).toHaveText('Couldn’t send — check your connection and try again.');
  await expect(sheet.getByLabel('Your note')).toHaveValue('A dark widget, please');

  await send.click();
  await expect(page.locator('#toast')).toContainText('Thanks — we read every note.');
  await expect(sheet).toBeHidden();

  expect(received).toHaveLength(2);
  const [first, second] = received;
  expect(first.url).toBe('/v1/issues');
  expect(first.headers.authorization).toBe('Bearer pk_gGPPMjRSsuHaXRdgdXvUevvL');
  expect(first.headers.origin).toBeUndefined();
  expect(second.body.clientID).toBe(first.body.clientID);
  expect(first.body.message).toBe('A dark widget, please');
  expect(first.body.contact).toBe('me@example.com');
  expect(first.body.title).toMatch(/^Idea · (Windows|macOS|Linux): A dark widget, please$/);
  expect(first.body.labels).toEqual(['kind:idea', `platform:${first.body.metadata.Platform}`, 'source:button']);
  expect(Object.keys(first.body.metadata)).toEqual(['Platform', 'App version', 'OS', 'Device', 'Language', 'Screen', 'Source']);
  expect(errors).toEqual([]);
});

test('Send feedback: from Settings, and a refused note says something went wrong', async ({ page, request }) => {
  received.length = 0;
  await openAsOwner(page, request, '/#/settings');
  await page.locator('#settings-feedback').getByRole('button', { name: 'Send feedback…' }).click();

  const sheet = page.locator('#drawer');
  await sheet.getByLabel('Your note').fill('The timer stops when I lock the screen.');
  answers.push(400);
  await sheet.getByRole('button', { name: 'Send' }).click();
  await expect(sheet.locator('.feedback-status')).toHaveText('Couldn’t send your note: something went wrong on our side. Please try again later.');
  expect(received).toHaveLength(1);
  expect(received[0].body.labels[0]).toBe('kind:bug');
});
