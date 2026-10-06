import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAutosave, changedFields } from '../../src/TodoTracker.Server/wwwroot/js/autosave.js';

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

test('flush reports whether everything was saved', async () => {
  let fail = true;
  const autosave = createAutosave({ save: async () => { if (fail) throw new Error('offline'); }, setTimer: () => 0, clearTimer: () => {} });

  autosave.schedule('note');
  assert.equal(await autosave.flush(), false);
  assert.equal(autosave.dirty, true);

  fail = false;
  assert.equal(await autosave.flush(), true);
  assert.equal(autosave.dirty, false);
});

test('changedFields sends only what changed, keeping linked fields together', () => {
  const groups = [['title'], ['details'], ['deadline', 'clearDeadline'], ['tags']];
  const before = { title: 'Ship', details: 'x', deadline: '2026-01-01T00:00:00Z', clearDeadline: false, tags: ['a'] };

  assert.deepEqual(changedFields(before, { ...before, details: 'xy' }, groups), { details: 'xy' });
  assert.deepEqual(changedFields(before, { ...before, deadline: null, clearDeadline: true }, groups), { deadline: null, clearDeadline: true });
  assert.deepEqual(changedFields(before, { ...before, tags: ['a', 'b'] }, groups), { tags: ['a', 'b'] });
  assert.deepEqual(changedFields(before, { ...before }, groups), {});
});