// Plan: a day or a week as a time grid. Drag a task from "Not planned" onto a time to plan a block of work on it; drag a
// block to move it, its bottom edge to make it longer or shorter, click it to open the task. Deadlines show on their day,
// "now" is a line, done blocks are ticked. Tasks put off (snoozed, waiting, a repeating task until its next time) are
// listed under "Later" with when they come back, and marked on that day. With the Energy extra, each day says what its
// blocks take (hours × each task's ⚡ an hour) against what there is in a day, and turns red past it. Each group keeps its own view (day or week, work hours or the whole day, and
// what work hours and days are). The keyboard way in: a task's details have a Plan field.

import { iconBadge, repeatMark, withAccent } from '../looks.js';
import { energyMarks } from '../format.js';
import { latest } from './common.js';

const SNAP = 15;
const MAX_MINUTES = 12 * 60;
const PX_PER_MIN = 0.9;
const DAY_NAMES = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

/** yyyy-mm-dd of a local date. */
export function isoDay(d) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** The first day shown: the day itself (day view), or the start of its week (Monday or Sunday). */
export function firstDay(date, view, weekStart = 1) {
  const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  if (view !== 'week') return d;
  const iso = ((d.getDay() + 6) % 7) + 1; // 1 = Monday … 7 = Sunday
  const back = weekStart === 7 ? iso % 7 : iso - 1;
  d.setDate(d.getDate() - back);
  return d;
}

/** Minutes after local midnight, snapped to the grid. */
export function snapMinutes(minutes, step = SNAP) {
  return Math.max(0, Math.min(24 * 60 - step, Math.round(minutes / step) * step));
}

/** When a task put off comes back: "Mon 09:00" (its time today: "09:00"). */
export function backLabel(at, now = new Date()) {
  const d = new Date(at);
  const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return isoDay(d) === isoDay(now) ? time : `${d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })} ${time}`;
}

const hhmm = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const minutesOf = (date) => date.getHours() * 60 + date.getMinutes();

