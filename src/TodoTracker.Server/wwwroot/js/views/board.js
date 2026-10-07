// Board: the top-level tasks of a group as cards in a small, fixed flow (Inbox → Next → Doing → Done). Drag cards
// (or Alt+arrows) to move them; Doing is kept to three on purpose. Clicking a card opens it.

import { COLUMNS, WIP_LIMIT, columnsOf, wip, dropRequests, keyboardMove, cardFacts } from '../boardmodel.js';
import { duration } from '../timefmt.js';

export function createBoardView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-board');
  let nodes = [];
  let columns = { inbox: [], next: [], doing: [], done: [] };
  let dragging = null;

  async function load() {
    try {
      nodes = await ctx.api(`/api/tree${ctx.state.group ? `?group=${ctx.state.group}` : ''}`);
      render();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  async function run(requests, message) {
    if (!requests?.length) return;
    await ctx.act((async () => { for (const r of requests) await ctx.api(r.url, { method: r.method, body: r.body }); })(), message);
  }

  function fact(f) {
    switch (f.kind) {
      case 'overdue': return h('span', { class: 'chip danger' }, icon('flag', { size: 12 }), 'Overdue');
      case 'waiting': return h('span', { class: 'chip info' }, icon('clock', { size: 12 }), 'Waiting');
      case 'progress': {
        const bar = h('span', { class: 'mini-progress', role: 'img', 'aria-label': `${f.done} of ${f.total} subtasks done` }, h('span', { class: 'mini-progress-fill' }));
        bar.firstChild.style.width = `${Math.round((f.done / f.total) * 100)}%`;
        return h('span', { class: `chip ${f.tone}` }, bar, `${f.done}/${f.total}`);
      }

      case 'time': return h('span', { class: `chip ${f.tone}`, title: 'Time spent' }, icon('timer', { size: 12 }), duration(f.seconds));
      default: return null;
    }
  }

  function card(node, column) {
    const facts = cardFacts(node, new Date());
    const dueTone = facts.find((f) => f.kind === 'overdue' || f.kind === 'due')?.tone ?? '';
    const due = node.deadline && !node.done ? h('span', { class: `card-due ${dueTone}` }, icon('calendar', { size: 12 }), ctx.relativeTime(node.deadline)) : null;
    const waiting = node.nextActionAt && new Date(node.nextActionAt) > new Date() && !node.done;
    const el = h('article', {
      class: `kcard prio-${node.priority}${node.done ? ' is-done' : ''}${node.timing ? ' is-timing' : ''}${waiting ? ' is-waiting' : ''}`,
      draggable: 'true',
      tabindex: '0',
      role: 'listitem',
      'aria-label': `${node.title}${node.done ? ', done' : ''}`,
      dataset: { id: node.id },
      onclick: (e) => { if (!e.target.closest('button, a')) ctx.openDrawer(node.id); },
      onkeydown: (e) => onCardKey(e, node),
      ondragstart: (e) => {
        dragging = node;
        el.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', node.id);
      },
      ondragend: () => { dragging = null; el.classList.remove('dragging'); clearMarks(); },
    },
    h('div', { class: 'card-top' },
      h('span', { class: 'card-title' }, node.title),
      h('div', { class: 'card-actions' },
        node.done ? null : h('button', {
          class: `icon-btn small${node.timing ? ' on' : ''}`, type: 'button', title: node.timing ? 'Stop the timer' : 'Start the timer',
          'aria-label': node.timing ? `Stop timing ${node.title}` : `Start timing ${node.title}`,
          onclick: () => run([node.timing ? { method: 'POST', url: '/api/timer/stop' } : { method: 'POST', url: '/api/timer/start', body: { itemId: node.id } }]),
        }, icon(node.timing ? 'stop' : 'play', { size: 14 })),
        column === 'done'
          ? h('button', { class: 'icon-btn small', type: 'button', title: 'Archive', 'aria-label': `Archive ${node.title}`, onclick: () => run([{ method: 'POST', url: `/api/items/${node.id}/archive` }], 'Archived') }, icon('archive', { size: 14 }))
          : h('button', { class: 'icon-btn small', type: 'button', title: 'Done', 'aria-label': `Complete ${node.title}`, onclick: () => run([{ method: 'POST', url: `/api/items/${node.id}/complete` }], 'Done') }, icon('check', { size: 14 })))),
    node.labels.length || node.tags.length ? h('div', { class: 'card-chips' }, ...node.labels.map((l) => ctx.labelChip(l)), ...node.tags.slice(0, 3).map((t) => h('span', { class: 'chip tag' }, `#${t}`))) : null,
    h('div', { class: 'card-meta' }, due, ...facts.map(fact)));
    return el;
  }

  function onCardKey(e, node) {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'Enter') {
      e.preventDefault();
      ctx.openDrawer(node.id);
      return;
    }

    if (!e.altKey || !e.key.startsWith('Arrow')) return;
    e.preventDefault();
    const move = keyboardMove(node, e.key, columns);
    if (!move) return;
    ctx.state.refocus = node.id;
    run(dropRequests(node, move.column, move.before, columns), move.column === 'done' ? 'Done' : null).then(() => { ctx.state.refocus = null; });
  }

  function clearMarks() {
    root.querySelectorAll('.drop-before, .drop-after, .drop-target').forEach((x) => x.classList.remove('drop-before', 'drop-after', 'drop-target'));
  }

  /** The card the pointer is above (the drop goes before it), or null for the end of the column. */
  function beforeAt(list, y) {
    for (const el of list.querySelectorAll('.kcard:not(.dragging)')) {
      const r = el.getBoundingClientRect();
      if (y < r.top + r.height / 2) return el;
    }

    return null;
  }

  function column(def) {
    const items = columns[def.id];
    const fill = def.id === 'doing' ? wip(items.length) : 'ok';
    const add = def.id === 'done' ? null : h('form', {
      class: 'card-add',
      onsubmit: async (e) => {
        e.preventDefault();
        const input = e.target.querySelector('input');
        const title = input.value.trim();
        if (!title) return;
        input.value = '';
        const ok = await ctx.act(ctx.post('/api/items', { title, groupId: ctx.state.group, stage: def.id }), 'Added');
        if (!ok) input.value = title;
        root.querySelector(`.lane[data-column="${def.id}"] .card-add input`)?.focus();
      },
    }, h('input', { placeholder: `Add to ${def.name}…`, maxlength: 300, 'aria-label': `Add a task to ${def.name}` }));
    const list = h('div', { class: 'column-cards', role: 'list', 'aria-label': def.name }, ...items.map((n) => card(n, def.id)));
    if (!items.length) list.append(h('p', { class: 'column-empty' }, { inbox: 'Nothing new. Press N to capture.', next: 'Pick something from the inbox.', doing: 'Start one thing.', done: 'Finished cards land here.' }[def.id]));
    const el = h('section', {
      class: `lane wip-${fill}`,
      dataset: { column: def.id },
      'aria-label': def.name,
      ondragover: (e) => {
        if (!dragging) return;
        e.preventDefault();
        clearMarks();
        el.classList.add('drop-target');
        const before = def.id === 'done' ? null : beforeAt(list, e.clientY);
        if (before) before.classList.add('drop-before');
      },
      ondragleave: (e) => { if (!el.contains(e.relatedTarget)) el.classList.remove('drop-target'); },
      ondrop: (e) => {
        e.preventDefault();
        if (!dragging) return;
        const before = def.id === 'done' ? null : beforeAt(list, e.clientY);
        const moved = dragging;
        clearMarks();
        run(dropRequests(moved, def.id, before?.dataset.id ?? null, columns), def.id === 'done' ? 'Done' : null);
      },
    },
    h('header', { class: 'column-head' },
      h('span', { class: 'column-name' }, icon({ inbox: 'inbox', next: 'list', doing: 'play', done: 'check' }[def.id], { size: 15 }), def.name),
      h('span', { class: `column-count${fill !== 'ok' ? ` ${fill}` : ''}`, title: def.id === 'doing' ? `Keep Doing to ${WIP_LIMIT}: finishing beats starting` : def.hint },
        def.id === 'doing' ? `${items.length}/${WIP_LIMIT}` : String(items.length)),
      def.id === 'done' ? h('button', { class: 'link small push-right', type: 'button', onclick: () => ctx.navigate('done') }, 'All done →') : null),
    fill === 'over' ? h('p', { class: 'wip-note', role: 'status' }, 'More than 3 at once splits attention. Finish one, or move one back to Next.') : null,
    list,
    add);
    return el;
  }

  function render() {
    columns = columnsOf(nodes, new Date());
    const focusId = ctx.state.refocus ?? document.activeElement?.closest?.('#view-board .kcard')?.dataset.id;
    root.replaceChildren(
      h('p', { class: 'view-intro muted' }, 'Drag cards along, or use Alt + arrow keys. Starting a timer moves a card to Doing.'),
      h('div', { class: 'board' }, ...COLUMNS.map(column)));
    if (focusId) root.querySelector(`.kcard[data-id="${CSS.escape(focusId)}"]`)?.focus();
  }

  return { root, show: load, refresh: load, title: 'Board' };
}
