import { api, h, ApiError } from './api.js';
import { relativeTime, priorityMeta, groupByDay, isSafeHttpUrl } from './format.js';

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
    `${item.completedAt ? '✓' : '○'} ${item.title}`,
    item.state === 'waiting' && item.nextActionAt ? h('span', { class: 'meta' }, ` · waiting ${relativeTime(item.nextActionAt)}`) : null,
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
      document.title = `${item.title} · Report`;
      const head = h('div', null,
        h('div', { class: 'crumbs' }, item.path.slice(0, -1).join(' › ')),
        h('h1', null, item.title),
        h('div', { class: 'meta' }, [
          priorityMeta(item.priority).label,
          item.state,
          `${leaves.done}/${leaves.total} steps done`,
          item.deadline ? `deadline ${new Date(item.deadline).toLocaleString()}` : null,
        ].filter(Boolean).join(' · ')),
        item.details ? h('p', null, item.details) : null,
        item.children.length ? h('ul', { class: 'tree' }, ...item.children.map(tree)) : null);
      head.style.setProperty('--prio', priorityMeta(item.priority).color);
      summary.replaceChildren(head);
    } else {
      summary.replaceChildren(h('h1', null, 'Everything, newest first'));
    }

    const byDay = groupByDay(timeline);
    daysEl.replaceChildren(...(byDay.length ? byDay : [{ day: 'No activity yet', entries: [] }]).map((d) =>
      h('section', { class: 'day' },
        h('h2', null, d.day),
        h('ul', { class: 'timeline' }, ...d.entries.map((e) => h('li', null,
          h('span', { class: 'when' }, new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })),
          h('div', null,
            h('div', { class: `kind ${e.kind}` }, `${KIND_LABELS[e.kind] ?? e.kind} · ${e.actor}${!id && e.itemTitle ? ` · ${e.itemTitle}` : ''}`),
            h('div', null, e.summary))))))));

    if (item) {
      const sources = [];
      const collect = (i) => { i.notes.forEach((n) => isSafeHttpUrl(n.sourceUrl) && sources.push(n)); i.children.forEach(collect); };
      collect(item);
      if (sources.length) {
        daysEl.append(h('section', { class: 'day' }, h('h2', null, 'Linked sources'),
          h('ul', { class: 'timeline' }, ...sources.map((n) => h('li', null, h('span', { class: 'when' }, '🔗'),
            h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer' }, n.sourceTitle || n.sourceUrl))))));
      }
    }
  } catch (err) {
    summary.replaceChildren(h('p', { class: 'error' }, err instanceof ApiError && err.status === 401
      ? 'Not connected. Open the dashboard from the sidebar first.'
      : err.message));
  }
}

load();
