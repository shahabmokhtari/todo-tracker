// Pure helpers for reordering lists (Do now, subtasks): computing the new order from a drag, an arrow, or a key.

/** `ids` with `id` moved to position `to` (clamped). */
export function moveTo(ids, id, to) {
  const from = ids.indexOf(id);
  if (from < 0) return [...ids];
  const rest = ids.filter((x) => x !== id);
  const at = Math.max(0, Math.min(to, rest.length));
  return [...rest.slice(0, at), id, ...rest.slice(at)];
}

/** `ids` with `id` moved one step up (-1) or down (+1); unchanged at the ends. */
export function step(ids, id, delta) {
  const from = ids.indexOf(id);
  return from < 0 ? [...ids] : moveTo(ids, id, from + delta);
}

/** The new order after dropping `dragged` before (or after) `target`. */
export function drop(ids, dragged, target, after = false) {
  if (dragged === target || !ids.includes(dragged) || !ids.includes(target)) return [...ids];
  const rest = ids.filter((x) => x !== dragged);
  const at = rest.indexOf(target) + (after ? 1 : 0);
  return [...rest.slice(0, at), dragged, ...rest.slice(at)];
}

/** For the server's "put before this sibling" (null = last): the sibling that follows `id` in `order`. */
export function beforeOf(order, id) {
  const i = order.indexOf(id);
  return i >= 0 && i + 1 < order.length ? order[i + 1] : null;
}

/** Whether two orders differ (to skip saving when a drop changed nothing). */
export function changed(a, b) {
  return a.length !== b.length || a.some((x, i) => x !== b[i]);
}
