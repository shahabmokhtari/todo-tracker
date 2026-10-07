// Tasks: every task of a group as an outline. Click a title to rename it; Enter adds the next task, Tab and Shift+Tab
// nest and un-nest, Alt+Up/Down reorder, Space or the checkbox finishes; drag a row before, after or into another.

import { flatten, index, indentRequests, outdentRequests, stepRequests, dropZone, dropRequests } from '../outlinemodel.js';
import { duration } from '../timefmt.js';

const EXPANDED_KEY = 'tt.outline.expanded';
const DONE_KEY = 'tt.outline.showDone';

export function createOutlineView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-tasks');
  const expanded = new Set(JSON.parse(localStorage.getItem(EXPANDED_KEY) ?? '[]'));
  let showDone = localStorage.getItem(DONE_KEY) === '1';
  let roots = [];
  let map = new Map();
  let dragging = null;
  // A new task being typed: after `afterId` (as a sibling) or inside `parentId` (first), at the top when both are null.
  let adding = null;

  const saveExpanded = () => localStorage.setItem(EXPANDED_KEY, JSON.stringify([...expanded].slice(-500)));

  async function load() {
    try {
      roots = await ctx.api(`/api/tree${ctx.state.group ? `?group=${ctx.state.group}` : ''}`);
      map = index(roots);
      render();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  async function run(requests, message, focusId = null) {
    if (!requests?.length) return false;
    ctx.state.refocus = focusId;
    const ok = await ctx.act((async () => { for (const r of requests) await ctx.api(r.url, { method: r.method, body: r.body }); })(), message);
    ctx.state.refocus = null;
    return ok;
  }

  function focusRow(id, field = 'row') {
    const row = root.querySelector(`.orow[data-id="${CSS.escape(id)}"]`);
    (field === 'title' ? row?.querySelector('.otitle') : row)?.focus();
  }

  async function rename(node, input) {
    const title = input.value.trim();
    if (!title || title === node.title) {
      input.value = node.title;
      return;
    }

    try {
      await ctx.api(`/api/items/${node.id}`, { method: 'PATCH', body: { title } });
      // Names read out for the row and its buttons follow the new title (no redraw: typing elsewhere is kept).
      const old = node.title;
      const row = input.closest('.orow');
      for (const el of row ? [row, ...row.querySelectorAll('[aria-label]')] : []) {
        const label = el.getAttribute('aria-label');
        if (label === old) el.setAttribute('aria-label', title);
        else if (label.endsWith(` ${old}`)) el.setAttribute('aria-label', label.slice(0, -old.length) + title);
      }

      node.title = title;
    } catch (err) {
      input.value = node.title;
      ctx.toast(err.message, 'error');
    }
  }

  async function create(title, at) {
    const body = { title, groupId: ctx.state.group, stage: 'next' };
    const parentId = at.parentId ?? (at.afterId ? map.get(at.afterId)?.parent?.id : null);
    if (parentId) body.parentId = parentId;
    let created;
    try {
      created = await ctx.post('/api/items', body);
    } catch (err) {
      ctx.toast(err.message, 'error');
      return null;
    }

    // In place: after the task it was typed under (or first inside its parent).
    const siblings = parentId ? map.get(parentId).node.children : roots;
    const after = at.afterId ? siblings.findIndex((n) => n.id === at.afterId) : -1;
    const before = siblings[after + 1]?.id ?? null;
    if (before) await ctx.api(`/api/items/${created.id}/reorder`, { method: 'POST', body: { before } }).catch(() => {});
    if (parentId) expanded.add(parentId);
    saveExpanded();
    return created;
  }

  function newRowEditor(depth, at) {
    const input = h('input', { class: 'otitle new', placeholder: 'New task', maxlength: 300, 'aria-label': 'New task' });
    const finish = async (keepGoing) => {
      const title = input.value.trim();
      if (!title) {
        adding = null;
        render();
        return;
      }

      input.disabled = true;
      const created = await create(title, at);
      adding = created && keepGoing ? { afterId: created.id } : null;
      await ctx.refreshAll();
      if (!keepGoing && created) focusRow(created.id);
    };
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); finish(true); }
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); adding = null; render(); }
    });
    input.addEventListener('blur', () => { if (!input.disabled && input.value.trim()) finish(false); else if (!input.disabled) { adding = null; setTimeout(render); } });
    const row = h('div', { class: 'orow adding', role: 'treeitem', 'aria-level': depth + 1 }, h('span', { class: 'otwisty' }), h('span', { class: 'ocheck' }), input);
    row.style.setProperty('--depth', depth);
    setTimeout(() => input.focus());
    return row;
  }

  function row({ node, depth, hasChildren, expanded: open }) {
    const title = h('input', {
      class: 'otitle', value: node.title, maxlength: 300, 'aria-label': `Title of ${node.title}`, tabindex: -1,
      onblur: () => rename(node, title),
      onkeydown: (e) => {
        if (e.key === 'Enter') { e.preventDefault(); title.blur(); adding = { afterId: node.id }; render(); }
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); title.value = node.title; focusRow(node.id); }
      },
    });
    const facts = [];
    if (node.deadline && !node.done) facts.push(h('span', { class: `ofact${new Date(node.deadline) < new Date() ? ' danger' : ''}` }, icon('calendar', { size: 12 }), ctx.relativeTime(node.deadline)));
    if (node.totalCount) facts.push(h('span', { class: 'ofact' }, `${node.doneCount}/${node.totalCount}`));
    if (node.timeSpentSeconds || node.timing) facts.push(h('span', { class: `ofact${node.timing ? ' accent' : ''}` }, icon('timer', { size: 12 }), duration(node.timeSpentSeconds)));
    if (node.noteCount) facts.push(h('span', { class: 'ofact' }, icon('note', { size: 12 }), String(node.noteCount)));
    const el = h('div', {
      class: `orow prio-${node.priority}${node.done ? ' is-done' : ''}${node.timing ? ' is-timing' : ''}`,
      role: 'treeitem',
      'aria-level': depth + 1,
      'aria-expanded': hasChildren ? String(open) : null,
      'aria-label': node.title,
      tabindex: 0,
      draggable: 'true',
      dataset: { id: node.id },
      onkeydown: (e) => onRowKey(e, node, hasChildren, open),
      ondragstart: (e) => {
        if (e.target === title) { e.preventDefault(); return; }
        dragging = node.id;
        el.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', node.id);
      },
      ondragend: () => { dragging = null; el.classList.remove('dragging'); clearMarks(); },
      ondragover: (e) => {
        if (!dragging || dragging === node.id) return;
        e.preventDefault();
        clearMarks();
        const r = el.getBoundingClientRect();
        el.classList.add(`drop-${dropZone(e.clientY - r.top, r.height)}`);
      },
      ondragleave: () => el.classList.remove('drop-before', 'drop-inside', 'drop-after'),
      ondrop: (e) => {
        e.preventDefault();
        const r = el.getBoundingClientRect();
        const zone = dropZone(e.clientY - r.top, r.height);
        const moved = dragging;
        clearMarks();
        const requests = dropRequests(map, moved, node.id, zone, ctx.state.group);
        if (!requests) return ctx.toast('A task can’t go inside itself', 'error');
        if (zone === 'inside') expanded.add(node.id);
        saveExpanded();
        run(requests, 'Moved', moved);
      },
    },
    h('button', {
      class: 'otwisty', type: 'button', tabindex: -1, 'aria-hidden': 'true', disabled: !hasChildren,
      onclick: () => toggle(node.id, !open),
    }, hasChildren ? icon('chevron', { size: 14, className: open ? 'open' : '' }) : null),
    h('button', {
      class: 'ocheck', type: 'button', tabindex: -1, 'aria-label': node.done ? `Reopen ${node.title}` : `Complete ${node.title}`,
      onclick: () => complete(node),
    }, node.done ? icon('check', { size: 13 }) : null),
    title,
    h('span', { class: 'ochips' }, ...node.labels.map((l) => ctx.labelChip(l)), ...node.tags.slice(0, 2).map((t) => h('span', { class: 'chip tag' }, `#${t}`))),
    h('span', { class: 'ofacts' }, ...facts),
    h('span', { class: 'oactions' },
      node.done ? null : h('button', { class: `icon-btn small${node.timing ? ' on' : ''}`, type: 'button', tabindex: -1, title: node.timing ? 'Stop the timer' : 'Start the timer', 'aria-label': node.timing ? `Stop timing ${node.title}` : `Start timing ${node.title}`,
        onclick: () => run([node.timing ? { method: 'POST', url: '/api/timer/stop' } : { method: 'POST', url: '/api/timer/start', body: { itemId: node.id } }], null, node.id) }, icon(node.timing ? 'stop' : 'play', { size: 13 })),
      h('button', { class: 'icon-btn small', type: 'button', tabindex: -1, title: 'Add a subtask', 'aria-label': `Add a subtask to ${node.title}`, onclick: () => { expanded.add(node.id); adding = { parentId: node.id }; render(); } }, icon('plus', { size: 13 })),
      h('button', { class: 'icon-btn small', type: 'button', tabindex: -1, title: 'Open (Ctrl+Enter)', 'aria-label': `Open ${node.title}`, onclick: () => ctx.openDrawer(node.id) }, icon('expand', { size: 13 }))));
    el.style.setProperty('--depth', depth);
    title.addEventListener('focus', () => { title.tabIndex = 0; });
    title.addEventListener('mousedown', (e) => e.stopPropagation());
    return el;
  }

  function complete(node) {
    if (node.done) return run([{ method: 'POST', url: `/api/items/${node.id}/reopen` }], 'Reopened', node.id);
    const open = (node.children ?? []).filter((c) => !c.done).length;
    if (open && !confirm(`Also mark ${open} open subtask${open > 1 ? 's' : ''} as done?`)) return null;
    return run([{ method: 'POST', url: `/api/items/${node.id}/complete` }], 'Done', node.id);
  }

  function toggle(id, open) {
    if (open) expanded.add(id);
    else expanded.delete(id);
    saveExpanded();
    render();
    focusRow(id);
  }

  function onRowKey(e, node, hasChildren, open) {
    if (e.target !== e.currentTarget) return;
    const rows = [...root.querySelectorAll('.orow[data-id]')];
    const i = rows.findIndex((r) => r.dataset.id === node.id);
    const go = (k) => rows[k]?.focus();
    if (e.key === 'ArrowDown' && e.altKey) { e.preventDefault(); run(stepRequests(map, node.id, 1), null, node.id); }
    else if (e.key === 'ArrowUp' && e.altKey) { e.preventDefault(); run(stepRequests(map, node.id, -1), null, node.id); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); go(i + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); go(i - 1); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); if (hasChildren && !open) toggle(node.id, true); else go(i + 1); }
    else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      if (hasChildren && open) toggle(node.id, false);
      else if (map.get(node.id)?.parent) focusRow(map.get(node.id).parent.id);
    } else if (e.key === 'Tab' && !e.shiftKey) {
      const requests = indentRequests(map, node.id, showDone);
      if (requests) { e.preventDefault(); expanded.add(requests[0].body.parentId); saveExpanded(); run(requests, null, node.id); }
    } else if (e.key === 'Tab' && e.shiftKey) {
      const requests = outdentRequests(map, node.id, ctx.state.group);
      if (requests) { e.preventDefault(); run(requests, null, node.id); }
    } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); ctx.openDrawer(node.id); }
    else if (e.key === 'Enter' || e.key === 'F2') { e.preventDefault(); const t = e.currentTarget.querySelector('.otitle'); t.focus(); t.select(); }
    else if (e.key === ' ') { e.preventDefault(); complete(node); }
    else if (e.key === 'Insert' || (e.key === 'n' && e.altKey)) { e.preventDefault(); adding = { afterId: node.id }; render(); }
  }

  function clearMarks() {
    root.querySelectorAll('.drop-before, .drop-inside, .drop-after').forEach((x) => x.classList.remove('drop-before', 'drop-inside', 'drop-after'));
  }

  function render() {
    const keep = ctx.state.refocus ?? document.activeElement?.closest?.('#view-tasks .orow')?.dataset.id;
    const rows = flatten(roots, expanded, showDone);
    const tree = h('div', { class: 'outline', role: 'tree', 'aria-label': 'Tasks' });
    if (adding && !adding.afterId && !adding.parentId) tree.append(newRowEditor(0, adding));
    for (const r of rows) {
      tree.append(row(r));
      if (adding?.parentId === r.node.id) tree.append(newRowEditor(r.depth + 1, adding));
      // After the task and everything shown under it.
      if (adding?.afterId === r.node.id) {
        const next = rows[rows.indexOf(r) + 1];
        if (!next || next.depth <= r.depth) tree.append(newRowEditor(r.depth, adding));
      }
    }

    // "After X" where X's subtasks are shown: the editor goes after the last of them.
    if (adding?.afterId && !tree.querySelector('.orow.adding')) {
      const at = rows.findIndex((r) => r.node.id === adding.afterId);
      if (at >= 0) {
        let end = at + 1;
        while (end < rows.length && rows[end].depth > rows[at].depth) end++;
        const anchor = tree.querySelectorAll('.orow[data-id]')[end - 1];
        anchor?.after(newRowEditor(rows[at].depth, adding));
      }
    }

    if (!rows.length && !adding) tree.append(h('p', { class: 'empty-state' }, 'No tasks here yet. Press ', h('kbd', null, 'Insert'), ' or the button to add one.'));
    const doneToggle = h('label', { class: 'check small' }, h('input', { type: 'checkbox', checked: showDone, onchange: (e) => { showDone = e.target.checked; localStorage.setItem(DONE_KEY, showDone ? '1' : '0'); render(); } }), 'Show finished');
    root.replaceChildren(
      h('div', { class: 'view-toolbar' },
        h('button', { class: 'btn primary', type: 'button', onclick: () => { adding = { afterId: null, parentId: null }; render(); } }, icon('plus', { size: 16 }), 'New task'),
        h('button', { class: 'btn ghost', type: 'button', onclick: () => { roots.forEach(function walk(n) { if (n.children?.length) { expanded.add(n.id); n.children.forEach(walk); } }); saveExpanded(); render(); } }, 'Expand all'),
        h('button', { class: 'btn ghost', type: 'button', onclick: () => { expanded.clear(); saveExpanded(); render(); } }, 'Collapse all'),
        doneToggle,
        h('span', { class: 'muted small push-right kbd-hints' }, h('kbd', null, 'Enter'), ' rename · ', h('kbd', null, 'Tab'), ' nest · ', h('kbd', null, 'Alt ↑↓'), ' move · ', h('kbd', null, 'Space'), ' done')),
      tree);
    if (keep && !root.querySelector('.orow.adding')) focusRow(keep);
  }

  return { root, show: load, refresh: load, title: 'Tasks' };
}
