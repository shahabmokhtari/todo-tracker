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

/** Make the task a subtask of the task above it (its previous visible sibling), last. */
export function indentRequests(map, id, showDone = true) {
  const at = map.get(id);
  if (!at) return null;
  const visible = at.siblings.filter((n) => showDone || !n.done || n.id === id);
  const prev = visible[visible.indexOf(at.node) - 1];
  if (!prev) return null;
  return [{ method: 'POST', url: `/api/items/${id}/move`, body: { parentId: prev.id, index: (prev.children ?? []).length } }];
}

/** Move the task out one level: right after its parent. */
export function outdentRequests(map, id, groupId) {
  const at = map.get(id);
  if (!at?.parent) return null;
  const parent = map.get(at.parent.id);
  if (parent.parent) {
    const index = parent.siblings.indexOf(parent.node) + 1;
    return [{ method: 'POST', url: `/api/items/${id}/move`, body: { parentId: parent.parent.id, index } }];
  }

  // To the top level: the server's top-level order spans every group, so place it by "before the next task".
  const next = parent.siblings[parent.siblings.indexOf(parent.node) + 1] ?? null;
  return [
    { method: 'POST', url: `/api/items/${id}/move`, body: { toTopLevel: true, groupId: groupId ?? parent.node.groupId } },
    { method: 'POST', url: `/api/items/${id}/reorder`, body: { before: next?.id ?? null } },
  ];
}

/** Alt+Up/Down: swap with the previous/next sibling. */
export function stepRequests(map, id, delta) {
  const at = map.get(id);
  if (!at) return null;
  const i = at.siblings.indexOf(at.node);
  const to = i + delta;
  if (to < 0 || to >= at.siblings.length) return null;
  const before = delta < 0 ? at.siblings[to].id : at.siblings[to + 1]?.id ?? null;
  return [{ method: 'POST', url: `/api/items/${id}/reorder`, body: { before } }];
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
  if (zone === 'inside') {
    return [{ method: 'POST', url: `/api/items/${draggedId}/move`, body: { parentId: targetId, index: (target.node.children ?? []).filter((c) => c.id !== draggedId).length } }];
  }

  const ids = target.siblings.map((n) => n.id).filter((x) => x !== draggedId);
  const at = ids.indexOf(targetId) + (zone === 'after' ? 1 : 0);
  if (target.parent) {
    return [{ method: 'POST', url: `/api/items/${draggedId}/move`, body: { parentId: target.parent.id, index: at } }];
  }

  const requests = dragged.parent ? [{ method: 'POST', url: `/api/items/${draggedId}/move`, body: { toTopLevel: true, groupId: groupId ?? target.node.groupId } }] : [];
  requests.push({ method: 'POST', url: `/api/items/${draggedId}/reorder`, body: { before: ids[at] ?? null } });
  return requests;
}
