// Pure formatting helpers shared by the dashboard, report page, and browser extension.
// Keep in sync with TodoTracker.Core.RelativeTime (C#) and RelativeTime.swift.

const PRIORITIES = {
  low: { label: 'Low', color: '#94a3b8', rank: 0 },
  normal: { label: 'Normal', color: '#3b82f6', rank: 1 },
  high: { label: 'High', color: '#f97316', rank: 2 },
  critical: { label: 'Critical', color: '#e11d48', rank: 3 },
};

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export function relativeTime(target, now = new Date()) {
  if (!target) return '';
  const t = target instanceof Date ? target : new Date(target);
  const deltaMs = t.getTime() - now.getTime();
  const minutes = Math.round(Math.abs(deltaMs) / 60000);
  if (minutes < 1) return 'now';
  let text;
  if (minutes < 60) text = `${minutes}m`;
  else if (minutes < 180) text = minutes % 60 === 0 ? `${minutes / 60}h` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
  else if (minutes < 24 * 60) text = `${Math.floor(minutes / 60)}h`;
  else if (minutes < 7 * 24 * 60) text = `${Math.floor(minutes / (24 * 60))}d`;
  else return `${MONTHS[t.getUTCMonth()]} ${t.getUTCDate()}`;
  return deltaMs > 0 ? `in ${text}` : `${text} ago`;
}

export function priorityMeta(priority) {
  return PRIORITIES[priority] ?? PRIORITIES.normal;
}

export function snoozeOptions(now = new Date()) {
  const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 9, 0, 0, 0);
  const minutesUntilTomorrow = Math.max(1, Math.round((tomorrow.getTime() - now.getTime()) / 60000));
  return [
    { label: '15 min', minutes: 15 },
    { label: '1 hour', minutes: 60 },
    { label: '3 hours', minutes: 180 },
    { label: 'Tomorrow 9:00', minutes: minutesUntilTomorrow },
    { label: '+24 hours', minutes: 1440 },
  ];
}

export function groupByDay(entries, dayKey = (d) => d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })) {
  const days = [];
  const byKey = new Map();
  const sorted = [...entries].sort((a, b) => new Date(b.at) - new Date(a.at));
  for (const entry of sorted) {
    const key = dayKey(new Date(entry.at));
    if (!byKey.has(key)) {
      const bucket = { day: key, entries: [] };
      byKey.set(key, bucket);
      days.push(bucket);
    }
    byKey.get(key).entries.push(entry);
  }
  return days;
}

export function progressPercent(done, total) {
  return total > 0 ? Math.round((done / total) * 100) : 0;
}

export function stepLabel(card) {
  return card && card.stepNumber ? `Step ${card.stepNumber} of ${card.stepCount}` : '';
}

export function isSafeHttpUrl(value) {
  if (!value) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}
