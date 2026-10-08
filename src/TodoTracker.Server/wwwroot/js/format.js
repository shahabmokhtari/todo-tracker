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
  // Before 4:00 "tomorrow" still means "when I wake up" (same rule as QuickCaptureParser.TomorrowMorning).
  const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + (now.getHours() < 4 ? 0 : 1), 9, 0, 0, 0);
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

export function greeting(date = new Date()) {
  const hour = date.getHours();
  if (hour < 4) return 'Still up?';
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

/** Card facts as short chips with a tone (path, step, info, warn, danger, muted) for consistent styling. */
export function metaChips(card, { waiting = false, now = new Date() } = {}) {
  const chips = [];
  if (card.breadcrumb?.length) chips.push({ text: card.breadcrumb.join(' › '), tone: 'path' });
  const step = stepLabel(card);
  if (step) chips.push({ text: step, tone: 'step' });
  if (waiting && card.wakeAt) chips.push({ text: `back ${relativeTime(card.wakeAt, now)}`, tone: 'info' });
  if (card.deadline) {
    chips.push(card.isOverdue
      ? { text: `overdue ${relativeTime(card.deadline, now)}`, tone: 'danger' }
      : { text: `due ${relativeTime(card.deadline, now)}`, tone: 'warn' });
  }
  if (card.noteCount) chips.push({ text: `${card.noteCount} note${card.noteCount > 1 ? 's' : ''}`, tone: 'muted' });
  return chips;
}

export function summaryLine(dashboard) {
  const now = dashboard.now.length;
  const reminders = dashboard.now.filter((c) => c.needsAttention).length;
  const waiting = dashboard.waiting.length;
  if (!now && !waiting) return 'All clear. Nothing needs you right now.';
  const parts = [now ? `${now} thing${now > 1 ? 's' : ''} to do now` : 'Nothing to do right now'];
  if (reminders) parts.push(`${reminders} reminder${reminders > 1 ? 's' : ''}`);
  if (waiting) parts.push(`${waiting} waiting`);
  return `${parts.join(' · ')}.`;
}

/** Share of the current timer phase that has elapsed (0 when idle). */
export function pomodoroFraction(p, now = new Date()) {
  if (!p || p.phase === 'idle') return 0;
  const minutes = { focus: p.focusMinutes, shortBreak: p.shortBreakMinutes, longBreak: p.longBreakMinutes }[p.phase] ?? p.focusMinutes;
  const total = minutes * 60;
  const remaining = p.running && p.endsAt ? Math.max(0, (new Date(p.endsAt) - now) / 1000) : p.remainingSeconds;
  return total > 0 ? Math.min(1, Math.max(0, 1 - remaining / total)) : 0;
}

export function fileSize(bytes) {
  if (!bytes) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${Number(value.toFixed(value < 10 ? 1 : 0))} ${units[unit]}`;
}

/**
 * Tags as typed in a task's tag box: separated by commas (deep work, q3); spaces and # around each are dropped, so
 * are quotes and [ ]. Hashtags typed one after another (#a #b) are separate tags too.
 */
export function parseTags(text) {
  const seen = new Set();
  const tags = [];
  for (const part of straightQuotes(text).replace(/^\s*\[|\]\s*$/g, '').split(',')) {
    for (const piece of part.split(/\s+(?=#)/)) {
      const tag = piece.replace(/"/g, '').trim().replace(/^#+/, '').replace(/\s+/g, ' ').trim();
      if (!tag || seen.has(tag.toLowerCase())) continue;
      seen.add(tag.toLowerCase());
      tags.push(tag);
    }
  }

  return tags;
}

/** Tags for the tag box: separated by commas. */
export const formatTags = (tags) => (tags ?? []).join(', ');
const OBSIDIAN = {
  download: 'https://obsidian.md/download',
  appStore: 'https://apps.apple.com/app/obsidian-connected-notes/id1557175442',
  playStore: 'https://play.google.com/store/apps/details?id=md.obsidian',
};

/**
 * Where an Obsidian link goes: the file in Obsidian when it's on this computer; else where to get it (its download
 * page: it isn't in the Microsoft Store; on a phone, its app in the App Store or Google Play).
 */
export function obsidianLink({ url, installed, userAgent = '', touchPoints = 0 }) {
  // An iPad's Safari says it's a Mac; a touch screen gives it away.
  if (/iPhone|iPad|iPod/i.test(userAgent) || (/Macintosh/i.test(userAgent) && touchPoints > 1)) return { href: OBSIDIAN.appStore, label: 'Get Obsidian', get: true };
  if (/Android/i.test(userAgent)) return { href: OBSIDIAN.playStore, label: 'Get Obsidian', get: true };
  return installed ? { href: url, label: 'Open in Obsidian', get: false } : { href: OBSIDIAN.download, label: 'Get Obsidian', get: true };
}

/** Search query for a clicked tag or label chip (same syntax as the server's TaskQuery). */
export function queryFor({ tag, label }) {
  if (tag) return /\s/.test(tag) ? `#"${tag}"` : `#${tag}`;
  return /\s/.test(label) ? `label:"${label}"` : `label:${label}`;
}

/** Smart punctuation (iPhone, iPad, Mac) curls quotes as they're typed: “deep work” means "deep work". */
const straightQuotes = (text) => String(text ?? '').replace(/[\u201C\u201D]/g, '"');

const terms = (query) => straightQuotes(query).match(/[^\s"]*"[^"]*"?|\S+/g) ?? [];

/** True when the filter already has this term (#tag, label:x; case-insensitive). */
export const hasTerm = (query, term) => terms(query).some((t) => t.toLowerCase() === term.toLowerCase());

/** Adds the term to the filter, or takes it out when it's there (the tag & label picker). */
export function toggleTerm(query, term) {
  const list = terms(query);
  const kept = list.filter((t) => t.toLowerCase() !== term.toLowerCase());
  return (kept.length === list.length ? [...list, term] : kept).join(' ');
}
