// The board: which column each card is in, how full Doing is, and what a drop means.

export const COLUMNS = [
  { id: 'inbox', name: 'Inbox', hint: 'Captured, not decided yet' },
  { id: 'next', name: 'Next', hint: 'Ready to pick up' },
  { id: 'doing', name: 'Doing', hint: 'In progress (keep it to 3)' },
  { id: 'done', name: 'Done', hint: 'Finished recently' },
];

/** Doing is kept small on purpose: more than this at once splits attention. */
export const WIP_LIMIT = 3;

/** How long finished cards stay on the board (then they're in Done & archive). */
export const DONE_DAYS = 14;

/** Cards per column, in tree order (the order people arranged). Done shows recent finished cards, newest first. */
export function columnsOf(nodes, now = new Date(), doneDays = DONE_DAYS) {
  const columns = { inbox: [], next: [], doing: [], done: [] };
  const since = new Date(now).getTime() - doneDays * 86_400_000;
  for (const node of nodes) {
    if (node.archivedAt) continue;
    if (node.done) {
      if (node.completedAt && new Date(node.completedAt).getTime() >= since) columns.done.push(node);
    } else {
      (columns[node.stage] ?? columns.next).push(node);
    }
  }
  columns.done.sort((a, b) => new Date(b.completedAt) - new Date(a.completedAt));
  return columns;
}

/** How Doing is: under the limit (ok), at it (full), or over it (over). */
export function wip(count, limit = WIP_LIMIT) {
  if (count > limit) return 'over';
  return count === limit ? 'full' : 'ok';
}

/**
 * The requests that move `card` to `column`, placed before `beforeId` (a card in that column). Without a place, a card
 * from another column keeps its spot in the one shared order (so moving it on the board doesn't demote it in Today);
 * within its column, no place means last. Moving to Done finishes it; out of Done reopens it (by setting its column).
 */
export function dropRequests(card, column, beforeId, columns) {
  const requests = [];
  const from = card.done ? 'done' : card.stage;
  if (column === 'done') {
    if (from !== 'done') requests.push({ method: 'POST', url: `/api/items/${card.id}/complete` });
    return requests;
  }

  if (from !== column) requests.push({ method: 'POST', url: `/api/items/${card.id}/stage`, body: { stage: column } });
  const order = (columns?.[column] ?? []).map((c) => c.id).filter((id) => id !== card.id);
  const target = beforeId && order.includes(beforeId) ? beforeId : null;
  const currentIndex = (columns?.[column] ?? []).findIndex((c) => c.id === card.id);
  const currentNext = currentIndex >= 0 ? columns[column][currentIndex + 1]?.id ?? null : undefined;
  // Only reorder when the place changes (or the card came from another column and a place was chosen).
  if ((from === column && target !== currentNext) || (from !== column && target)) {
    requests.push({ method: 'POST', url: `/api/items/${card.id}/reorder`, body: { before: target } });
  }

  return requests;
}

/** The card after which keyboard Alt+arrows land: left/right changes column, up/down moves within it. */
export function keyboardMove(card, key, columns) {
  const order = COLUMNS.map((c) => c.id);
  const from = card.done ? 'done' : card.stage;
  const list = columns[from] ?? [];
  const index = list.findIndex((c) => c.id === card.id);
  if (key === 'ArrowLeft' || key === 'ArrowRight') {
    const to = order[order.indexOf(from) + (key === 'ArrowLeft' ? -1 : 1)];
    return to ? { column: to, before: null } : null;
  }

  if (from === 'done') return null;
  if (key === 'ArrowUp' && index > 0) return { column: from, before: list[index - 1].id };
  if (key === 'ArrowDown' && index < list.length - 1) return { column: from, before: list[index + 2]?.id ?? null };
  return null;
}

/** Facts on a card, most urgent first: overdue, due soon, waiting, progress, time. */
export function cardFacts(node, now = new Date()) {
  const facts = [];
  const t = new Date(now).getTime();
  if (node.deadline && !node.done) {
    const due = new Date(node.deadline).getTime();
    if (due < t) facts.push({ kind: 'overdue', tone: 'danger' });
    else if (due - t < 2 * 86_400_000) facts.push({ kind: 'due', tone: 'warn' });
    else facts.push({ kind: 'due', tone: 'muted' });
  }

  if (!node.done && node.nextActionAt && new Date(node.nextActionAt).getTime() > t) facts.push({ kind: 'waiting', tone: 'info' });
  if (node.totalCount > 0) facts.push({ kind: 'progress', tone: node.doneCount === node.totalCount ? 'ok' : 'muted', done: node.doneCount, total: node.totalCount });
  if (node.timeSpentSeconds > 0 || node.timing) facts.push({ kind: 'time', tone: node.timing ? 'accent' : 'muted', seconds: node.timeSpentSeconds });
  return facts;
}
