// Done: what got finished, by day, with the time it took. Put finished tasks away (archive) or delete them; the
// Archive tab shows what was put away, to bring back or delete for good.

import { byDay, duration } from '../timefmt.js';
import { latest, serial, sameAs, send, treeUrl } from './common.js';

const AUTO_DAYS = [7, 14, 30];

export function createDoneView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-done');
  let tab = 'done';
  let done = [];
  let archived = [];
  let filter = '';
  const selected = new Set();
  const fetchBoth = latest(() => Promise.all([ctx.api(treeUrl(ctx, 'archived=false')), ctx.api(treeUrl(ctx, 'archived=true'))]));
  const queue = serial();
  let shown = null;

  /** `quiet`: a background refresh, which leaves the list alone when nothing changed. */
  async function load({ quiet = false } = {}) {
    try {
      const reply = await fetchBoth();
      if (!reply.current) return;
      const { same, text } = sameAs(shown, reply.value);
      shown = text;
      if (quiet && same) return;
      const [tree, archive] = reply.value;
      done = tree.filter((n) => n.done).sort((a, b) => new Date(b.completedAt) - new Date(a.completedAt));
      archived = archive.sort((a, b) => new Date(b.archivedAt ?? b.completedAt) - new Date(a.archivedAt ?? a.completedAt));
      for (const id of [...selected]) if (![...done, ...archived].some((n) => n.id === id)) selected.delete(id);
      render();
    } catch (err) {
      if (!quiet) ctx.toast(err.message, 'error');
    }
  }

  const run = (requests, message) => queue(() => send(ctx, requests, message));

  function remove(nodes) {
    const what = nodes.length === 1 ? `"${nodes[0].title}"` : `${nodes.length} tasks`;
    if (!confirm(`Delete ${what} and their subtasks? They go to the vault's trash (and stay in the history).`)) return null;
    nodes.forEach((n) => selected.delete(n.id));
    return run(nodes.map((n) => ({ method: 'DELETE', url: `/api/items/${n.id}` })), 'Deleted');
  }

  function row(node) {
    const check = h('input', {
      type: 'checkbox', checked: selected.has(node.id), 'aria-label': `Select ${node.title}`,
      onchange: (e) => { if (e.target.checked) selected.add(node.id); else selected.delete(node.id); renderBulk(); },
    });
    const group = ctx.groups().find((g) => g.id === node.groupId);
    const dot = h('span', { class: 'group-dot', title: group?.name ?? '' });
    if (group?.color) dot.style.setProperty('--group', group.color);
    return h('li', { class: 'done-row' },
      check,
      h('span', { class: 'done-check', 'aria-hidden': 'true' }, icon('check', { size: 13 })),
      h('button', { class: 'done-title link', type: 'button', onclick: () => ctx.openDrawer(node.id) }, node.title),
      dot,
      node.totalCount ? h('span', { class: 'muted small' }, `${node.doneCount}/${node.totalCount} steps`) : null,
      node.timeSpentSeconds ? h('span', { class: 'chip muted', title: 'Time spent' }, icon('timer', { size: 12 }), duration(node.timeSpentSeconds)) : null,
      h('span', { class: 'muted small when', title: new Date(node.completedAt).toLocaleString() }, new Date(node.completedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })),
      h('span', { class: 'row-actions' },
        tab === 'done'
          ? h('button', { class: 'btn small ghost', type: 'button', onclick: () => run([{ method: 'POST', url: `/api/items/${node.id}/archive` }], 'Archived') }, icon('archive', { size: 14 }), 'Archive')
          : h('button', { class: 'btn small ghost', type: 'button', onclick: () => run([{ method: 'POST', url: `/api/items/${node.id}/unarchive` }], 'Back in Done') }, icon('undo', { size: 14 }), 'Unarchive'),
        tab === 'done' ? h('button', { class: 'btn small ghost', type: 'button', onclick: () => run([{ method: 'POST', url: `/api/items/${node.id}/reopen` }], 'Reopened') }, icon('reset', { size: 14 }), 'Reopen') : null,
        h('button', { class: 'icon-btn small danger', type: 'button', title: 'Delete', 'aria-label': `Delete ${node.title}`, onclick: () => remove([node]) }, icon('trash', { size: 14 }))));
  }

  const bulk = h('div', { class: 'bulk-bar', role: 'region', 'aria-label': 'Selected tasks' });

  function renderBulk() {
    const nodes = (tab === 'done' ? done : archived).filter((n) => selected.has(n.id));
    bulk.hidden = nodes.length === 0;
    bulk.replaceChildren(
      h('span', null, `${nodes.length} selected`),
      tab === 'done'
        ? h('button', { class: 'btn small', type: 'button', onclick: () => { nodes.forEach((n) => selected.delete(n.id)); run(nodes.map((n) => ({ method: 'POST', url: `/api/items/${n.id}/archive` })), `Archived ${nodes.length}`); } }, icon('archive', { size: 14 }), 'Archive')
        : h('button', { class: 'btn small', type: 'button', onclick: () => { nodes.forEach((n) => selected.delete(n.id)); run(nodes.map((n) => ({ method: 'POST', url: `/api/items/${n.id}/unarchive` })), `Unarchived ${nodes.length}`); } }, icon('undo', { size: 14 }), 'Unarchive'),
      h('button', { class: 'btn small danger', type: 'button', onclick: () => remove(nodes) }, icon('trash', { size: 14 }), 'Delete'),
      h('button', { class: 'link small', type: 'button', onclick: () => { selected.clear(); render(); } }, 'Clear'));
  }

  // Typing in the search box only redraws the list under it (the box keeps its caret and any IME composition).
  const results = h('div', { class: 'done-results' });
  const count = h('span', { class: 'muted small push-right', role: 'status' });

  function renderList() {
    const list = (tab === 'done' ? done : archived).filter((n) => !filter || n.title.toLowerCase().includes(filter.toLowerCase()));
    const days = byDay(list, (n) => n.completedAt);
    const total = list.reduce((s, n) => s + (n.timeSpentSeconds ?? 0), 0);
    count.textContent = list.length ? `${list.length} task${list.length > 1 ? 's' : ''}${total ? ` · ${duration(total)} spent` : ''}` : '';
    results.replaceChildren(list.length
      ? h('div', { class: 'done-days' }, ...days.map((d) => h('section', { class: 'done-day' },
        h('h3', null, d.label, h('span', { class: 'muted small' }, ` · ${d.items.length}`)),
        h('ul', { class: 'done-list' }, ...d.items.map(row)))))
      : h('div', { class: 'empty-state' }, icon(tab === 'done' ? 'check' : 'archive', { size: 28 }),
        h('p', null, filter ? 'Nothing matches.' : tab === 'done' ? 'Nothing finished yet. It will show up here, day by day.' : 'Nothing archived. Archive finished tasks to keep Done short.')));
    renderBulk();
  }

  function render() {
    const tabs = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Done or archived' },
      ...[['done', `Done (${done.length})`], ['archive', `Archive (${archived.length})`]].map(([id, label]) => h('button', {
        type: 'button', role: 'tab', 'aria-selected': String(tab === id),
        onclick: () => { tab = id; selected.clear(); render(); },
      }, label)));
    const autoArchive = tab === 'done' && done.length ? h('label', { class: 'inline-select' }, 'Archive everything done ',
      h('select', { 'aria-label': 'Archive tasks done more than', onchange: (e) => {
        const days = Number(e.target.value);
        e.target.value = '';
        if (days) run([{ method: 'POST', url: '/api/archive', body: { olderThanDays: days, groupId: ctx.state.group } }], 'Archived');
      } }, h('option', { value: '' }, 'more than…'), ...AUTO_DAYS.map((d) => h('option', { value: d }, `${d} days ago`)))) : null;
    root.replaceChildren(
      h('div', { class: 'view-toolbar' },
        tabs,
        h('input', { type: 'search', class: 'small-search', placeholder: 'Find…', value: filter, 'aria-label': 'Find a finished task', oninput: (e) => { filter = e.target.value; renderList(); } }),
        autoArchive,
        count),
      bulk,
      results);
    renderList();
  }

  return { root, show: () => load(), refresh: () => load(), poll: () => load({ quiet: true }), title: 'Done' };
}
