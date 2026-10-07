import { test } from 'node:test';
import assert from 'node:assert/strict';
import { duration, clock, byDay, dayKey, dayLabel } from '../../src/TodoTracker.Server/wwwroot/js/timefmt.js';
import { columnsOf, wip, dropRequests, keyboardMove, cardFacts } from '../../src/TodoTracker.Server/wwwroot/js/boardmodel.js';
import { flatten, index, indentRequests, outdentRequests, stepRequests, dropZone, dropRequests as outlineDrop } from '../../src/TodoTracker.Server/wwwroot/js/outlinemodel.js';
import { breakState, tipFor } from '../../src/TodoTracker.Server/wwwroot/js/breaks.js';
import { score, rank } from '../../src/TodoTracker.Server/wwwroot/js/palette.js';
import { niceScale, barLayout, donutSlices, arcPath, ganttLayout, heatLevel } from '../../src/TodoTracker.Server/wwwroot/js/charts.js';

const node = (id, extra = {}) => ({ id, title: id, stage: 'next', done: false, children: [], ...extra });

test('durations read like people say them', () => {
  assert.equal(duration(45), '45 s');
  assert.equal(duration(25 * 60), '25 min');
  assert.equal(duration(65 * 60), '1 h 05 min');
  assert.equal(duration(2 * 3600), '2 h');
  assert.equal(clock(247), '4:07');
  assert.equal(clock(3847), '1:04:07');
});

test('days group newest first with friendly labels', () => {
  const now = new Date(2026, 0, 7, 15);
  const groups = byDay([{ at: new Date(2026, 0, 6, 9) }, { at: new Date(2026, 0, 7, 9) }, { at: new Date(2026, 0, 7, 11) }], (x) => x.at, now);
  assert.deepEqual(groups.map((g) => [g.label, g.items.length]), [['Today', 2], ['Yesterday', 1]]);
  assert.equal(dayKey(new Date(2026, 0, 7, 23, 59)), '2026-01-07');
  assert.notEqual(dayLabel('2025-11-02', now), 'Today');
});

test('cards go to their column; done shows recent finished ones, newest first; archived ones never show', () => {
  const now = new Date('2026-01-20T12:00:00Z');
  const cols = columnsOf([
    node('a', { stage: 'inbox' }), node('b'), node('c', { stage: 'doing' }), node('d', { stage: 'weird' }),
    node('old', { done: true, completedAt: '2025-12-01T00:00:00Z' }),
    node('e', { done: true, completedAt: '2026-01-18T00:00:00Z' }), node('f', { done: true, completedAt: '2026-01-19T00:00:00Z' }),
    node('g', { done: true, completedAt: '2026-01-19T00:00:00Z', archivedAt: '2026-01-19T00:00:00Z' }),
  ], now);
  assert.deepEqual(Object.fromEntries(Object.entries(cols).map(([k, v]) => [k, v.map((n) => n.id)])), { inbox: ['a'], next: ['b', 'd'], doing: ['c'], done: ['f', 'e'] });
});

test('doing has a limit of three', () => {
  assert.equal(wip(2), 'ok');
  assert.equal(wip(3), 'full');
  assert.equal(wip(4), 'over');
});

test('dropping a card asks the server for exactly the change', () => {
  const a = node('a', { stage: 'inbox' });
  const b = node('b');
  const c = node('c');
  const done = node('d', { done: true, completedAt: '2026-01-19T00:00:00Z' });
  const cols = { inbox: [a], next: [b, c], doing: [], done: [done] };

  assert.deepEqual(dropRequests(a, 'next', 'c', cols), [
    { method: 'POST', url: '/api/items/a/stage', body: { stage: 'next' } },
    { method: 'POST', url: '/api/items/a/reorder', body: { before: 'c' } },
  ]);
  assert.deepEqual(dropRequests(a, 'doing', null, cols), [{ method: 'POST', url: '/api/items/a/stage', body: { stage: 'doing' } }]);
  assert.deepEqual(dropRequests(b, 'done', null, cols), [{ method: 'POST', url: '/api/items/b/complete' }]);
  assert.deepEqual(dropRequests(done, 'next', null, cols), [{ method: 'POST', url: '/api/items/d/stage', body: { stage: 'next' } }]);
  // Same column, same place: nothing to do; to the end: before nothing.
  assert.deepEqual(dropRequests(b, 'next', 'c', cols), []);
  assert.deepEqual(dropRequests(b, 'next', null, cols), [{ method: 'POST', url: '/api/items/b/reorder', body: { before: null } }]);
});

