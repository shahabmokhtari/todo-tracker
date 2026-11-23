// The Tasks tree's rules (outlinemodel.js) against the shared fixture the C# core generates (tests/fixtures/outline.json),
// the same one Swift and Kotlin run. The tree is built the way /api/tree sends it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { outlineRows, outlinePath, resolveZoom, openTo, newTaskPlace, shownTitle, initialShow, groupDropRequests, index } from '../../src/OneThing.Server/wwwroot/js/outlinemodel.js';

const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/outline.json', import.meta.url), 'utf8'));
const groups = fixture.groups.map((name) => ({ id: name, name }));
const byKey = new Map(fixture.tasks.map((t) => [t.key, t]));

/** The /api/tree reply: every top-level task that isn't archived (of one group, when picked), narrowed by a search. */
function treeFor(options, keep) {
  const nodeOf = (t, groupId) => ({
    id: t.key, title: t.title, groupId, done: fixture.nodes[t.key].state === 'done',
    state: fixture.nodes[t.key].state, doneCount: fixture.nodes[t.key].doneCount, totalCount: fixture.nodes[t.key].totalCount,
    children: fixture.tasks.filter((c) => c.parent === t.key && (!keep || keep.has(c.key))).map((c) => nodeOf(c, groupId)),
  });
  return fixture.tasks
    .filter((t) => !t.parent && !t.archived && (!options.group || t.group === options.group) && (!keep || keep.has(t.key)))
    .map((t) => nodeOf(t, t.group));
}

/** A zoom's parents, outermost first (what the view remembers from the path it last drew). */
function trailOf(key) {
  const trail = [];
  for (let at = byKey.get(key)?.parent; at; at = byKey.get(at).parent) trail.unshift(at);
  return trail;
}

const optionsOf = (o, sets = {}) => ({
  groupId: o.group, zoomId: o.zoom, show: o.show, query: o.query ?? '',
  expanded: new Set(sets.expanded ?? o.expanded), closedGroups: new Set(sets.closedGroups ?? o.closedGroups),
  trail: o.zoom ? trailOf(o.zoom) : [],
});

const plain = (rows) => rows.map(({ kind, id, depth, title, fullTitle, hasChildren, expanded, done, total, left, state, outdent }) =>
  ({ kind, id, depth, title, fullTitle, hasChildren, expanded, done, total, left, state, outdent }));
const sorted = (set, order) => order.filter((x) => set.has(x));

test('titles leave out the parent\'s name the way the fixture says', () => {
  for (const t of fixture.titles) assert.equal(shownTitle(t.title, t.parent, t.parentShown), t.shown, JSON.stringify(t));
});

for (const c of fixture.cases) {
  test(`outline: ${c.name}`, () => {
    const roots = treeFor(c.options, c.keep ? new Set(c.keep) : null);
    const options = optionsOf(c.options);
    assert.equal(resolveZoom(roots, options), c.zoom, 'zoom');
    assert.deepEqual(plain(outlineRows(roots, groups, options)), c.rows, 'rows');
    assert.deepEqual(outlinePath(roots, groups, options).map(({ node, ...p }) => p), c.path, 'path');
    assert.deepEqual(newTaskPlace(roots, groups, options), { parentId: c.newTask.parent, groupId: c.newTask.group }, 'new task');
    for (const [level, want] of Object.entries(c.openTo ?? {})) {
      const got = openTo(roots, groups, options, level);
      assert.deepEqual(sorted(got.expanded, fixture.tasks.map((t) => t.key)), want.expanded, `openTo ${level}: expanded`);
      assert.deepEqual(sorted(got.closedGroups, fixture.groups), want.closedGroups, `openTo ${level}: closed groups`);
      assert.deepEqual(plain(outlineRows(roots, groups, optionsOf(c.options, got))), want.rows, `openTo ${level}: rows`);
    }
  });
}

test('someone who had "Show finished" on starts at Everything; a choice made since wins', () => {
  assert.equal(initialShow(null, '1'), 'all');
  assert.equal(initialShow(null, '0'), 'todo');
  assert.equal(initialShow(null, null), 'todo');
  assert.equal(initialShow('waiting', '1'), 'waiting');
  assert.equal(initialShow('nonsense', null), 'todo');
});

test('dropping a task on a group row moves it to the top of that group', () => {
  const map = index([{ id: 'a', children: [{ id: 'a1', children: [] }] }]);
  assert.deepEqual(groupDropRequests(map, 'a1', 'g2'), [{ method: 'POST', url: '/api/items/a1/move', body: { toTopLevel: true, groupId: 'g2', index: 0 } }]);
  assert.equal(groupDropRequests(map, 'nope', 'g2'), null);
});
