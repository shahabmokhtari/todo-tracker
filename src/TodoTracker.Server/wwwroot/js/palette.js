// The command palette (Ctrl+K): type to find a task or an action; arrows and Enter to go.

/** How well `query` matches `text` (higher is better), or -1: all words must appear; whole-word and prefix hits rank up. */
export function score(text, query) {
  const t = String(text ?? '').toLowerCase();
  const words = String(query ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return 0;
  let total = 0;
  for (const word of words) {
    const at = t.indexOf(word);
    if (at < 0) return -1;
    total += 10;
    if (at === 0) total += 6;
    else if (/[\s\-_/·›]/.test(t[at - 1])) total += 4;
  }
  return total - Math.min(t.length, 80) / 40;
}

/** The commands that match, best first (a stable order for equal scores). */
export function rank(commands, query, limit = 8) {
  return commands
    .map((c, i) => ({ c, i, s: score(`${c.title} ${c.keywords ?? ''}`, query) }))
    .filter((x) => x.s >= 0)
    .sort((a, b) => b.s - a.s || a.i - b.i)
    .slice(0, limit)
    .map((x) => x.c);
}
