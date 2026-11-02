import { test } from 'node:test';
import assert from 'node:assert/strict';
import worker from '../../website/src/worker.js';

const assets = { fetch: async (request) => new Response(`asset ${new URL(request.url).pathname}`) };

test('the old address sends people to onething.awesomehq.app, keeping the page and query', async () => {
  const response = await worker.fetch(new Request('https://todotracker.awesomehq.app/img/og.png?v=2'), { ASSETS: assets });
  assert.equal(response.status, 301);
  assert.equal(response.headers.get('location'), 'https://onething.awesomehq.app/img/og.png?v=2');
});

test('the site itself is served from the new address', async () => {
  const response = await worker.fetch(new Request('https://onething.awesomehq.app/'), { ASSETS: assets });
  assert.equal(response.status, 200);
  assert.equal(await response.text(), 'asset /');
});
