// The outline: a task tree shown as rows, and the server requests for indenting, outdenting, reordering and dropping.
// Nodes come from /api/tree (top-level tasks of one group or of all, with their subtasks; a search already narrowed
// them to the matches, their parents and subtasks). The rules for what shows are the same as the C# core's Outline
// (and Swift's and Kotlin's), checked against tests/fixtures/outline.json.

const SEPARATORS = [':', ' -', ' –', ' —'];

/**
 * A subtask's title without its parent's name in front: when it starts with the parent's title (any case) followed by
 * ':', ' -', ' –' or ' —', the rest (leading spaces dropped), unless that's empty. When the parent's own title is
 * shown shorter (`parentShown`), that name counts too.
 */
export function shownTitle(title, parentTitle, parentShown = null) {
  const shown = trimmed(title, parentTitle);
  return shown === title && parentShown != null && parentShown !== parentTitle ? trimmed(title, parentShown) : shown;
}

function trimmed(title, parentTitle) {
  if (!parentTitle || !startsIgnoringCase(title, parentTitle)) return title;
  const after = title.slice(parentTitle.length);
  const sep = SEPARATORS.find((s) => after.startsWith(s));
  if (!sep) return title;
  const rest = after.slice(sep.length).replace(SPACES, '');
  return rest ? rest : title;
}

// Leading spaces as C#'s char.IsWhiteSpace has them (not trimStart's: U+FEFF isn't one, U+0085 is).
const SPACES = /^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+/;

/**
 * A letter in upper case the way C#'s OrdinalIgnoreCase compares (the same everywhere): one code point at a time, by
 * the simple mapping (one that would make it longer, like ß, leaves it as it is), no locale (ı and ſ stay as they
 * are), the Greek letters with iota below to their capitals.
 */
function upperOf(cp) {
  if (cp === 0x131 || cp === 0x17f) return cp;
  if (cp >= 0x1f80 && cp <= 0x1faf && (cp & 0xf) < 8) return cp + 8;
  if (cp === 0x1fb3 || cp === 0x1fc3 || cp === 0x1ff3) return cp + 9;
  const one = String.fromCodePoint(cp);
  const upper = one.toUpperCase();
  return upper.length === one.length && String.fromCodePoint(upper.codePointAt(0)) === upper ? upper.codePointAt(0) : cp;
}

/** Whether `text` starts with `prefix`, case aside (C#'s StartsWith with OrdinalIgnoreCase: no normalization). */
export function startsIgnoringCase(text, prefix) {
  if (text.length < prefix.length) return false;
  for (let i = 0; i < prefix.length;) {
    const a = text.codePointAt(i);
    const b = prefix.codePointAt(i);
    const size = b > 0xffff ? 2 : 1;
    if ((a > 0xffff ? 2 : 1) !== size || (a !== b && upperOf(a) !== upperOf(b))) return false;
    i += size;
  }
  return true;
}

/** The Show choices, in the order the menu lists them. */
export const SHOW_CHOICES = [['todo', 'To do'], ['now', 'Can do now'], ['waiting', 'Waiting'], ['all', 'Everything']];

/** The first Show choice on this device: what was picked, else Everything for someone who had "Show finished" on. */
export function initialShow(saved, oldShowDone) {
  if (SHOW_CHOICES.some(([id]) => id === saved)) return saved;
  return oldShowDone === '1' ? 'all' : 'todo';
}

const searching = (options) => !!options.query?.trim();

/** What's shown with these options: which tasks are kept (memoized), by id. */
function shownOf(roots, options) {
  const map = index(roots);
  const memo = new Map();
  const itself = (node) => {
    switch (options.show) {
      case 'all': return true;
      case 'now': return node.state === 'actionable';
      case 'waiting': return node.state === 'waiting' || node.state === 'locked';
      default: return !node.done;
    }
  };
  const kept = (node) => {
    if (!memo.has(node.id)) memo.set(node.id, itself(node) || (node.children ?? []).some(kept));
    return memo.get(node.id);
  };
  return { map, kept };
}

