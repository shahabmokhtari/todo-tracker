// Breaks: when the focus session ends, a full-screen (skippable) reminder to rest.

export const TIPS = [
  'Stand up and stretch for a minute.',
  'Look at something far away: rest your eyes.',
  'Drink a glass of water.',
  'Take five slow breaths.',
  'Walk around the room.',
  'Roll your shoulders and unclench your jaw.',
  'Step outside or open a window.',
];

/**
 * Whether the break screen shows, and until when. It shows during a running break, and also right when a running
 * focus session reaches its end (before the server has switched to the break), so it is never late.
 * `dismissed` is the end (ms) of a break already hidden or skipped; `key` is the end of this one.
 */
export function breakState(p, now = Date.now(), dismissed = null) {
  if (!p || !p.endsAt) return { show: false };
  const end = new Date(p.endsAt).getTime();
  const t = typeof now === 'number' ? now : new Date(now).getTime();
  if (p.running && (p.phase === 'shortBreak' || p.phase === 'longBreak') && end > t) {
    return { show: dismissed !== end, until: end, key: end, long: p.phase === 'longBreak' };
  }

  if (p.running && p.phase === 'focus' && end <= t) {
    // The session just ended: the break that starts now (long every few sessions).
    const long = (p.completedFocusCount + 1) % (p.focusesBeforeLongBreak || 4) === 0;
    const until = end + (long ? p.longBreakMinutes : p.shortBreakMinutes) * 60_000;
    return { show: until > t && dismissed !== until, until, key: until, long, predicted: true };
  }

  return { show: false };
}

/** A gentle suggestion that changes with each break (stable while it lasts). */
export const tipFor = (key) => TIPS[Math.abs([...String(key)].reduce((n, c) => (n * 31 + c.charCodeAt(0)) | 0, 7)) % TIPS.length];

/** How long "Break's over" keeps asking after a break ran out (later, the timer's own Start button does). */
export const OVER_FOR_MS = 15 * 60_000;

/**
 * Whether to ask "Break's over: start the next focus?": the break this window saw (`seen`, its end in ms) ran out by
 * time (a skip ends it early, before `seen`), not long ago, no new session has started, and it wasn't answered with
 * Not now (`dismissedOver`). Same rule in the Windows and Apple apps (tests/fixtures/breaks.json).
 */
export function breakOver(p, now = Date.now(), seen = null, dismissedOver = null) {
  if (!p || seen == null || dismissedOver === seen) return false;
  const t = typeof now === 'number' ? now : new Date(now).getTime();
  if (t < seen || t - seen >= OVER_FOR_MS) return false;
  // The old session's focus ended before the break did; a focus phase ending later is a new session.
  return !(p.phase === 'focus' && (!p.endsAt || new Date(p.endsAt).getTime() > seen));
}