test('Alt+arrows move a card between columns and within one', () => {
  const cols = { inbox: [], next: [node('a'), node('b'), node('c')], doing: [], done: [] };
  assert.deepEqual(keyboardMove(cols.next[1], 'ArrowRight', cols), { column: 'doing', before: null });
  assert.deepEqual(keyboardMove(cols.next[1], 'ArrowUp', cols), { column: 'next', before: 'a' });
  assert.deepEqual(keyboardMove(cols.next[0], 'ArrowDown', cols), { column: 'next', before: 'c' });
  assert.equal(keyboardMove(cols.next[0], 'ArrowUp', cols), null);
});

test('card facts put the urgent ones first', () => {
  const now = new Date('2026-01-20T12:00:00Z');
  const facts = cardFacts(node('a', { deadline: '2026-01-19T12:00:00Z', totalCount: 3, doneCount: 1, timeSpentSeconds: 600 }), now);
  assert.deepEqual(facts.map((f) => f.kind), ['overdue', 'progress', 'time']);
  assert.equal(cardFacts(node('b', { deadline: '2026-01-21T00:00:00Z' }), now)[0].tone, 'warn');
});

test('the outline hides collapsed and (optionally) finished subtasks', () => {
  const roots = [node('a', { children: [node('a1'), node('a2', { done: true })] }), node('b')];
  assert.deepEqual(flatten(roots, new Set()).map((r) => r.node.id), ['a', 'b']);
  assert.deepEqual(flatten(roots, new Set(['a'])).map((r) => [r.node.id, r.depth]), [['a', 0], ['a1', 1], ['a2', 1], ['b', 0]]);
  assert.deepEqual(flatten(roots, new Set(['a']), false).map((r) => r.node.id), ['a', 'a1', 'b']);
});

test('Tab makes a task a subtask of the one above it; Shift+Tab takes it out again, right after its parent', () => {
  const roots = [node('a', { children: [node('a1'), node('a2')] }), node('b'), node('c')];
  const map = index(roots);
  assert.deepEqual(indentRequests(map, 'b'), [{ method: 'POST', url: '/api/items/b/move', body: { parentId: 'a', index: 2 } }]);
  assert.equal(indentRequests(map, 'a'), null);
  assert.deepEqual(indentRequests(map, 'a2'), [{ method: 'POST', url: '/api/items/a2/move', body: { parentId: 'a1', index: 0 } }]);
  assert.deepEqual(outdentRequests(map, 'a1', 'g1'), [
    { method: 'POST', url: '/api/items/a1/move', body: { toTopLevel: true, groupId: 'g1' } },
    { method: 'POST', url: '/api/items/a1/reorder', body: { before: 'b' } },
  ]);
  assert.equal(outdentRequests(map, 'b'), null);
});

test('a nested task outdents into its grandparent right after its parent', () => {
  const roots = [node('a', { children: [node('a1', { children: [node('x')] }), node('a2')] })];
  assert.deepEqual(outdentRequests(index(roots), 'x'), [{ method: 'POST', url: '/api/items/x/move', body: { parentId: 'a', index: 1 } }]);
});

test('Alt+Up/Down swap a task with its neighbour', () => {
  const map = index([node('a'), node('b'), node('c')]);
  assert.deepEqual(stepRequests(map, 'b', -1), [{ method: 'POST', url: '/api/items/b/reorder', body: { before: 'a' } }]);
  assert.deepEqual(stepRequests(map, 'b', 1), [{ method: 'POST', url: '/api/items/b/reorder', body: { before: null } }]);
  assert.equal(stepRequests(map, 'a', -1), null);
});

test('drops go before, inside or after a row, never into the task itself', () => {
  assert.equal(dropZone(2, 40), 'before');
  assert.equal(dropZone(20, 40), 'inside');
  assert.equal(dropZone(38, 40), 'after');
  const roots = [node('a', { children: [node('a1'), node('a2')] }), node('b')];
  const map = index(roots);
  assert.deepEqual(outlineDrop(map, 'b', 'a1', 'after'), [{ method: 'POST', url: '/api/items/b/move', body: { parentId: 'a', index: 1 } }]);
  assert.deepEqual(outlineDrop(map, 'a2', 'a1', 'before'), [{ method: 'POST', url: '/api/items/a2/move', body: { parentId: 'a', index: 0 } }]);
  assert.deepEqual(outlineDrop(map, 'b', 'a', 'inside'), [{ method: 'POST', url: '/api/items/b/move', body: { parentId: 'a', index: 2 } }]);
  assert.deepEqual(outlineDrop(map, 'a1', 'b', 'after', 'g'), [
    { method: 'POST', url: '/api/items/a1/move', body: { toTopLevel: true, groupId: 'g' } },
    { method: 'POST', url: '/api/items/a1/reorder', body: { before: null } },
  ]);
  assert.equal(outlineDrop(map, 'a', 'a1', 'inside'), null);
  assert.equal(outlineDrop(map, 'a', 'a', 'before'), null);
});

