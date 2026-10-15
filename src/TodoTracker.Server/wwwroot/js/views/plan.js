// Plan: a day or a week as a time grid. Drag a task from "Not planned" onto a time to plan a block of work on it; drag a
// block to move it, its bottom edge to make it longer or shorter, click it to open the task. Deadlines show on their day,
// "now" is a line, done blocks are ticked. Each group keeps its own view (day or week, work hours or the whole day, and
// what work hours and days are). The keyboard way in: a task's details have a Plan field.

import { iconBadge, repeatMark, withAccent } from '../looks.js';
import { latest } from './common.js';

const SNAP = 15;
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

const hhmm = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const minutesOf = (date) => date.getHours() * 60 + date.getMinutes();

export function createPlanView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-plan');
  let anchor = new Date();
  let plan = null;
  const fetchPlan = latest((url) => ctx.api(url));
  const groupQuery = () => (ctx.state.group ? `group=${ctx.state.group}` : '');

  async function load({ quiet = false } = {}) {
    try {
      const settings = plan?.settings ?? await ctx.api(`/api/plan/settings?${groupQuery()}`);
      const start = firstDay(anchor, settings.view, settings.weekStart);
      const days = settings.view === 'week' ? 7 : 1;
      const reply = await fetchPlan(`/api/plan?from=${isoDay(start)}&days=${days}&${groupQuery()}`);
      if (!reply.current) return;
      plan = reply.value;
      render();
    } catch (err) {
      if (!quiet) ctx.toast(err.message, 'error');
    }
  }

  async function saveSettings(change) {
    try {
      const next = await ctx.api(`/api/plan/settings?${groupQuery()}`, { method: 'PUT', body: { ...plan.settings, ...change } });
      plan = { ...plan, settings: next };
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

  function range() {
    const s = plan.settings;
    if (s.hours === 'full') return [0, 24 * 60];
    // Work hours, stretched to show any block outside them.
    let from = s.workStart;
    let to = s.workEnd;
    for (const b of plan.blocks) {
      const start = new Date(b.start);
      const m = minutesOf(start);
      from = Math.min(from, Math.floor(m / 60) * 60);
      to = Math.max(to, Math.min(24 * 60, Math.ceil((m + b.minutes) / 60) * 60));
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

  function unplannedList() {
    const items = plan.unplanned.map((t) => {
      const li = h('li', { class: 'plan-task', draggable: 'true', tabindex: 0, dataset: { id: t.id }, title: 'Drag onto a time to plan it (or set it in its details)',
        onclick: () => ctx.openDrawer(t.id),
        onkeydown: (e) => { if (e.key === 'Enter') ctx.openDrawer(t.id); } },
      iconBadge(t.icon), h('span', { class: 'plan-task-title' }, t.title), t.breadcrumb?.length ? h('span', { class: 'muted small' }, t.breadcrumb.join(' › ')) : null);
      li.addEventListener('dragstart', (e) => { e.dataTransfer.setData('text/tt-task', JSON.stringify({ id: t.id, minutes: t.minutes })); e.dataTransfer.effectAllowed = 'move'; li.classList.add('dragging'); });
      li.addEventListener('dragend', () => li.classList.remove('dragging'));
      return withAccent(li, t.accent);
    });
    return h('aside', { class: 'plan-unplanned', 'aria-label': 'Not planned' },
      h('h3', null, 'Not planned'),
      items.length ? h('ul', null, ...items) : h('p', { class: 'muted small' }, 'Everything to do now is planned.'));
  }

  function block(b, from) {
    const start = new Date(b.start);
    const top = (minutesOf(start) - from) * PX_PER_MIN;
    const end = new Date(start.getTime() + b.minutes * 60000);
    const el = h('div', {
      class: `plan-block${b.done ? ' done' : ''}`, draggable: 'true', role: 'button', tabindex: 0, dataset: { id: b.id },
      'aria-label': `${b.title}, ${start.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} to ${end.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}${b.done ? ', done' : ''}`,
      onclick: (e) => { if (!e.target.closest('.plan-resize')) ctx.openDrawer(b.id); },
      onkeydown: (e) => { if (e.key === 'Enter') ctx.openDrawer(b.id); },
    },
    h('span', { class: 'plan-block-time' }, `${hhmm(minutesOf(start))}–${hhmm(minutesOf(end))}`),
    h('span', { class: 'plan-block-title' }, b.done ? icon('check', { size: 12 }) : null, iconBadge(b.icon), b.title, repeatMark(b.repeat)),
    h('span', { class: 'plan-resize', title: 'Drag to make it longer or shorter', 'aria-hidden': 'true' }));
    el.style.setProperty('--top', `${top}px`);
    el.style.setProperty('--height', `${Math.max(b.minutes * PX_PER_MIN, 18)}px`);
    withAccent(el, b.accent);
    el.addEventListener('dragstart', (e) => {
      // Where in the block it was picked up, so it lands where it's dropped.
      const grab = (e.offsetY ?? 0) / PX_PER_MIN;
      e.dataTransfer.setData('text/tt-task', JSON.stringify({ id: b.id, minutes: b.minutes, grab }));
      el.classList.add('dragging');
    });
    el.addEventListener('dragend', () => el.classList.remove('dragging'));
    resizable(el, b);
    return el;
  }

  /** The bottom edge: drag to change how long (snapped to 15 minutes). */
  function resizable(el, b) {
    const handle = el.querySelector('.plan-resize');
    handle.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      el.draggable = false;
      const startY = e.clientY;
      let minutes = b.minutes;
      handle.setPointerCapture(e.pointerId);
      const moveTo = (ev) => {
        minutes = Math.max(SNAP, Math.round((b.minutes + (ev.clientY - startY) / PX_PER_MIN) / SNAP) * SNAP);
        el.style.setProperty('--height', `${minutes * PX_PER_MIN}px`);
      };
      const done = () => {
        handle.removeEventListener('pointermove', moveTo);
        el.draggable = true;
        if (minutes !== b.minutes) planTask(b.id, new Date(b.start), minutes);
      };
      handle.addEventListener('pointermove', moveTo);
      handle.addEventListener('pointerup', done, { once: true });
      handle.addEventListener('pointercancel', done, { once: true });
    });
  }

  function dayColumn(day, from, to) {
    const key = isoDay(day);
    const blocks = plan.blocks.filter((b) => isoDay(new Date(b.start)) === key);
    const deadlines = plan.deadlines.filter((d) => isoDay(new Date(d.deadline)) === key);
    const col = h('div', { class: 'plan-day', dataset: { day: key }, 'aria-label': day.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' }) },
      ...blocks.map((b) => block(b, from)));
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
      ...deadlines.map((d) => h('button', { class: `chip ${d.done ? 'muted' : 'warn'} plan-deadline`, type: 'button', title: `Due ${new Date(d.deadline).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`, onclick: () => ctx.openDrawer(d.id) },
        icon('flag', { size: 11 }), d.title)));
    return { head, col };
  }

  function render() {
    const [from, to] = range();
    const days = shownDays();
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
    // Start at work hours (or the current time) rather than midnight.
    const body = grid.querySelector('.plan-body');
    if (plan.settings.hours === 'full' && body) body.scrollTop = Math.max(0, (plan.settings.workStart - 30) * PX_PER_MIN);
  }

  return {
    root,
    show: () => { plan = null; load(); },
    refresh: () => load({ quiet: true }),
    poll: () => load({ quiet: true }),
    title: 'Plan',
  };
}
