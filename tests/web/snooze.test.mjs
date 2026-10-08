import { test } from 'node:test';
import assert from 'node:assert/strict';
import { whenText, metaChips, rulePreview } from '../../src/TodoTracker.Server/wwwroot/js/format.js';

// Local times (the browser's zone), so the words match what the user sees on the clock.
const now = new Date(2026, 0, 5, 9, 0); // Monday 5 Jan 2026, 9:00
const at = (d, h, m = 0) => new Date(2026, 0, d, h, m);

test('whenText says today / tomorrow / weekday / date, with a 24h time', () => {
  assert.equal(whenText(at(5, 17), now), 'today 17:00');
  assert.equal(whenText(at(5, 9, 15), now), 'today 9:15');
  assert.equal(whenText(at(6, 9), now), 'tomorrow 9:00');
  assert.equal(whenText(at(9, 14, 30), now), 'Fri 14:30');
  assert.equal(whenText(at(11, 9), now), 'Sun 9:00');
  assert.equal(whenText(at(12, 9), now), 'Mon 12 Jan, 9:00');
  assert.equal(whenText(new Date(2026, 1, 5, 9), now), 'Thu 5 Feb, 9:00');
  assert.equal(whenText(new Date(2027, 0, 4, 9), now), 'Mon 4 Jan 2027, 9:00');
  assert.equal(whenText('2026-01-05T08:00:00', now), 'today 8:00');
});

test('a waiting card says which task it waits for', () => {
  const chips = metaChips({ waitingForTitle: 'Get the keys' }, { waiting: true, now });
  assert.deepEqual(chips, [{ text: 'after “Get the keys”', tone: 'info' }]);
});

test('a card waiting for a time and a task shows both', () => {
  const chips = metaChips({ wakeAt: at(6, 9).toISOString(), waitingForTitle: 'Keys' }, { waiting: true, now });
  assert.deepEqual(chips.map((c) => c.text), ['back in 1d', 'after “Keys”']);
});

test('rulePreview turns the server answer into a line to show (or nothing yet)', () => {
  assert.deepEqual(rulePreview(null, now), { text: '', ok: false });
  assert.deepEqual(rulePreview({ ruleAt: at(12, 9).toISOString(), ruleIn: 'in 7d' }, now), { text: 'Mon 12 Jan, 9:00 · in 7d', ok: true });
  assert.deepEqual(rulePreview({ ruleProblem: 'Couldn’t tell when "x" is.' }, now), { text: 'Couldn’t tell when "x" is.', ok: false });
});
