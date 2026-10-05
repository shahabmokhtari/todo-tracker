import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAutosave } from '../../src/TodoTracker.Server/wwwroot/js/autosave.js';

/** Manual clock: timers only fire when the test says so. */
function fakeTimers() {
  let next = 1;
  const timers = new Map();
  return {
    setTimer: (fn) => { const id = next++; timers.set(id, fn); return id; },
    clearTimer: (id) => timers.delete(id),
    fire() { const all = [...timers.values()]; timers.clear(); all.forEach((fn) => fn()); },
    get count() { return timers.size; },
  };
}

const tick = () => new Promise((r) => setImmediate(r));

test('rapid edits become one save of the latest value', async () => {
  const clock = fakeTimers();
  const saves = [];
  const autosave = createAutosave({ save: async (v) => saves.push(v), ...clock });

  autosave.schedule('d');
  autosave.schedule('de');
  autosave.schedule('dep');
  clock.fire();
  await tick();

  assert.deepEqual(saves, ['dep']);
  assert.equal(autosave.dirty, false);
});

test('flush saves right away', async () => {
  const clock = fakeTimers();
  const saves = [];
  const autosave = createAutosave({ save: async (v) => saves.push(v), ...clock });

  autosave.schedule('note');
  await autosave.flush();

  assert.deepEqual(saves, ['note']);
  assert.equal(clock.count, 0);
});

test('typing during a save is saved afterwards, in order', async () => {
  const clock = fakeTimers();
  const saves = [];
  let release;
  const autosave = createAutosave({ save: (v) => { saves.push(v); return saves.length === 1 ? new Promise((r) => { release = r; }) : Promise.resolve(); }, ...clock });

  autosave.schedule('a');
  clock.fire();
  autosave.schedule('ab');
  release();
  await autosave.flush();

  assert.deepEqual(saves, ['a', 'ab']);
});

test('an unchanged value is not saved again', async () => {
  const clock = fakeTimers();
  const saves = [];
  const autosave = createAutosave({ save: async (v) => saves.push(v), ...clock });

  autosave.schedule({ title: 'x' });
  await autosave.flush();
  autosave.schedule({ title: 'x' });
  await autosave.flush();

  assert.equal(saves.length, 1);
});

test('a failed save keeps the value and retries on the next flush', async () => {
  const clock = fakeTimers();
  const states = [];
  let fail = true;
  const saves = [];
  const autosave = createAutosave({ save: async (v) => { if (fail) throw new Error('offline'); saves.push(v); }, onState: (s) => states.push(s), ...clock });

  autosave.schedule('keep me');
  await autosave.flush();
  assert.equal(autosave.dirty, true);
  assert.ok(states.includes('error'));

  fail = false;
  await autosave.flush();
  assert.deepEqual(saves, ['keep me']);
  assert.equal(states.at(-1), 'saved');
});

test('cancel drops unsaved changes', async () => {
  const clock = fakeTimers();
  const saves = [];
  const autosave = createAutosave({ save: async (v) => saves.push(v), ...clock });

  autosave.schedule('gone');
  autosave.cancel();
  await autosave.flush();

  assert.deepEqual(saves, []);
});