/** Whether a task is shown with these options (the Show choice; a search already narrowed the tree). */
export function keptOf(roots, options) {
  return shownOf(roots, options).kept;
}

function leaves(node) {
  return { done: node.doneCount ?? 0, total: node.totalCount ?? 0 };
}

/** Not in the tree (a search left it out, or it's gone): its nearest parent still there, from the path last seen. */
function trailOf(map, options) {
  const found = [...(options.trail ?? [])].reverse().find((id) => map.has(id));
  return found ? map.get(found) : null;
}

/** The zoom asked, if it can be zoomed into: the task (its parent when it has no subtasks), in the group picked. */
function zoomable(at, options) {
  if (!at || (options.groupId && at.node.groupId !== options.groupId)) return null;
  return (at.node.children ?? []).length ? at.node : at.parent;
}

function zoomOf(shown, options) {
  if (!options.zoomId) return null;
  return zoomable(shown.map.get(options.zoomId) ?? trailOf(shown.map, options), options);
}

/**
 * The zoom drawn: the asked task (its parent when it has no subtasks); while a search leaves it out, its nearest parent
 * the search keeps (for now); null (the top) when it's gone or in another group than the one picked. The Show choice
 * never moves it: it only narrows what's under it. `options.trail` (the zoom's parents, outermost first) finds a parent
 * when the task itself isn't in the tree.
 */
export function resolveZoom(roots, options) {
  return zoomOf(shownOf(roots, options), options)?.id ?? null;
}

/**
 * The zoom to keep (the address): the asked task unless it's really gone (deleted, archived, in another group than the
 * one picked: its nearest parent still there, else null; without subtasks: its parent). While searching, a task the
 * search leaves out is kept as it is (for when the search is cleared).
 */
export function rememberedZoom(roots, options) {
  if (!options.zoomId) return null;
  const map = index(roots);
  const at = map.get(options.zoomId);
  if (!at && searching(options)) return options.zoomId;
  return zoomable(at ?? trailOf(map, options), options)?.id ?? null;
}

/** The zoom one level up (Escape): the zoomed task's parent, the part of the path before it; null from the first level. */
export function zoomUp(roots, options) {
  const shown = shownOf(roots, options);
  const zoom = zoomOf(shown, options);
  return zoom ? shown.map.get(zoom.id).parent?.id ?? null : null;
}

/**
 * The rows to draw, in order: { kind: 'group'|'task', id, depth, title, fullTitle, hasChildren, expanded, done, total,
 * left, state, outdent } plus `node` (task rows: the tree node, `parent` its parent node) or `group` (group rows).
 * options: { groupId, zoomId, show: 'todo'|'now'|'waiting'|'all', query, expanded: Set, closedGroups: Set, trail }.
 */
export function outlineRows(roots, groups, options) {
  const shown = shownOf(roots, options);
  const { kept } = shown;
  const expanded = options.expanded ?? new Set();
  const closed = options.closedGroups ?? new Set();
  const search = searching(options);
  const zoom = zoomOf(shown, options);
  const rows = [];
  const walk = (nodes, depth, parent, parentShown) => {
    for (const node of nodes) {
      if (!kept(node)) continue;
      const children = (node.children ?? []).filter(kept);
      const open = children.length > 0 && (search || expanded.has(node.id));
      const { done, total } = leaves(node);
      const title = shownTitle(node.title, parent?.title, parentShown);
      rows.push({
        kind: 'task', id: node.id, depth, title, fullTitle: node.title,
        hasChildren: children.length > 0, expanded: open, done, total, left: total - done, state: node.state ?? null,
        outdent: !!parent && parent !== zoom, node, parent, parentShown,
      });
      if (open) walk(children, depth + 1, node, title);
    }
  };

  if (zoom) {
    walk(zoom.children ?? [], 0, zoom, chainOf(shown.map, zoom).at(-1).shown);
    return rows;
  }
  if (options.groupId) {
    walk(roots.filter((r) => r.groupId === options.groupId), 0, null, null);
    return rows;
  }

  // Empty groups stay (somewhere to drop into) unless a search or a narrowing choice is on.
  const hideEmpty = search || options.show === 'now' || options.show === 'waiting';
  for (const group of groups) {
    const tasks = roots.filter((r) => r.groupId === group.id);
    const hasChildren = tasks.some(kept);
    if (!hasChildren && hideEmpty) continue;
    let done = 0;
    let total = 0;
    for (const t of tasks) {
      const l = leaves(t);
      if (l.total > 0) { done += l.done; total += l.total; } else { done += t.done ? 1 : 0; total += 1; }
    }
    const open = search || !closed.has(group.id);
    rows.push({ kind: 'group', id: group.id, depth: 0, title: group.name, fullTitle: group.name, hasChildren, expanded: open, done, total, left: total - done, state: null, outdent: false, group });
    if (open) walk(tasks, 1, null, null);
  }
  return rows;
}

