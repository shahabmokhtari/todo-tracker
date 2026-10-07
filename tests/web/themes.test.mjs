import { test } from 'node:test';
import assert from 'node:assert/strict';

// A page, as far as themes.js needs one.
globalThis.document = { documentElement: { dataset: {} } };
const stored = new Map();
globalThis.localStorage = { setItem: (k, v) => stored.set(k, v), removeItem: (k) => stored.delete(k), getItem: (k) => stored.get(k) ?? null };

const { applyTheme, chooseTheme, followTheme } = await import('../../src/TodoTracker.Server/wwwroot/js/themes.js');
const shown = () => document.documentElement.dataset.theme ?? 'system';

test('a theme picked here wins over a read of the shared one that was already on its way', async () => {
  applyTheme('light');
  let answer;
  const read = followTheme(() => new Promise((resolve) => { answer = resolve; }));
  await chooseTheme('dark');
  answer('light'); // what the app said before the save
  await read;
  assert.equal(shown(), 'dark');
});

test('a read started while the choice is being saved is ignored too; later ones apply', async () => {
  applyTheme('system');
  let saved;
  const choosing = chooseTheme('dark', () => new Promise((resolve) => { saved = resolve; }));
  const during = followTheme(async () => 'system');
  await during;
  assert.equal(shown(), 'dark');
  saved();
  await choosing;

  await followTheme(async () => 'light'); // changed in the sidebar later
  assert.equal(shown(), 'light');
  assert.equal(stored.get('tt.theme'), 'light');
});