export function createPlanView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-plan');
  let anchor = new Date();
  let plan = null;
  // Busy: a block being resized (a redraw would drop it). Scroll to work hours on the next draw (not on every poll).
  let resizing = false;
  let scrollToWork = true;
  const fetchPlan = latest((url) => ctx.api(url));
  const groupQuery = () => (ctx.state.group ? `group=${ctx.state.group}` : '');

  async function load({ quiet = false } = {}) {
    try {
      // Each group has its own settings: another group's (after switching) can't pick the days.
      const group = ctx.state.group ?? null;
      if (plan && plan.group !== group) {
        plan = null;
        scrollToWork = true;
      }
      const settings = plan?.settings ?? await ctx.api(`/api/plan/settings?${groupQuery()}`);
      const start = firstDay(anchor, settings.view, settings.weekStart);
      const days = settings.view === 'week' ? 7 : 1;
      const reply = await fetchPlan(`/api/plan?from=${isoDay(start)}&days=${days}&${groupQuery()}`);
      if (!reply.current || resizing) return;
      plan = { ...reply.value, group };
      render();
    } catch (err) {
      if (!quiet) ctx.toast(err.message, 'error');
    }
  }

  async function saveSettings(change) {
    try {
      const next = await ctx.api(`/api/plan/settings?${groupQuery()}`, { method: 'PUT', body: { ...plan.settings, ...change } });
      plan = { ...plan, settings: next };
      scrollToWork = true;
      await load();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  async function planTask(id, start, minutes) {
    try {
      await ctx.api(`/api/items/${id}/plan`, { method: 'POST', body: start ? { start: start.toISOString(), minutes } : {} });
      await load({ quiet: true });
      ctx.refreshDashboard?.();
    } catch (err) {
      ctx.toast(err.message, 'error');
      load({ quiet: true });
    }
  }

  // ---- drawing ---------------------------------------------------------------------------------------------------

  function shownDays() {
    const start = new Date(`${plan.from}T00:00:00`);
    const all = Array.from({ length: plan.days }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i));
    if (plan.days === 1 || plan.settings.hours === 'full') return all;
    const work = new Set(plan.settings.workDays ?? [1, 2, 3, 4, 5]);
    const kept = all.filter((d) => work.has(((d.getDay() + 6) % 7) + 1));
    return kept.length ? kept : all;
  }

  /** A block per day it covers (one crossing midnight goes on until the end of its day and carries on the next). */
  function segments(b) {
    const start = new Date(b.start);
    const end = new Date(start.getTime() + b.minutes * 60000);
    const parts = [];
    let day = new Date(start.getFullYear(), start.getMonth(), start.getDate());
    while (day < end) {
      const next = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1);
      const from = Math.max(start, day);
      const to = Math.min(end, next);
      parts.push({ day: isoDay(day), from: Math.round((from - day) / 60000), to: Math.round((to - day) / 60000), cont: start < day, last: end <= next });
      day = next;
    }
    return parts;
  }

  function range(days) {
    const s = plan.settings;
    if (s.hours === 'full') return [0, 24 * 60];
    // Work hours, stretched to show any block outside them on the days shown.
    const shown = new Set(days.map(isoDay));
    let from = s.workStart;
    let to = s.workEnd;
    for (const seg of plan.blocks.flatMap(segments).filter((x) => shown.has(x.day))) {
      from = Math.min(from, Math.floor(seg.from / 60) * 60);
      to = Math.max(to, Math.min(24 * 60, Math.ceil(seg.to / 60) * 60));
    }
    return [from, to];
  }

  function toolbar() {
    const s = plan.settings;
    const step = s.view === 'week' ? 7 : 1;
    const move = (n) => { anchor = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate() + n * step); load(); };
    const seg = (label, options, value, onPick) => h('div', { class: 'seg', role: 'group', 'aria-label': label },
      ...options.map(([v, text]) => h('button', { type: 'button', 'aria-pressed': String(v === value), onclick: () => onPick(v) }, text)));
    const days = shownDays();
    const title = days.length === 1
      ? days[0].toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' })
      : `${days[0].toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} – ${days[days.length - 1].toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}`;
    return h('div', { class: 'plan-toolbar' },
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': s.view === 'week' ? 'Previous week' : 'Previous day', onclick: () => move(-1) }, icon('chevron', { size: 16, className: 'flip' })),
      h('button', { class: 'btn small ghost', type: 'button', onclick: () => { anchor = new Date(); load(); } }, 'Today'),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': s.view === 'week' ? 'Next week' : 'Next day', onclick: () => move(1) }, icon('chevron', { size: 16 })),
      h('h2', { class: 'plan-title' }, title),
      seg('Show', [['day', 'Day'], ['week', 'Week']], s.view, (view) => saveSettings({ view })),
      seg('Hours', [['work', 'Work hours'], ['full', 'Full day']], s.hours, (hours) => saveSettings({ hours })),
      settingsButton());
  }

  function settingsButton() {
    const s = plan.settings;
    const box = h('div', { class: 'plan-settings', hidden: true, role: 'group', 'aria-label': 'Work hours and days' });
    const start = h('input', { type: 'time', value: hhmm(s.workStart), step: 900, 'aria-label': 'Work starts' });
    const end = h('input', { type: 'time', value: hhmm(s.workEnd), step: 900, 'aria-label': 'Work ends' });
    const days = new Set(s.workDays ?? [1, 2, 3, 4, 5]);
    const dayBoxes = DAY_NAMES.map((name, i) => h('label', { class: 'check' },
      h('input', { type: 'checkbox', checked: days.has(i + 1), onchange: (e) => { if (e.target.checked) days.add(i + 1); else days.delete(i + 1); } }), name));
    const weekStart = h('select', { 'aria-label': 'Week starts on' },
      h('option', { value: 1, selected: s.weekStart !== 7 }, 'Monday'), h('option', { value: 7, selected: s.weekStart === 7 }, 'Sunday'));
    const toMinutes = (v) => { const [hh, mm] = v.split(':').map(Number); return hh * 60 + mm; };
    box.append(
      h('div', { class: 'row' }, h('label', { class: 'field' }, h('span', null, 'Work starts'), start), h('label', { class: 'field' }, h('span', null, 'ends'), end),
        h('label', { class: 'field' }, h('span', null, 'Week starts on'), weekStart)),
      h('div', { class: 'row plan-days' }, ...dayBoxes),
      h('div', { class: 'row buttons' }, h('button', { class: 'btn primary small', type: 'button', onclick: () => saveSettings({
        workStart: toMinutes(start.value), workEnd: toMinutes(end.value), workDays: [...days].sort(), weekStart: Number(weekStart.value),
      }) }, 'Save')));
    const toggle = h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Work hours and days', 'aria-expanded': 'false', title: 'Work hours and days (this group)', onclick: () => {
      box.hidden = !box.hidden;
      toggle.setAttribute('aria-expanded', String(!box.hidden));
    } }, icon('settings', { size: 16 }));
    return h('span', { class: 'plan-settings-wrap' }, toggle, box);
  }

  function taskItem(t) {
    const back = t.wakeAt ? `back ${backLabel(t.wakeAt)}` : t.waitingFor ? `after “${t.waitingFor}”` : null;
    const li = h('li', { class: `plan-task${back ? ' later' : ''}`, draggable: 'true', tabindex: 0, dataset: { id: t.id }, title: 'Drag onto a time to plan it (or set it in its details)',
      onclick: () => ctx.openDrawer(t.id),
      onkeydown: (e) => { if (e.key === 'Enter') ctx.openDrawer(t.id); } },
    iconBadge(t.icon), h('span', { class: 'plan-task-title' }, t.title, repeatMark(t.repeat), energyMark(t.energy)),
    t.breadcrumb?.length ? h('span', { class: 'muted small' }, t.breadcrumb.join(' › ')) : null,
    back ? h('span', { class: 'muted small plan-back' }, back) : null);
    li.addEventListener('dragstart', (e) => { e.dataTransfer.setData('text/tt-task', JSON.stringify({ id: t.id, minutes: t.minutes })); e.dataTransfer.effectAllowed = 'move'; li.classList.add('dragging'); });
    li.addEventListener('dragend', () => li.classList.remove('dragging'));
    return withAccent(li, t.accent);
  }

  function unplannedList() {
    const items = plan.unplanned.map(taskItem);
    const later = (plan.later ?? []).map(taskItem);
    return h('aside', { class: 'plan-unplanned', 'aria-label': 'Not planned' },
      h('h3', null, 'Not planned'),
      items.length ? h('ul', { 'aria-label': 'Not planned' }, ...items) : h('p', { class: 'muted small' }, 'Everything to do now is planned.'),
      later.length ? h('h3', { class: 'plan-later-head' }, 'Later') : null,
      later.length ? h('ul', { 'aria-label': 'Later' }, ...later) : null);
  }

  const energyOn = () => ctx.pluginOn?.('energy') !== false && !!plan.energy;
  const energyMark = (level) => (energyOn() && level ? h('span', { class: 'energy-mark', title: `${energyMarks(level)} an hour` }, energyMarks(level)) : null);

  /** What the day's blocks take against what there is in a day (Settings › Energy). */
  function energyMeter(key) {
    if (!energyOn()) return null;
    const used = plan.energy.days?.[key] ?? 0;
    const per = plan.energy.perDay;
    const over = used > per;
    const n = Math.round(used * 10) / 10;
    const meter = h('span', {
      class: `plan-energy${over ? ' over' : ''}`, role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': String(per), 'aria-valuenow': String(n),
      'aria-label': `Energy: ${n} of ${per}`, title: over ? `${n} of ${per} ⚡: more than you have in a day` : `${n} of ${per} ⚡ planned (each block: hours × its ⚡ an hour)`,
    }, `⚡ ${n}/${per}`);
    meter.style.setProperty('--fill', `${Math.min(100, (used / per) * 100)}%`);
    return meter;
  }

  function block(b, seg, from) {
    const start = new Date(b.start);
    const top = (seg.from - from) * PX_PER_MIN;
    const end = new Date(start.getTime() + b.minutes * 60000);
    // A done block stays where it was (done tasks can't be planned again).
    const movable = !b.done;
    const el = h('div', {
      class: `plan-block${b.done ? ' done' : ''}${seg.cont ? ' cont' : ''}`, draggable: String(movable), role: 'button', tabindex: 0, dataset: { id: b.id },
      'aria-label': `${b.title}, ${start.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} to ${end.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}${b.done ? ', done' : ''}`,
      onclick: (e) => { if (!e.target.closest('.plan-resize')) ctx.openDrawer(b.id); },
      onkeydown: (e) => { if (e.key === 'Enter') ctx.openDrawer(b.id); },
    },
    h('span', { class: 'plan-block-time' }, `${hhmm(minutesOf(start))}–${hhmm(minutesOf(end))}`),
    h('span', { class: 'plan-block-title' }, b.done ? icon('check', { size: 12 }) : null, iconBadge(b.icon), b.title, repeatMark(b.repeat), energyMark(b.energy)),
    movable && seg.last ? h('span', { class: 'plan-resize', title: 'Drag to make it longer or shorter', 'aria-hidden': 'true' }) : null);
    el.style.setProperty('--top', `${top}px`);
    el.style.setProperty('--height', `${Math.max((seg.to - seg.from) * PX_PER_MIN, 18)}px`);
    withAccent(el, b.accent);
    if (!movable) return el;
    el.addEventListener('dragstart', (e) => {
      // Where in the block it was picked up (from its start), so it lands where it's dropped.
      const segStart = new Date(`${seg.day}T00:00:00`).getTime() + seg.from * 60000;
      const grab = (segStart - start.getTime()) / 60000 + (e.offsetY ?? 0) / PX_PER_MIN;
      e.dataTransfer.setData('text/tt-task', JSON.stringify({ id: b.id, minutes: b.minutes, grab }));
      el.classList.add('dragging');
    });
    el.addEventListener('dragend', () => el.classList.remove('dragging'));
    if (seg.last) resizable(el, b, seg);
    return el;
  }

  /** The bottom edge: drag to change how long (snapped to 15 minutes). */
  function resizable(el, b, seg) {
    const handle = el.querySelector('.plan-resize');
    handle.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      el.draggable = false;
      resizing = true;
      const startY = e.clientY;
      const before = b.minutes - (seg.to - seg.from); // the part on earlier days
      let minutes = b.minutes;
      handle.setPointerCapture(e.pointerId);
      const moveTo = (ev) => {
        minutes = Math.min(MAX_MINUTES, Math.max(SNAP, Math.round((b.minutes + (ev.clientY - startY) / PX_PER_MIN) / SNAP) * SNAP));
        el.style.setProperty('--height', `${Math.max(minutes - before, SNAP) * PX_PER_MIN}px`);
      };
      const done = () => {
        handle.removeEventListener('pointermove', moveTo);
        el.draggable = true;
        resizing = false;
        if (minutes !== b.minutes) planTask(b.id, new Date(b.start), minutes);
      };
      handle.addEventListener('pointermove', moveTo);
      handle.addEventListener('pointerup', done, { once: true });
      handle.addEventListener('pointercancel', done, { once: true });
    });
  }

  function dayColumn(day, from, to) {
    const key = isoDay(day);
    const parts = plan.blocks.flatMap((b) => segments(b).filter((s) => s.day === key).map((s) => block(b, s, from)));
    const deadlines = plan.deadlines.filter((d) => isoDay(new Date(d.deadline)) === key);
    const comingBack = (plan.later ?? []).filter((t) => t.wakeAt && isoDay(new Date(t.wakeAt)) === key);
    const col = h('div', { class: 'plan-day', dataset: { day: key }, 'aria-label': day.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' }) },
      ...parts);
    col.style.setProperty('--height', `${(to - from) * PX_PER_MIN}px`);
    if (key === isoDay(new Date())) {
      col.classList.add('today');
      const now = minutesOf(new Date());
      if (now >= from && now <= to) {
        const line = h('div', { class: 'plan-now', 'aria-hidden': 'true' });
        line.style.setProperty('--top', `${(now - from) * PX_PER_MIN}px`);
        col.append(line);
      }
    }

    col.addEventListener('dragover', (e) => { if (e.dataTransfer.types.includes('text/tt-task')) { e.preventDefault(); col.classList.add('over'); } });
    col.addEventListener('dragleave', () => col.classList.remove('over'));
    col.addEventListener('drop', (e) => {
      e.preventDefault();
      col.classList.remove('over');
      const data = JSON.parse(e.dataTransfer.getData('text/tt-task') || 'null');
      if (!data) return;
      const y = e.clientY - col.getBoundingClientRect().top;
      const minutes = snapMinutes(from + y / PX_PER_MIN - (data.grab ?? 0));
      const at = new Date(day.getFullYear(), day.getMonth(), day.getDate(), Math.floor(minutes / 60), minutes % 60);
      planTask(data.id, at, data.minutes);
    });

    const head = h('div', { class: `plan-day-head${key === isoDay(new Date()) ? ' today' : ''}` },
      h('span', { class: 'plan-day-name' }, day.toLocaleDateString(undefined, { weekday: 'short' })),
      h('span', { class: 'plan-day-date' }, String(day.getDate())),
      energyMeter(key),
      ...deadlines.map((d) => h('button', { class: `chip ${d.done ? 'muted' : 'warn'} plan-deadline`, type: 'button', title: `Due ${new Date(d.deadline).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`, onclick: () => ctx.openDrawer(d.id) },
        icon('flag', { size: 11 }), d.title)),
      ...comingBack.map((t) => h('button', { class: 'chip plan-back-chip', type: 'button', title: `Back ${backLabel(t.wakeAt)}`, onclick: () => ctx.openDrawer(t.id) },
        icon('clock', { size: 11 }), t.title, repeatMark(t.repeat))));
    return { head, col };
  }

  function render() {
    const days = shownDays();
    const [from, to] = range(days);
    const scrolled = root.querySelector('.plan-body')?.scrollTop;
    const columns = days.map((d) => dayColumn(d, from, to));
    const hours = [];
    for (let m = Math.ceil(from / 60) * 60; m < to; m += 60) {
      const label = h('span', { class: `plan-hour${m === from ? ' first' : ''}` }, hhmm(m));
      label.style.setProperty('--top', `${(m - from) * PX_PER_MIN}px`);
      hours.push(label);
    }

    const gutter = h('div', { class: 'plan-gutter', 'aria-hidden': 'true' }, ...hours);
    gutter.style.setProperty('--height', `${(to - from) * PX_PER_MIN}px`);
    const grid = h('div', { class: 'plan-grid' },
      h('div', { class: 'plan-heads' }, h('span', { class: 'plan-gutter-head' }), ...columns.map((c) => c.head)),
      h('div', { class: 'plan-body' }, gutter, ...columns.map((c) => c.col)));
    grid.style.setProperty('--days', String(days.length));
    grid.style.setProperty('--hour', `${60 * PX_PER_MIN}px`);
    root.replaceChildren(h('div', { class: 'plan' }, toolbar(), h('div', { class: 'plan-main' }, unplannedList(), grid)));
    // The whole day starts at work hours rather than midnight; a redraw (a poll) keeps where it was scrolled to.
    const body = grid.querySelector('.plan-body');
    if (scrollToWork) {
      scrollToWork = false;
      if (plan.settings.hours === 'full') body.scrollTop = Math.max(0, (plan.settings.workStart - 30) * PX_PER_MIN);
    } else if (scrolled != null) {
      body.scrollTop = scrolled;
    }
  }

  /** Something that a redraw would undo: a resize, or the settings being edited. */
  const busy = () => resizing || !!root.querySelector('.plan-settings:not([hidden])');

  return {
    root,
    show: () => { plan = null; scrollToWork = true; load(); },
    refresh: () => { if (!resizing) load({ quiet: true }); },
    poll: () => { if (!busy()) load({ quiet: true }); },
    title: 'Plan',
  };
}
