import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createClient, normalizeServerUrl, pageSource } from '../lib/client.js';

function fakeFetch(responses = {}) {
  const calls = [];
  const fn = async (url, init = {}) => {
    calls.push({ url, init, body: init.body ? JSON.parse(init.body) : undefined });
    const key = `${init.method || 'GET'} ${new URL(url).pathname}`;
    const r = responses[key] ?? { status: 200, body: {} };
    return {
      ok: r.status >= 200 && r.status < 300,
      status: r.status,
      text: async () => (r.body === undefined ? '' : JSON.stringify(r.body)),
    };
  };
  fn.calls = calls;
  return fn;
}

test('client sends bearer token and browser actor on every request', async () => {
  const fetch = fakeFetch({ 'GET /api/dashboard': { status: 200, body: { now: [] } } });
  const client = createClient({ serverUrl: 'http://127.0.0.1:5317/', token: 'abc', fetch });

  await client.dashboard();

  const { url, init } = fetch.calls[0];
  assert.equal(url, 'http://127.0.0.1:5317/api/dashboard');
  assert.equal(init.headers.Authorization, 'Bearer abc');
  assert.equal(init.headers['X-TodoTracker-Actor'], 'browser-extension');
});

test('dashboard can be filtered by group', async () => {
  const fetch = fakeFetch();
  await createClient({ serverUrl: 'http://127.0.0.1:5317', token: 't', fetch }).dashboard('g 1');
  assert.equal(fetch.calls[0].url, 'http://127.0.0.1:5317/api/dashboard?group=g%201');
});

test('capture, complete, note and snooze hit the right endpoints', async () => {
  const fetch = fakeFetch();
  const client = createClient({ serverUrl: 'http://127.0.0.1:5317', token: 't', fetch });

  await client.capture('Read RFC !', 'grp');
  await client.complete('id1');
  await client.addNote('id1', 'Found it', { url: 'https://example.com/a', title: 'A' });
  await client.snooze('id1', 60);

  assert.deepEqual(fetch.calls.map((c) => `${c.init.method} ${new URL(c.url).pathname}`), [
    'POST /api/capture', 'POST /api/items/id1/complete', 'POST /api/items/id1/notes', 'POST /api/items/id1/schedule',
  ]);
  assert.deepEqual(fetch.calls[0].body, { text: 'Read RFC !', groupId: 'grp' });
  assert.deepEqual(fetch.calls[2].body, { text: 'Found it', sourceUrl: 'https://example.com/a', sourceTitle: 'A' });
  assert.deepEqual(fetch.calls[3].body, { inMinutes: 60, notify: true });
});

test('errors surface the server problem detail and auth failures are flagged', async () => {
  const fetch = fakeFetch({
    'POST /api/items/x/complete': { status: 409, body: { detail: 'Finish "Ring 0" first.' } },
    'GET /api/dashboard': { status: 401 },
  });
  const client = createClient({ serverUrl: 'http://127.0.0.1:5317', token: 't', fetch });

  await assert.rejects(client.complete('x'), /Finish "Ring 0" first/);
  await assert.rejects(client.dashboard(), (err) => err.unauthorized === true);
});

test('launch url comes from a single-use code, never the token', async () => {
  const fetch = fakeFetch({ 'POST /api/launch': { status: 200, body: { url: 'http://127.0.0.1:5317/auth?code=xyz&return=%2F' } } });
  const client = createClient({ serverUrl: 'http://127.0.0.1:5317', token: 'secret', fetch });

  const url = await client.launchUrl('/report.html?id=1');

  assert.equal(url, 'http://127.0.0.1:5317/auth?code=xyz&return=%2F');
  assert.deepEqual(fetch.calls[0].body, { return: '/report.html?id=1' });
  assert.ok(!url.includes('secret'));
});

test('server url must be a loopback http url', () => {
  assert.equal(normalizeServerUrl(' http://localhost:5317/ '), 'http://localhost:5317');
  assert.equal(normalizeServerUrl('http://127.0.0.1:6000'), 'http://127.0.0.1:6000');
  assert.throws(() => normalizeServerUrl('https://evil.example'), /127\.0\.0\.1 or localhost/);
  assert.throws(() => normalizeServerUrl('not a url'), /valid URL/);
});

test('pageSource only attaches http(s) pages', () => {
  assert.deepEqual(pageSource({ url: 'https://docs.example/x', title: 'Docs' }), { url: 'https://docs.example/x', title: 'Docs' });
  assert.equal(pageSource({ url: 'chrome://settings', title: 'Settings' }), null);
  assert.equal(pageSource(undefined), null);
});

test('extension format.js and icons.js are exact copies of the web ones (no drift)', () => {
  for (const file of ['format.js', 'icons.js']) {
    const web = readFileSync(new URL(`../../src/TodoTracker.Server/wwwroot/js/${file}`, import.meta.url), 'utf8');
    const ext = readFileSync(new URL(`../lib/${file}`, import.meta.url), 'utf8');
    assert.equal(ext.replace(/\r\n/g, '\n'), web.replace(/\r\n/g, '\n'), file);
  }
});

test('manifest only requests loopback host access', () => {
  const manifest = JSON.parse(readFileSync(new URL('../manifest.json', import.meta.url), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.host_permissions, ['http://127.0.0.1/*', 'http://localhost/*']);
  assert.equal(manifest.side_panel.default_path, 'sidepanel.html');
});