/** The task and its parents, outermost first, each with its title as shown under the one before. */
function chainOf(map, node) {
  const nodes = [];
  for (let at = node; at; at = map.get(at.id).parent) nodes.unshift(at);
  const chain = [];
  for (const n of nodes) chain.push({ node: n, shown: chain.length ? shownTitle(n.title, chain.at(-1).node.title, chain.at(-1).shown) : n.title });
  return chain;
}

/** The path above the tree while zoomed ([] when not): All groups, the group, each parent, the zoomed task. */
export function outlinePath(roots, groups, options) {
  const shown = shownOf(roots, options);
  const zoom = zoomOf(shown, options);
  if (!zoom) return [];
  const group = groups.find((g) => g.id === zoom.groupId);
  const parts = [{ kind: 'all', id: null, title: 'All groups', done: 0, total: 0 }, { kind: 'group', id: zoom.groupId, title: group?.name ?? '', done: 0, total: 0 }];
  for (const { node, shown: title } of chainOf(shown.map, zoom)) parts.push({ kind: 'task', id: node.id, title, ...leaves(node), node });
  return parts;
}

/**
 * "Open to" a level ('groups' | 'tasks' | 'subtasks' | 'all'): the open tasks and closed groups afterwards. Only
 * what's in view changes (under the zoom, the group picked, or every group).
 */
export function openTo(roots, groups, options, level) {
  const zoom = zoomOf(shownOf(roots, options), options);
  const top = zoom ? zoom.children ?? [] : options.groupId ? roots.filter((r) => r.groupId === options.groupId) : roots;
  const expanded = new Set(options.expanded ?? []);
  const each = (nodes, depth) => nodes.forEach((node) => {
    const open = (node.children ?? []).length > 0 && (level === 'all' || (level === 'subtasks' && depth === 0));
    if (open) expanded.add(node.id);
    else expanded.delete(node.id);
    each(node.children ?? [], depth + 1);
  });
  each(top, 0);
  const closedGroups = zoom || options.groupId ? new Set(options.closedGroups ?? []) : new Set(level === 'groups' ? groups.map((g) => g.id) : []);
  return { expanded, closedGroups };
}

/** Where New task adds: inside the zoomed task, else at the top of the group picked (all groups: the first group). */
export function newTaskPlace(roots, groups, options) {
  const zoom = zoomOf(shownOf(roots, options), options);
  return { parentId: zoom?.id ?? null, groupId: zoom?.groupId ?? options.groupId ?? groups[0]?.id ?? null };
}

/**
 * Dropping a task on a group row: to the top of that group, at its top level. Null for a task not in the tree, and for
 * the group's first task dropped on its own group (nothing would change).
 */
export function groupDropRequests(map, draggedId, groupId) {
  const dragged = map.get(draggedId);
  if (!dragged) return null;
  if (!dragged.parent && dragged.node.groupId === groupId && dragged.siblings.find((n) => n.groupId === groupId) === dragged.node) return null;
  return [{ method: 'POST', url: `/api/items/${draggedId}/move`, body: { toTopLevel: true, groupId, index: 0 } }];
}

/**
 * Which siblings are on screen next to a task, for moving it among them: those the Show choice keeps (a search already
 * narrowed the tree), and at the top level only its own group's (under All groups each group is a tree of its own).
 */
