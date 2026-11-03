import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError, upload, text } from '../../src/OneThing.Server/wwwroot/js/api.js';

const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; });

const answer = (status, body) => async () => ({ ok: status >= 200 && status < 300, status, text: async () => body, json: async () => JSON.parse(body) });

test('no connection to One Thing says so plainly, and is marked as offline', async () => {
  globalThis.fetch = async () => { throw new TypeError('Failed to fetch'); };
  await assert.rejects(api('/api/dashboard'), (err) => err instanceof ApiError && err.offline && err.status === 0 && /Can’t reach One Thing/.test(err.message) && !/Failed to fetch/.test(err.message));
  await assert.rejects(text('/api/items/x/history/1'), (err) => err.offline);
  await assert.rejects(upload('/api/items/x/attachments', new Blob(['x']), 'a.png'), (err) => err.offline);
});

test('an answer that isn’t JSON gives a friendly error, not a parser message', async () => {
  globalThis.fetch = answer(502, '<html>Bad gateway</html>');
  await assert.rejects(api('/api/dashboard'), (err) => err.status === 502 && !/JSON|Unexpected token/.test(err.message) && /502/.test(err.message));
  globalThis.fetch = answer(200, '<html>not the app</html>');
  await assert.rejects(api('/api/dashboard'), (err) => !/JSON|Unexpected token/.test(err.message));
});

test('the server’s own explanation is kept', async () => {
  globalThis.fetch = answer(409, JSON.stringify({ detail: 'The agent is busy. Wait for its answer, or stop it.' }));
  await assert.rejects(api('/api/plugins/agent-chat/message', { method: 'POST', body: { text: 'hi' } }), (err) => err.status === 409 && err.message === 'The agent is busy. Wait for its answer, or stop it.' && !err.offline);
});
