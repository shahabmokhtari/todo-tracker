import { test } from 'node:test';
import assert from 'node:assert/strict';
import { phoneStatusLine, pairedLine } from '../../src/TodoTracker.Server/wwwroot/js/plugins/phones.js';

const status = (over) => ({ letPhonesConnect: true, listening: true, problem: null, addresses: ['192.168.1.20:5318'], port: 5318, ...over });

test('Settings › Phones says whether phones can connect, and when a restart is needed', () => {
  assert.equal(phoneStatusLine(status()), 'On: phones on the same Wi-Fi or Tailscale reach this computer at 192.168.1.20:5318.');
  assert.equal(phoneStatusLine(status({ listening: false })), 'Restart Todo Tracker so phones can connect.');
  assert.equal(phoneStatusLine(status({ letPhonesConnect: false })), 'Phones can’t connect any more. Todo Tracker stops listening for them when it restarts.');
  assert.equal(phoneStatusLine(status({ letPhonesConnect: false, listening: false })), 'Off: phones can’t reach this computer.');
  assert.match(phoneStatusLine(status({ addresses: [] })), /isn’t on a network/);
  assert.equal(phoneStatusLine(status({ listening: false, problem: 'Port 5318 is used by another app.' })), 'Port 5318 is used by another app.');
});

test('a connected phone shows when it was paired and last used', () => {
  const now = Date.parse('2026-03-03T12:00:00Z');
  assert.match(pairedLine({ pairedAt: '2026-03-01T09:00:00Z', lastUsed: null }, now), /not used yet$/);
  assert.match(pairedLine({ pairedAt: '2026-03-01T09:00:00Z', lastUsed: '2026-03-03T11:58:00Z' }, now), /last used 2 min ago$/);
  assert.match(pairedLine({ pairedAt: '2026-03-01T09:00:00Z', lastUsed: '2026-03-03T09:00:00Z' }, now), /last used 3 h ago$/);
});
