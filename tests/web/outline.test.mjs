// The Tasks tree's rules (outlinemodel.js) against the shared fixture the C# core generates (tests/fixtures/outline.json),
// the same one Swift and Kotlin run. The tree is built the way /api/tree sends it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
  outlineRows, outlinePath, resolveZoom, rememberedZoom, zoomUp, openTo, newTaskPlace, shownTitle, initialShow, groupDropRequests, index,
  besidesOf, indentRequests, outdentRequests, stepRequests, dropRequests,
} from '../../src/OneThing.Server/wwwroot/js/outlinemodel.js';

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
    assert.equal(rememberedZoom(roots, options), c.remembered, 'remembered zoom');
    assert.equal(zoomUp(roots, options), c.zoomUp, 'zoom up');
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

// ---- moves: planned the way outline.js does, made by a stand-in for the server (TaskBoard.Move, Reorder, ReorderAfter)

/** The whole board (archived tasks too), changed the way the server changes it. */
function serverBoard() {
  const tasks = new Map(fixture.tasks.map((t) => [t.key, { key: t.key, group: t.group ?? null, parent: t.parent ?? null, children: [] }]));
  const roots = [];
  for (const t of tasks.values()) (t.parent ? tasks.get(t.parent).children : roots).push(t);
  const listOf = (t) => (t.parent ? tasks.get(t.parent).children : roots);
  const take = (t) => listOf(t).splice(listOf(t).indexOf(t), 1);
  const make = ({ url, body }) => {
    if (url === '/api/items') {
      const t = { key: 'new', group: body.parentId ? null : body.groupId, parent: body.parentId ?? null, children: [] };
      tasks.set('new', t);
      listOf(t).push(t);
      return 'new';
    }
    const [, id, what] = url.match(/^\/api\/items\/([^/]+)\/(move|reorder)$/);
    const t = tasks.get(id);
    if (what === 'move') {
      take(t);
      if (body.parentId) t.parent = body.parentId;
      else { t.parent = null; t.group = body.groupId ?? t.group; }
      const list = listOf(t);
      list.splice(Math.min(body.index ?? list.length, list.length), 0, t);
    } else {
      const list = listOf(t);
      take(t);
      const at = list.indexOf(tasks.get(body.before ?? body.after));
      list.splice(body.after ? at + 1 : at, 0, t);
    }
    return id;
  };
  const tree = () => fixture.groups.flatMap((g) => {
    const lines = [g];
    const walk = (list, depth) => list.forEach((t) => { lines.push(`${'  '.repeat(depth)}${t.key}`); walk(t.children, depth + 1); });
    walk(roots.filter((r) => r.group === g), 1);
    return lines;
  });
  return { make, tree };
}

/** The requests outline.js sends for a move (null: it doesn't make it). */
function plan(roots, options, move) {
  const map = index(roots);
  const besides = (id) => besidesOf(roots, options, id);
  switch (move.do) {
    case 'indent': return indentRequests(map, move.task, besides(move.task));
    case 'outdent': {
      // Only from a row that can move out here (not at the top of a zoom): what the keys and the Move menu check.
      const all = openTo(roots, groups, options, 'all');
      const row = outlineRows(roots, groups, { ...options, ...all }).find((r) => r.id === move.task);
      return row?.outdent ? outdentRequests(map, move.task, options.groupId) : null;
    }
    case 'up': return stepRequests(map, move.task, -1, besides(move.task));
    case 'down': return stepRequests(map, move.task, 1, besides(move.task));
    case 'drop': return dropRequests(map, move.task, move.target, move.zone, options.groupId);
    case 'dropOnGroup': return groupDropRequests(map, move.task, move.group);
    case 'newTask': {
      // As outline.js's create(): added, then moved first where it goes.
      const place = newTaskPlace(roots, groups, options);
      const add = { method: 'POST', url: '/api/items', body: place.parentId ? { title: 'New', parentId: place.parentId } : { title: 'New', groupId: place.groupId } };
      return [add, { method: 'POST', url: '/api/items/new/move', body: place.parentId ? { parentId: place.parentId, index: 0 } : { toTopLevel: true, groupId: place.groupId, index: 0 } }];
    }
    default: throw new Error(move.do);
  }
}

test('the board the moves start from is the fixture\'s', () => assert.deepEqual(serverBoard().tree(), fixture.tree));

for (const m of fixture.moves) {
  test(`move: ${m.name}`, () => {
    const o = { ...m.options, expanded: [], closedGroups: [] };
    const roots = treeFor(o, m.keep ? new Set(m.keep) : null);
    const requests = plan(roots, optionsOf(o), m.move);
    if (m.tree === 'refused') {
      assert.equal(requests, null);
      return;
    }
    assert.ok(requests, 'planned');
    const server = serverBoard();
    requests.forEach(server.make);
    assert.deepEqual(server.tree(), m.tree);
  });
}

test('dropping a task on a group row moves it to the top of that group', () => {
  const map = index([{ id: 'a', children: [{ id: 'a1', children: [] }] }]);
  assert.deepEqual(groupDropRequests(map, 'a1', 'g2'), [{ method: 'POST', url: '/api/items/a1/move', body: { toTopLevel: true, groupId: 'g2', index: 0 } }]);
  assert.equal(groupDropRequests(map, 'nope', 'g2'), null);
});
