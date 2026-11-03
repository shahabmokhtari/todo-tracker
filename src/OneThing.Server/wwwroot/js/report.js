import { api, h, ApiError } from './api.js';
import { relativeTime, priorityMeta, groupByDay, isSafeHttpUrl, progressPercent } from './format.js';
import { icon, ring } from './icons.js';

const id = new URLSearchParams(location.search).get('id');
const summary = document.getElementById('summary');
const daysEl = document.getElementById('days');
document.getElementById('print').addEventListener('click', () => window.print());

const KIND_LABELS = {
  created: 'Created', updated: 'Updated', noteAdded: 'Note', completed: 'Done', reopened: 'Reopened',
  scheduled: 'Scheduled', reminderAdded: 'Reminder set', reminderFired: 'Reminder', reminderDismissed: 'Dismissed',
  deleted: 'Deleted', moved: 'Moved', focusStarted: 'Focus', focusCompleted: 'Focus done', groupChanged: 'Group',
};

function tree(item) {
  return h('li', { class: item.completedAt ? 'done' : '' },
    h('span', { class: 'author' }, icon(item.completedAt ? 'check' : item.state === 'locked' ? 'lock' : 'target', { size: 14 }), item.title),
    item.state === 'waiting' && item.nextActionAt ? h('span', { class: 'chip info' }, `back ${relativeTime(item.nextActionAt)}`) : null,
    item.children.length ? h('ul', { class: 'tree' }, ...item.children.map(tree)) : null);
}

function countLeaves(item) {
  if (!item.children.length) return { done: item.completedAt ? 1 : 0, total: 1 };
  return item.children.map(countLeaves).reduce((a, b) => ({ done: a.done + b.done, total: a.total + b.total }), { done: 0, total: 0 });
}

async function load() {
  try {
    const [item, timeline] = id
      ? await Promise.all([api(`/api/items/${id}`), api(`/api/items/${id}/timeline`)])
      : [null, await api('/api/timeline?limit=1000')];

    if (item) {
      const leaves = countLeaves(item);
      const pct = progressPercent(leaves.done, leaves.total);
      const meta = priorityMeta(item.priority);
      document.title = `${item.title} · Report`;
      const chips = [
        h('span', { class: 'prio-pill' }, meta.label),
        h('span', { class: 'chip step' }, `${leaves.done}/${leaves.total} done`),
        h('span', { class: 'chip' }, item.state),
        item.deadline ? h('span', { class: 'chip warn' }, `deadline ${new Date(item.deadline).toLocaleString()}`) : null,
      ];
      summary.replaceChildren(...[
        h('div', { class: 'crumbs' }, item.path.slice(0, -1).join(' › ')),
        h('div', { class: 'empty-row' },
          h('span', { class: 'step-progress' }, ring(pct / 100, { size: 52, stroke: 5 })),
          h('div', null, h('h1', null, item.title), h('div', { class: 'report-stats' }, ...chips))),
        item.details ? h('p', { class: 'muted' }, item.details) : null,
        item.children.length ? h('ul', { class: 'tree' }, ...item.children.map(tree)) : null].filter(Boolean));
      summary.style.setProperty('--prio', meta.color);
    } else {
      summary.replaceChildren(h('h1', null, 'Everything, newest first'), h('p', { class: 'muted' }, 'Every task, note, reminder, and focus session across your board.'));
    }

    const byDay = groupByDay(timeline);
    daysEl.replaceChildren(...(byDay.length ? byDay : [{ day: 'No activity yet', entries: [] }]).map((d) =>
      h('section', { class: 'day' },
        h('h2', null, d.day),
        h('ul', { class: 'timeline' }, ...d.entries.map((e) => h('li', { class: `kind-${e.kind}` },
          h('div', { class: 'meta' },
            h('span', { class: 'kind' }, KIND_LABELS[e.kind] ?? e.kind),
            h('span', { class: 'when' }, new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })),
            h('span', { class: 'when' }, e.actor),
            !id && e.itemTitle ? h('span', { class: 'chip' }, e.itemTitle) : null),
          h('div', null, e.summary)))))));

    if (item) {
      const sources = [];
      const collect = (i) => { i.notes.forEach((n) => isSafeHttpUrl(n.sourceUrl) && sources.push(n)); i.children.forEach(collect); };
      collect(item);
      if (sources.length) {
        daysEl.append(h('section', { class: 'day' }, h('h2', null, 'Linked sources'),
          h('ul', { class: 'timeline' }, ...sources.map((n) => h('li', { class: 'kind-noteAdded' },
            h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'author' }, icon('link', { size: 14 }), n.sourceTitle || n.sourceUrl))))));
      }
    }
  } catch (err) {
    summary.replaceChildren(h('p', { class: 'error' }, err instanceof ApiError && err.status === 401
      ? 'Not connected. Open the dashboard from the sidebar first.'
      : err.message));
  }
}

load();
