// The outline: a task tree shown as rows, and the server requests for indenting, outdenting, reordering and dropping.
// Nodes come from /api/tree (top-level tasks of one group, with all their subtasks).

/** Visible rows, depth-first: collapsed tasks hide their subtasks; finished ones are hidden unless `showDone`. */
export function flatten(roots, expanded, showDone = true) {
  const rows = [];
  const walk = (nodes, depth, parent) => {
    for (const node of nodes) {
      if (!showDone && node.done) continue;
      const children = node.children ?? [];
      const visibleChildren = showDone ? children : children.filter((c) => !c.done);
      const open = expanded.has(node.id);
      rows.push({ node, depth, parent, hasChildren: visibleChildren.length > 0, expanded: open });
      if (open) walk(children, depth + 1, node);
    }
  };
  walk(roots, 0, null);
  return rows;
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

const isInside = (map, id, ancestorId) => {
  for (let at = map.get(id); at; at = at.parent ? map.get(at.parent.id) : null) {
    if (at.node.id === ancestorId) return true;
  }
  return false;
};

// Moves name a neighbouring task (before / after it) rather than a position: the tree on screen may leave some tasks
// out (a filter, finished ones hidden), and the server places by the full list.

/** Make the task a subtask of the task above it (its previous visible sibling), last. */
export function indentRequests(map, id, showDone = true) {
  const at = map.get(id);
  if (!at) return null;
  const visible = at.siblings.filter((n) => showDone || !n.done || n.id === id);
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
export function stepRequests(map, id, delta, showDone = true) {
  const at = map.get(id);
  if (!at) return null;
  const visible = at.siblings.filter((n) => showDone || !n.done || n.id === id);
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
  return dragged.parent ? [move({ toTopLevel: true, groupId: groupId ?? target.node.groupId }), place] : [place];
}