// Time spent, as people read it, and local calendar days (for grouping and reports).

/** "45 s", "25 min", "1 h 05 min", "12 h" (whole hours drop the minutes). */
export function duration(seconds) {
  const s = Math.max(0, Math.round(seconds ?? 0));
  if (s < 60) return `${s} s`;
  const minutes = Math.round(s / 60);
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m === 0 ? `${h} h` : `${h} h ${String(m).padStart(2, '0')} min`;
}

/** A compact running clock: "4:07", "1:04:07". */
export function clock(seconds) {
  const s = Math.max(0, Math.floor(seconds ?? 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
}

/** Hours with one decimal for chart axes: 1.5 h. */
export const hours = (seconds) => Math.round((seconds / 3600) * 10) / 10;

/** The local calendar day of a time, as yyyy-mm-dd. */
export function dayKey(value) {
  const d = new Date(value);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** yyyy-mm-dd of the local day `offset` days from `now` (negative: in the past). */
export function dayOffset(now, offset) {
  const d = new Date(now);
  d.setHours(12, 0, 0, 0);
  d.setDate(d.getDate() + offset);
  return dayKey(d);
}

/** How a day reads in a list: Today, Yesterday, the weekday this week, else the date. */
export function dayLabel(key, now = new Date()) {
  if (key === dayKey(now)) return 'Today';
  if (key === dayOffset(now, -1)) return 'Yesterday';
  const [y, m, d] = key.split('-').map(Number);
  const date = new Date(y, m - 1, d, 12);
  const days = Math.round((new Date(now).setHours(12, 0, 0, 0) - date) / 86_400_000);
  if (days > 1 && days < 7) return date.toLocaleDateString(undefined, { weekday: 'long' });
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: date.getFullYear() === new Date(now).getFullYear() ? undefined : 'numeric' });
}

/** Items grouped by the local day of `when(item)`, newest day first, keeping each day's order. */
export function byDay(items, when, now = new Date()) {
  const groups = new Map();
  for (const item of items) {
    const key = dayKey(when(item));
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(item);
  }
  return [...groups.entries()].sort((a, b) => (a[0] < b[0] ? 1 : -1)).map(([key, list]) => ({ key, label: dayLabel(key, now), items: list }));
}

/** A datetime-local input value for a time (local). */
export function toLocalInput(value) {
  if (!value) return '';
  const d = new Date(value);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
