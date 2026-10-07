// Reports: where the time went and what got done, for a range (7, 28, 90 days or a year) and the current group:
// headline numbers, time per day (focus vs. other), per group (donut), top tasks, a weekday × hour heat map, a
// calendar of active days, and a timeline (Gantt) of the work on each task.

import { barChart, donutChart, hourHeatmap, ganttChart, calendarHeatmap } from '../charts.js';
import { duration, hours, dayOffset } from '../timefmt.js';

const RANGES = [[7, '7 days'], [28, '4 weeks'], [90, '3 months'], [365, 'Year']];
const FALLBACK_COLORS = ['#6366f1', '#22c55e', '#f59e0b', '#ec4899', '#0ea5e9', '#8b5cf6', '#14b8a6'];

export function createReportsView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-reports');
  let days = Number(localStorage.getItem('tt.reports.days')) || 28;
  let report = null;

  async function load() {
    const today = dayOffset(new Date(), 0);
    const from = dayOffset(new Date(), -(days - 1));
    try {
      report = await ctx.api(`/api/reports?from=${from}&to=${today}${ctx.state.group ? `&group=${ctx.state.group}` : ''}`);
      render();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  const colorOfGroup = (id, i = 0) => ctx.groups().find((g) => g.id === id)?.color ?? FALLBACK_COLORS[i % FALLBACK_COLORS.length];

  const kpi = (label, value, sub, iconName) => h('div', { class: 'kpi' },
    h('span', { class: 'kpi-icon' }, icon(iconName, { size: 18 })),
    h('span', { class: 'kpi-value' }, value),
    h('span', { class: 'kpi-label' }, label),
    sub ? h('span', { class: 'kpi-sub muted small' }, sub) : null);

  const card = (title, subtitle, ...body) => h('section', { class: 'report-card' }, h('header', null, h('h3', null, title), subtitle ? h('span', { class: 'muted small' }, subtitle) : null), ...body);

  function render() {
    const r = report;
    const focus = r.days.reduce((s, d) => s + d.focusSeconds, 0);
    const avg = r.activeDays ? r.trackedSeconds / r.activeDays : 0;
    const label = (d) => new Date(`${d.date}T12:00:00`).toLocaleDateString(undefined, days > 31 ? { month: 'short', day: 'numeric' } : { weekday: 'short', day: 'numeric' });

    // Long ranges are summed per week so the bars stay readable.
    const buckets = [];
    const size = days > 120 ? 7 : 1;
    for (let i = 0; i < r.days.length; i += size) {
      const slice = r.days.slice(i, i + size);
      buckets.push({
        label: label(slice[0]),
        parts: [slice.reduce((s, d) => s + d.focusSeconds, 0) / 3600, slice.reduce((s, d) => s + d.trackedSeconds - d.focusSeconds, 0) / 3600],
        tip: `${size > 1 ? 'Week of ' : ''}${label(slice[0])}: ${duration(slice.reduce((s, d) => s + d.trackedSeconds, 0))} (${duration(slice.reduce((s, d) => s + d.focusSeconds, 0))} focus) · ${slice.reduce((s, d) => s + d.completed, 0)} done`,
      });
    }

    const groups = r.groups.map((g, i) => ({ label: g.name, value: g.trackedSeconds, color: g.color ?? FALLBACK_COLORS[i % FALLBACK_COLORS.length], tip: duration(g.trackedSeconds) }));
    const top = r.tasks[0]?.trackedSeconds ?? 0;
    const empty = r.trackedSeconds === 0 && r.completed === 0;

    root.replaceChildren(
      h('div', { class: 'view-toolbar' },
        h('div', { class: 'seg', role: 'group', 'aria-label': 'Range' }, ...RANGES.map(([d, text]) => h('button', {
          type: 'button', 'aria-pressed': String(days === d),
          onclick: () => { days = d; localStorage.setItem('tt.reports.days', String(d)); load(); },
        }, text))),
        h('span', { class: 'muted small push-right' }, `${new Date(`${r.from}T12:00:00`).toLocaleDateString()} – ${new Date(`${r.to}T12:00:00`).toLocaleDateString()}`)),
      h('div', { class: 'kpis' },
        kpi('Time tracked', duration(r.trackedSeconds), avg ? `${duration(avg)} per active day` : null, 'timer'),
        kpi('Focus sessions', String(r.focusSessions), focus ? `${duration(focus)} of focus` : null, 'target'),
        kpi('Tasks done', String(r.completed), r.created ? `${r.created} new` : null, 'check'),
        kpi('Longest streak', `${r.longestStreak} day${r.longestStreak === 1 ? '' : 's'}`, `${r.activeDays} active day${r.activeDays === 1 ? '' : 's'}`, 'flag')),
      empty
        ? h('div', { class: 'empty-state' }, icon('chart', { size: 28 }), h('p', null, 'Nothing tracked here yet. Start a timer on a task (▶) or a focus session, and your reports fill in.'))
        : h('div', { class: 'report-grid' },
          card('Time per day', 'focus vs. other time · hours', barChart(buckets, { format: (v) => `${hours(v * 3600)}h`, partClasses: ['bar focus', 'bar manual'], label: 'Hours tracked per day' }),
            h('div', { class: 'legend' }, h('span', { class: 'key focus' }, 'Focus'), h('span', { class: 'key manual' }, 'Timer & logged'))),
          card('By group', null, h('div', { class: 'donut-wrap' },
            donutChart(groups, { center: duration(r.trackedSeconds), centerSub: 'tracked', label: 'Time per group' }),
            h('ul', { class: 'legend-list' }, ...groups.map((g) => {
              const sw = h('span', { class: 'swatch' });
              sw.style.background = g.color;
              return h('li', null, sw, h('span', null, g.label), h('span', { class: 'muted' }, `${duration(g.value)} · ${Math.round((g.value / Math.max(1, r.trackedSeconds)) * 100)}%`));
            })))),
          card('Where the time went', 'top tasks', h('ol', { class: 'hbars' }, ...r.tasks.map((t, i) => {
            const fill = h('span', { class: 'hbar-fill' });
            fill.style.width = `${Math.max(2, (t.trackedSeconds / Math.max(1, top)) * 100)}%`;
            fill.style.background = colorOfGroup(t.groupId, i);
            return h('li', null, h('button', { class: 'link hbar-title', type: 'button', onclick: () => ctx.openDrawer(t.id) }, t.title, t.done ? ' ✓' : ''), h('span', { class: 'hbar-track' }, fill), h('span', { class: 'muted small' }, duration(t.trackedSeconds)));
          }))),
          card('When you work', 'weekday × hour', hourHeatmap(r.hourGrid, { format: duration })),
          days < 90 ? null : card('Active days', 'time tracked or tasks done', calendarHeatmap(r.days.map((d) => ({ date: d.date, value: d.trackedSeconds + d.completed * 1800, tip: `${label(d)}: ${duration(d.trackedSeconds)}, ${d.completed} done` })))),
          h('section', { class: 'report-card wide' }, h('header', null, h('h3', null, 'Timeline'), h('span', { class: 'muted small' }, 'bars: work · line: from created to done or due · ◆ due · ● done')),
            r.timeline.length
              ? ganttChart(r.timeline, `${r.from}T00:00:00`, `${dayOffset(new Date(`${r.to}T12:00:00`), 1)}T00:00:00`, { colorOf: (row) => colorOfGroup(row.groupId), format: duration, onOpen: (row) => ctx.openDrawer(row.id) })
              : h('p', { class: 'muted' }, 'No task was worked on or finished in this range.'))));
  }

  return { root, show: load, refresh: load, title: 'Reports' };
}