export function besidesOf(roots, options, id) {
  const kept = keptOf(roots, options);
  const group = index(roots).get(id)?.node.groupId;
  return (n) => kept(n) && n.groupId === group;
}


/** Every task in the tree by id, with its parent (null at the top) and its siblings list. */
export function index(roots) {
  const map = new Map();
  const walk = (nodes, parent) => nodes.forEach((node) => {
    map.set(node.id, { node, parent, siblings: nodes });
    walk(node.children ?? [], node);
  });
  walk(roots, null);
  return map;
}

/** `shown`: true (every task), false (unfinished ones), or a function saying whether a node is on screen. */
const isShown = (shown, node) => (typeof shown === 'function' ? shown(node) : shown || !node.done);

const isInside = (map, id, ancestorId) => {
  for (let at = map.get(id); at; at = at.parent ? map.get(at.parent.id) : null) {
    if (at.node.id === ancestorId) return true;
  }
  return false;
};

// Moves name a neighbouring task (before / after it) rather than a position: the tree on screen may leave some tasks
// out (a filter, finished ones hidden), and the server places by the full list.

/** Make the task a subtask of the task above it (its previous visible sibling), last. */
export function indentRequests(map, id, shown = true) {
  const at = map.get(id);
  if (!at) return null;
  const visible = at.siblings.filter((n) => isShown(shown, n) || n.id === id);
  const prev = visible[visible.indexOf(at.node) - 1];
  if (!prev) return null;
  return [{ method: 'POST', url: `/api/items/${id}/move`, body: { parentId: prev.id } }];
}

/** Move the task out one level: right after its parent. */
export function outdentRequests(map, id, groupId) {
  const at = map.get(id);
  if (!at?.parent) return null;
  const parent = map.get(at.parent.id);
  const move = parent.parent
    ? { method: 'POST', url: `/api/items/${id}/move`, body: { parentId: parent.parent.id } }
    : { method: 'POST', url: `/api/items/${id}/move`, body: { toTopLevel: true, groupId: groupId ?? parent.node.groupId } };
  return [move, { method: 'POST', url: `/api/items/${id}/reorder`, body: { after: parent.node.id } }];
}

/** Alt+Up/Down: swap with the previous/next sibling on screen (finished ones may be hidden: they're stepped over). */
export function stepRequests(map, id, delta, shown = true) {
  const at = map.get(id);
  if (!at) return null;
  const visible = at.siblings.filter((n) => isShown(shown, n) || n.id === id);
  const to = visible.indexOf(at.node) + delta;
  if (to < 0 || to >= visible.length) return null;
  const neighbour = visible[to];
  return [{ method: 'POST', url: `/api/items/${id}/reorder`, body: delta < 0 ? { before: neighbour.id } : { after: neighbour.id } }];
}
/** Where a drop lands on a row: the top quarter is before it, the bottom quarter after it, the middle inside it. */
export function dropZone(offsetY, height) {
  if (offsetY < height * 0.25) return 'before';
  if (offsetY > height * 0.75) return 'after';
  return 'inside';
}

/** The requests that put `draggedId` before, after or inside `targetId`; null when it can't go there. */
export function dropRequests(map, draggedId, targetId, zone, groupId) {
  if (draggedId === targetId || isInside(map, targetId, draggedId)) return null;
  const dragged = map.get(draggedId);
  const target = map.get(targetId);
  if (!dragged || !target) return null;
  const move = (body) => ({ method: 'POST', url: `/api/items/${draggedId}/move`, body });
  if (zone === 'inside') return [move({ parentId: targetId })];

  const place = { method: 'POST', url: `/api/items/${draggedId}/reorder`, body: zone === 'after' ? { after: targetId } : { before: targetId } };
  if (target.parent) return [move({ parentId: target.parent.id }), place];
  // Under All groups the top level holds every group's tasks: next to a task of another group moves it there too.
  const otherGroup = !groupId && dragged.node.groupId !== target.node.groupId;
  return dragged.parent || otherGroup ? [move({ toTopLevel: true, groupId: groupId ?? target.node.groupId }), place] : [place];
}