import { test } from 'node:test';
import assert from 'node:assert/strict';
import { relativeTime, priorityMeta, snoozeOptions, groupByDay, progressPercent, stepLabel, isSafeHttpUrl, greeting, metaChips, summaryLine, pomodoroFraction } from '../../src/TodoTracker.Server/wwwroot/js/format.js';

const now = new Date('2026-01-05T09:00:00Z');
const plus = (minutes) => new Date(now.getTime() + minutes * 60000);

test('relativeTime matches the C# RelativeTime rules', () => {
  assert.equal(relativeTime(plus(0), now), 'now');
  assert.equal(relativeTime(plus(5), now), 'in 5m');
  assert.equal(relativeTime(plus(90), now), 'in 1h 30m');
  assert.equal(relativeTime(plus(120), now), 'in 2h');
  assert.equal(relativeTime(plus(23 * 60 + 50), now), 'in 23h');
  assert.equal(relativeTime(plus(24 * 60), now), 'in 1d');
  assert.equal(relativeTime(plus(-5), now), '5m ago');
  assert.equal(relativeTime(plus(-3 * 24 * 60), now), '3d ago');
  assert.equal(relativeTime(new Date('2026-02-20T00:00:00Z'), now), 'Feb 20');
  assert.equal(relativeTime(null, now), '');
});

test('priorityMeta gives label and color for every priority', () => {
  assert.deepEqual(priorityMeta('critical'), { label: 'Critical', color: '#e11d48', rank: 3 });
  assert.equal(priorityMeta('high').color, '#f97316');
  assert.equal(priorityMeta('normal').color, '#3b82f6');
  assert.equal(priorityMeta('low').color, '#94a3b8');
  assert.equal(priorityMeta('bogus').label, 'Normal');
});

test('snoozeOptions offer short, hour, tomorrow-morning and +24h choices', () => {
  const local = new Date(2026, 0, 5, 14, 30); // local 14:30
  const options = snoozeOptions(local);
  assert.deepEqual(options.map((o) => o.label), ['15 min', '1 hour', '3 hours', 'Tomorrow 9:00', '+24 hours']);
  assert.equal(options[0].minutes, 15);
  assert.equal(options[4].minutes, 1440);
  const tomorrow = new Date(local.getTime() + options[3].minutes * 60000);
  assert.equal(tomorrow.getDate(), 6);
  assert.equal(tomorrow.getHours(), 9);
  assert.equal(tomorrow.getMinutes(), 0);
});

test('snoozeOptions: "tomorrow" just after midnight means this morning', () => {
  const lateNight = new Date(2026, 0, 6, 0, 30);
  const option = snoozeOptions(lateNight).find((o) => o.label === 'Tomorrow 9:00');
  const at = new Date(lateNight.getTime() + option.minutes * 60000);
  assert.equal(at.getDate(), 6);
  assert.equal(at.getHours(), 9);
});

test('groupByDay buckets timeline entries newest day first', () => {
  const entries = [
    { at: '2026-01-06T10:00:00Z', summary: 'b' },
    { at: '2026-01-06T08:00:00Z', summary: 'a' },
    { at: '2026-01-05T08:00:00Z', summary: 'c' },
  ];
  const days = groupByDay(entries, (d) => d.toISOString().slice(0, 10));
  assert.deepEqual(days.map((d) => d.day), ['2026-01-06', '2026-01-05']);
  assert.deepEqual(days[0].entries.map((e) => e.summary), ['b', 'a']);
});

test('progressPercent handles empty workstreams', () => {
  assert.equal(progressPercent(0, 0), 0);
  assert.equal(progressPercent(1, 3), 33);
  assert.equal(progressPercent(3, 3), 100);
});

test('stepLabel describes rollout position', () => {
  assert.equal(stepLabel({ stepNumber: 2, stepCount: 10 }), 'Step 2 of 10');
  assert.equal(stepLabel({ stepNumber: null }), '');
});

test('isSafeHttpUrl only allows http(s) links', () => {
  assert.equal(isSafeHttpUrl('https://example.com/a'), true);
  assert.equal(isSafeHttpUrl('http://127.0.0.1:5317'), true);
  assert.equal(isSafeHttpUrl('javascript:alert(1)'), false);
  assert.equal(isSafeHttpUrl('not a url'), false);
  assert.equal(isSafeHttpUrl(null), false);
});

test('greeting follows the local time of day', () => {
  assert.equal(greeting(new Date(2026, 0, 5, 6, 0)), 'Good morning');
  assert.equal(greeting(new Date(2026, 0, 5, 13, 0)), 'Good afternoon');
  assert.equal(greeting(new Date(2026, 0, 5, 19, 0)), 'Good evening');
  assert.equal(greeting(new Date(2026, 0, 5, 1, 0)), 'Still up?');
});

test('metaChips turn card facts into short, toned chips', () => {
  const chips = metaChips({
    breadcrumb: ['Feature X', 'Feature A'], stepNumber: 2, stepCount: 3,
    deadline: '2026-01-05T08:00:00Z', isOverdue: true, noteCount: 2,
  }, { waiting: false, now });
  assert.deepEqual(chips, [
    { text: 'Feature X › Feature A', tone: 'path' },
    { text: 'Step 2 of 3', tone: 'step' },
    { text: 'overdue 1h ago', tone: 'danger' },
    { text: '2 notes', tone: 'muted' },
  ]);

  const waiting = metaChips({ wakeAt: '2026-01-06T09:00:00Z', deadline: '2026-01-07T09:00:00Z', noteCount: 1 }, { waiting: true, now });
  assert.deepEqual(waiting, [
    { text: 'back in 1d', tone: 'info' },
    { text: 'due in 2d', tone: 'warn' },
    { text: '1 note', tone: 'muted' },
  ]);
});

test('summaryLine describes the board in a calm sentence', () => {
  assert.equal(summaryLine({ now: [], waiting: [] }), 'All clear. Nothing needs you right now.');
  assert.equal(summaryLine({ now: [{}], waiting: [] }), '1 thing to do now.');
  assert.equal(summaryLine({ now: [{}, { needsAttention: true }], waiting: [{}, {}, {}] }), '2 things to do now · 1 reminder · 3 waiting.');
});

test('pomodoroFraction shows elapsed share of the current phase', () => {
  const base = { focusMinutes: 25, shortBreakMinutes: 5, longBreakMinutes: 15 };
  assert.equal(pomodoroFraction({ ...base, phase: 'idle', remainingSeconds: 1500 }, now), 0);
  assert.equal(pomodoroFraction({ ...base, phase: 'focus', running: false, remainingSeconds: 750 }, now), 0.5);
  const endsAt = new Date(now.getTime() + 60_000).toISOString();
  assert.equal(pomodoroFraction({ ...base, phase: 'shortBreak', running: true, endsAt }, now), 0.8);
});
