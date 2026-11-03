import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { activate } from '../../src/OneThing.Server/wwwroot/js/plugins/sync.js';

test('the sync status isn’t checked while sync is off, the window is hidden, or you’re signed out', () => {
  mock.timers.enable({ apis: ['setInterval'] });
  try {
    const el = () => new Proxy({}, { get: (t, k) => (k in t ? t[k] : k === 'classList' ? { add() {}, toggle() {} } : () => {}) });
    globalThis.location = { search: '' };
    globalThis.document = { hidden: false };
    let on = true;
    let signedIn = true;
    let checks = 0;
    activate({
      h: el, icon: el, toast() {}, refresh() {}, openPanel: async () => true,
      addHeaderButton: () => el(),
      api: async () => { checks++; throw new Error('not needed here'); },
      on: () => on,
      signedIn: () => signedIn,
    });
    assert.equal(checks, 1); // once when it starts

    mock.timers.tick(30000);
    assert.equal(checks, 2);

    on = false;
    mock.timers.tick(30000);
    document.hidden = true;
    on = true;
    mock.timers.tick(30000);
    document.hidden = false;
    signedIn = false;
    mock.timers.tick(30000);
    assert.equal(checks, 2);

    signedIn = true;
    mock.timers.tick(30000);
    assert.equal(checks, 3);
  } finally {
    mock.timers.reset();
  }
});