test('the break screen shows during a break, right when focus ends, and not after it was hidden', () => {
  const now = Date.parse('2026-01-05T10:00:00Z');
  const focus = { phase: 'focus', running: true, endsAt: '2026-01-05T09:59:50Z', completedFocusCount: 0, shortBreakMinutes: 5, longBreakMinutes: 15 };
  const predicted = breakState(focus, now);
  assert.ok(predicted.show);
  assert.equal(predicted.until, Date.parse('2026-01-05T10:04:50Z'));
  assert.equal(breakState({ ...focus, completedFocusCount: 3 }, now).long, true);
  // The long break comes after the user's own number of sessions (so the predicted break matches the server's).
  assert.equal(breakState({ ...focus, completedFocusCount: 2, focusesBeforeLongBreak: 2 }, now).long, false);
  assert.equal(breakState({ ...focus, completedFocusCount: 1, focusesBeforeLongBreak: 2 }, now).long, true);

  const onBreak = { phase: 'shortBreak', running: true, endsAt: '2026-01-05T10:04:50Z' };
  assert.ok(breakState(onBreak, now).show);
  assert.equal(breakState(onBreak, now, Date.parse('2026-01-05T10:04:50Z')).show, false);
  // Hidden while it was predicted (before the server switched to the break): still hidden once it's real.
  assert.equal(breakState(onBreak, now, predicted.key).show, false);
  assert.equal(breakState({ ...onBreak, running: false }, now).show, false);
  assert.equal(breakState({ phase: 'focus', running: true, endsAt: '2026-01-05T10:10:00Z' }, now).show, false);
  assert.equal(typeof tipFor('x'), 'string');
});

test('the palette ranks prefix and word-start matches first and needs every word', () => {
  assert.ok(score('Write report', 'wri') > score('Rewrite report', 'wri'));
  assert.equal(score('Write report', 'write budget'), -1);
  const ranked = rank([{ title: 'Go to Reports' }, { title: 'Write report' }, { title: 'Board' }, { title: 'Prepare slides' }], 'rep');
  assert.deepEqual(ranked.map((c) => c.title).slice(0, 2).sort(), ['Go to Reports', 'Write report']);
  assert.equal(ranked.at(-1).title, 'Prepare slides');
  assert.equal(ranked.length, 3);
  assert.deepEqual(rank([{ title: 'Prepare slides' }, { title: 'Report' }], 'rep').map((c) => c.title), ['Report', 'Prepare slides']);
});

test('chart scales are round and bars stack', () => {
  assert.deepEqual(niceScale(7.3, 4), { max: 8, ticks: [0, 2, 4, 6, 8] });
  assert.equal(niceScale(0).max, 1);
  const [[low, high]] = barLayout([[2, 2]], { width: 100, height: 100, max: 8 });
  assert.equal(low.h, 25);
  assert.equal(high.y, 50);
});

test('donut slices share the circle and arcs are closed paths', () => {
  const [a, b] = donutSlices([1, 3]);
  assert.equal(a.share, 0.25);
  assert.ok(Math.abs(b.end - Math.PI * 2) < 1e-9);
  assert.match(arcPath(50, 50, 40, 20, 0, Math.PI), /^M50 10A40 40 0 0 1 50 90L50 70A20 20 0 0 0 50 30Z$/);
  assert.match(arcPath(50, 50, 40, 20, 0, Math.PI * 2), /Z$/);
});

test('the timeline puts life, work, deadline and finish on one scale', () => {
  const [g] = ganttLayout([{ title: 'A', createdAt: '2026-01-01', completedAt: '2026-01-06', deadline: '2026-01-08', work: [{ start: '2026-01-02', end: '2026-01-03', source: 'manual' }] }], '2026-01-01', '2026-01-11', 100);
  assert.equal(g.life.x, 0);
  assert.equal(g.life.w, 50);
  assert.deepEqual(g.work.map((w) => [w.x, w.w]), [[10, 10]]);
  assert.equal(g.deadline, 70);
  assert.equal(g.done, 50);
  assert.deepEqual([0, 1, 50, 100].map((v) => heatLevel(v, 100)), [0, 1, 2, 4]);
});
