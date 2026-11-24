// Tasks: every task as an outline. Under All groups the top level is the groups; a parent shows how far along it is;
// ⊙ zooms into a branch (#/tasks/<id>, with a path back). Click a row's title to select it, click it again (or Enter,
// F2) to rename it; Enter adds the next task, Tab and Shift+Tab (or Alt+Shift+Right/Left) nest and un-nest,
// Alt+Up/Down reorder, Alt+Enter zooms in, Space or the checkbox finishes, Escape goes up one level of the zoom, then
// leaves the tree; drag a row before, after or into another, or onto a group (or use a row's Move menu on a touch
// screen).

import {
  index, indentRequests, outdentRequests, stepRequests, dropZone, dropRequests, groupDropRequests,
  outlineRows, outlinePath, resolveZoom, rememberedZoom, zoomUp, openTo, newTaskPlace, besidesOf, shownTitle, shownTags, initialShow, SHOW_CHOICES,
} from '../outlinemodel.js';
import { duration } from '../timefmt.js';
import { latest, serial, sameAs, send, treeUrl, keepToolbarFocus, DRAG_TYPE } from './common.js';
import { iconBadge, repeatMark, withAccent } from '../looks.js';

const EXPANDED_KEY = 'onething.outline.expanded';
const CLOSED_KEY = 'onething.outline.closedGroups';
const SHOW_KEY = 'onething.outline.show';
// The old "Show finished" box: read once to pick the first Show choice, and left in place.
const DONE_KEY = 'onething.outline.showDone';
const ROWS = '.orow[data-key]';

const read = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private window */ } };
const readSet = (key) => { try { return new Set(JSON.parse(read(key) ?? '[]')); } catch { return new Set(); } };

const LEVELS = [['groups', 'Groups'], ['tasks', 'Tasks'], ['subtasks', 'Subtasks'], ['all', 'All']];
const EMPTY = {
  now: 'Nothing can be done right now.',
  waiting: 'Nothing is waiting.',
};
// In a zoom: what the Show choice leaves out there, and how to see it.
const EMPTY_HERE = {
  todo: 'Nothing left to do here. Show › Everything shows what’s done.',
  now: 'Nothing here can be done right now. Show › To do shows the rest.',
  waiting: 'Nothing here is waiting. Show › To do shows the rest.',
  all: 'Nothing under this task.',
};

export function createOutlineView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-tasks');
  let expanded = readSet(EXPANDED_KEY);
  let closedGroups = readSet(CLOSED_KEY);
  let show = initialShow(read(SHOW_KEY), read(DONE_KEY));
  let roots = [];
  let map = new Map();
  let groups = [];
  let dragging = null;
  // The branch zoomed into (from the address), and its parents as last drawn (to zoom out to when it's gone).
  let zoomId = null;
  let trail = [];
  // A new task being typed: after `afterId` (as a sibling), inside `parentId` (first), or first in `groupId`.
  let adding = null;
  // The rows as last drawn, by key (task or group id).
  let drawn = new Map();

  // Group rows stay under the top bar while their tasks scroll; how tall it is depends on the screen.
  const topbar = document.querySelector('.topbar');
  const stickUnder = () => root.style.setProperty('--sticky-top', `${topbar?.offsetHeight ?? 0}px`);
  if (topbar && typeof ResizeObserver === 'function') new ResizeObserver(stickUnder).observe(topbar);

  // Escape where no row takes it (the path, an empty zoom): up one level all the same.
  root.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || e.defaultPrevented || !zoomId || e.target.closest?.('input, select, textarea')) return;
    e.preventDefault();
    zoomBack();
  });

  // Kept in the order they were opened (the newest few hundred), without tasks and groups that are gone. Only the whole
  // tree (no group picked, no search) knows every task.
  const wholeTree = () => !ctx.state.group && !ctx.state.query?.trim();
  const saveExpanded = () => {
    const known = [...expanded].filter((id) => !wholeTree() || !roots.length || map.has(id));
    write(EXPANDED_KEY, JSON.stringify(known.slice(-500)));
  };
  const saveClosed = () => write(CLOSED_KEY, JSON.stringify([...closedGroups].filter((id) => !groups.length || groups.some((g) => g.id === id))));
  const fetchTree = latest(() => ctx.api(treeUrl(ctx)));
  const queue = serial();
  let shown = null;
  // The row the keyboard comes back to (the tree is one Tab stop; arrows move inside it).
  let activeId = null;
  // After a zoom: the row to focus (the task zoomed out of), or the first row (zoomed in).
  let focusNext = null;

  const options = () => ({
    groupId: ctx.state.group || null, zoomId, show, query: ctx.state.query ?? '', expanded, closedGroups, trail,
  });

  /** The groups in the board's order (the side bar has them; asked for once if it hasn't yet). */
  async function loadGroups() {
    const known = ctx.groups();
    if (known.length) groups = known;
    else if (!groups.length) groups = await ctx.api('/api/groups').catch(() => []);
  }

  /** `quiet`: a background refresh, which leaves the outline alone when nothing changed. */
  async function load({ quiet = false } = {}) {
    try {
      const reply = await fetchTree();
      if (!reply.current) return;
      await loadGroups();
      const { same, text } = sameAs(shown, [reply.value, groups.map((g) => [g.id, g.name, g.color])]);
      shown = text;
      if (quiet && same) return;
      roots = reply.value;
      map = index(roots);
      render();
    } catch (err) {
      if (!quiet) ctx.toast(err.message, 'error');
    }
  }

  /** Changes go one at a time; `build` makes the requests from the tree as it is when the change's turn comes. */
  const run = (build, message, focusId = null) => queue(() => send(ctx, typeof build === 'function' ? build() : build, message, focusId));

  const rowEl = (key) => root.querySelector(`.orow[data-key="${CSS.escape(key)}"]`);

  /** Focuses a row (a task's or a group's); false when it isn't shown. */
  function focusRow(key) {
    const row = rowEl(key);
    row?.focus({ preventScroll: true });
    row?.scrollIntoView?.({ block: 'nearest' });
    return !!row;
  }

  /** Names read out for the row and its buttons follow its title (no redraw: typing elsewhere is kept). */
  function relabel(row, from, to) {
    for (const el of row ? [row, ...row.querySelectorAll('[aria-label]')] : []) {
      const label = el.getAttribute('aria-label');
      if (label === from) el.setAttribute('aria-label', to);
      else if (label.endsWith(` ${from}`)) el.setAttribute('aria-label', label.slice(0, -from.length) + to);
    }
  }

  async function rename(node, input) {
    const title = input.value.trim();
    const old = node.title;
    if (!title || title === old) {
      input.value = old;
      return;
    }

    // Shown right away (Enter draws the next row at once); put back if the save fails.
    node.title = title;
    relabel(input.closest('.orow'), old, title);
    try {
      await ctx.api(`/api/items/${node.id}`, { method: 'PATCH', body: { title } });
    } catch (err) {
      node.title = old;
      input.value = old;
      relabel(input.closest('.orow'), title, old);
      ctx.toast(err.message, 'error');
    }
  }

  // ---- zoom ----------------------------------------------------------------------------------------------------

  /** Into a task's branch (the address changes, so Back comes out again). */
  function zoomTo(id, focus = { first: true }) {
    adding = null;
    focusNext = focus;
    location.hash = id ? `#/tasks/${encodeURIComponent(id)}` : '#/tasks';
  }

  /** Escape: up one level (to the zoomed task's parent), and from the first level out to the whole tree. */
  function zoomBack() {
    const opts = options();
    const up = zoomUp(roots, opts);
    if (up) zoomTo(up, { key: resolveZoom(roots, opts) });
    else zoomOut();
  }

  /** Out of the zoom, to the whole tree, with the task that was zoomed into in sight and focused. */
  function zoomOut() {
    const from = zoomId;
    if (!from) return;
    const node = map.get(from)?.node;
    trail.forEach((id) => expanded.add(id));
    if (node) closedGroups.delete(node.groupId);
    saveExpanded();
    saveClosed();
    zoomTo(null, { key: from });
  }

  /** Called by the router before the view draws: the zoom the address names (null: none). */
  function setZoom(id) {
    if (id === zoomId) return;
    // Back or a link while the keyboard is in the tree: it lands on the first row.
    if (!focusNext && root.contains(document.activeElement)) focusNext = { first: true };
    zoomId = id;
    if (!id) trail = [];
    adding = null;
  }

  // ---- adding --------------------------------------------------------------------------------------------------

  async function create(title, at) {
    const after = at.afterId ? map.get(at.afterId) : null;
    const parentId = at.parentId ?? after?.parent?.id ?? null;
    const groupId = after?.node.groupId ?? at.groupId ?? ctx.state.group ?? null;
    const body = { title, stage: 'next' };
    if (parentId) body.parentId = parentId;
    else if (groupId) body.groupId = groupId;
    let created;
    try {
      created = await ctx.post('/api/items', body);
    } catch (err) {
      ctx.toast(err.message, 'error');
      return null;
    }

    // In place: after the task it was typed under, else first inside its parent or group (by position: a search or a
    // Show choice may leave the first one out of the tree).
    const place = at.afterId
      ? [`/api/items/${created.id}/reorder`, { after: at.afterId }]
      : [`/api/items/${created.id}/move`, parentId ? { parentId, index: 0 } : { toTopLevel: true, groupId: created.groupId, index: 0 }];
    await ctx.api(place[0], { method: 'POST', body: place[1] }).catch(() => {});
    if (parentId && parentId !== zoomId) expanded.add(parentId);
    saveExpanded();
    return created;
  }

  function newRowEditor(depth, at) {
    // What's typed is kept with `at`: a redraw (or an add that didn't go through) doesn't lose it.
    const input = h('input', { class: 'otitle new', placeholder: 'New task', maxlength: 300, 'aria-label': 'New task', value: at.draft ?? '', oninput: () => { at.draft = input.value; } });
    const finish = async (keepGoing) => {
      const title = input.value.trim();
      if (!title) {
        adding = null;
        render();
        return;
      }

      input.disabled = true;
      const created = await create(title, at);
      if (!created) {
        // Not added (the reason was shown): the text stays in the box to try again.
        input.disabled = false;
        if (keepGoing) input.focus();
        return;
      }
      adding = keepGoing ? { afterId: created.id } : null;
      await ctx.refreshAll();
      if (!keepGoing) focusRow(created.id);
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

  /** New task: inside the zoomed task, else first in the group picked (all groups: the first group). */
  function startAdding() {
    const place = newTaskPlace(roots, groups, options());
    if (place.parentId) adding = { parentId: place.parentId };
    else if (!ctx.state.group && place.groupId) {
      adding = { groupId: place.groupId };
      closedGroups.delete(place.groupId);
      saveClosed();
    } else adding = { afterId: null, parentId: null };
    render();
  }

  // ---- rows ----------------------------------------------------------------------------------------------------

  /** Done/total and a thin bar (leaves under it). */
  function progress(done, total) {
    const fill = h('span', { class: 'obar-fill' });
    fill.style.setProperty('--p', `${total ? Math.round((done / total) * 100) : 0}%`);
    return h('span', { class: 'oprogress', title: `${done} of ${total} done` },
      h('span', { class: 'obar', 'aria-hidden': 'true' }, fill),
      h('span', { class: 'ocount' }, `${done}/${total}`));
  }

  /** Drop handlers shared by task and group rows. */
  function dropTarget(el, zoneOf, onDrop) {
    el.addEventListener('dragover', (e) => {
      if (!dragging || dragging === el.dataset.key) return;
      e.preventDefault();
      clearMarks();
      el.classList.add(`drop-${zoneOf(e)}`);
    });
    el.addEventListener('dragleave', () => el.classList.remove('drop-before', 'drop-inside', 'drop-after'));
    el.addEventListener('drop', (e) => {
      e.preventDefault();
      const zone = zoneOf(e);
      const moved = dragging;
      clearMarks();
      if (moved) onDrop(moved, zone);
    });
  }

  function groupRow(r) {
    const g = r.group;
    const el = h('div', {
      class: 'orow ogroup',
      role: 'treeitem',
      'aria-level': 1,
      'aria-expanded': String(r.expanded),
      'aria-label': `${g.name}, ${r.left} to do`,
      tabindex: g.id === activeId ? 0 : -1,
      dataset: { key: g.id, group: g.id },
      onfocus: (e) => { if (e.target === el) setActive(g.id); },
      onkeydown: (e) => onGroupKey(e, r),
    },
    h('button', { class: 'otwisty', type: 'button', tabindex: -1, 'aria-hidden': 'true', onclick: () => toggleGroup(g.id, !r.expanded) }, icon('chevron', { size: 14, className: r.expanded ? 'open' : '' })),
    h('span', { class: 'gdot', 'aria-hidden': 'true' }),
    h('span', { class: 'otext gname' }, g.name),
    h('span', { class: 'gleft' }, r.left ? `${r.left} to do` : r.total ? 'All done' : 'No tasks yet'),
    r.total ? progress(r.done, r.total) : null,
    h('span', { class: 'oactions' },
      h('button', { class: 'icon-btn small', type: 'button', tabindex: -1, title: `Add a task to ${g.name}`, 'aria-label': `Add a task to ${g.name}`, onclick: () => { closedGroups.delete(g.id); saveClosed(); adding = { groupId: g.id }; render(); } }, icon('plus', { size: 13 }))));
    if (g.color) el.style.setProperty('--group', g.color);
    el.addEventListener('click', (e) => { if (e.target.closest('.otext')) toggleGroup(g.id, !r.expanded); });
    dropTarget(el, () => 'inside', (moved) => {
      const requests = groupDropRequests(map, moved, g.id);
      if (!requests) return;
      closedGroups.delete(g.id);
      saveClosed();
      run(requests, `Moved to ${g.name}`, moved);
    });
    return el;
  }

  /** The title as text (wraps to two lines); the selected row's title, Enter or F2 turns it into the box. */
  function titleText(r) {
    return h('span', { class: 'otext', title: r.fullTitle !== r.title ? r.fullTitle : null }, r.title);
  }

  // The title being renamed: saved before the tree is drawn again (a zoom, Show in tree, the Back button), as leaving
  // the box does, so what was typed is never lost.
  let renaming = null;

  function startRename(el, r) {
    const node = r.node;
    const text = el.querySelector('.otext');
    if (!text) return;
    let cancelled = false;
    const input = h('input', {
      class: 'otitle', value: node.title, maxlength: 300, 'aria-label': `Title of ${node.title}`,
      onkeydown: (e) => {
        if (e.key === 'Enter') { e.preventDefault(); input.blur(); adding = { afterId: node.id }; render(); }
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancelled = true; input.value = node.title; focusRow(node.id); }
      },
      onblur: async () => {
        if (renaming?.input === input) renaming = null;
        if (!cancelled) await rename(node, input);
        if (input.isConnected) input.replaceWith(titleText({ title: shownTitle(node.title, r.parent?.title, r.parentShown), fullTitle: node.title }));
      },
    });
    input.addEventListener('mousedown', (e) => e.stopPropagation());
    renaming = { node, input, cancelled: () => cancelled };
    text.replaceWith(input);
    input.focus();
    input.select();
  }

  function row(r) {
    const { node, depth, hasChildren, expanded: open } = r;
    const facts = [];
    if (node.deadline && !node.done) facts.push(h('span', { class: `ofact${new Date(node.deadline) < new Date() ? ' danger' : ''}` }, icon('calendar', { size: 12 }), ctx.relativeTime(node.deadline)));
    if (node.timeSpentSeconds || node.timing) facts.push(h('span', { class: `ofact${node.timing ? ' accent' : ''}` }, icon('timer', { size: 12 }), duration(node.timeSpentSeconds)));
    if (node.noteCount) facts.push(h('span', { class: 'ofact' }, icon('note', { size: 12 }), String(node.noteCount)));
    let selected = false;
    const text = titleText(r);
    const el = h('div', {
      class: `orow prio-${node.priority}${node.done ? ' is-done' : ''}${node.timing ? ' is-timing' : ''}`,
      role: 'treeitem',
      'aria-level': depth + 1,
      'aria-expanded': hasChildren ? String(open) : null,
      'aria-label': node.title,
      tabindex: node.id === activeId ? 0 : -1,
      draggable: 'true',
      dataset: { id: node.id, key: node.id },
      onfocus: (e) => { if (e.target === el) setActive(node.id); },
      onkeydown: (e) => onRowKey(e, r),
      ondragstart: (e) => {
        if (e.target.closest?.('input, select')) { e.preventDefault(); return; }
        dragging = node.id;
        el.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData(DRAG_TYPE, node.id);
      },
      ondragend: () => { dragging = null; el.classList.remove('dragging'); clearMarks(); },
    },
    h('button', {
      class: 'otwisty', type: 'button', tabindex: -1, 'aria-hidden': 'true', disabled: !hasChildren,
      onclick: () => toggle(node.id, !open),
    }, hasChildren ? icon('chevron', { size: 14, className: open ? 'open' : '' }) : null),
    h('button', {
      class: 'ocheck', type: 'button', tabindex: -1, 'aria-label': node.done ? `Reopen ${node.title}` : `Complete ${node.title}`,
      onclick: () => complete(node),
    }, node.done ? icon('check', { size: 13 }) : null),
    iconBadge(node.icon),
    text,
    repeatMark(node.repeat),
    h('span', { class: 'ochips' }, ...node.labels.map((l) => ctx.labelChip(l)), ...shownTags(node.tags, r.parent?.tags).slice(0, 2).map((t) => h('span', { class: 'chip tag' }, `#${t}`))),
    h('span', { class: 'ofacts' }, ...facts),
    r.total ? progress(r.done, r.total) : null,
    (node.children ?? []).length
      ? h('button', { class: 'icon-btn small ozoom', type: 'button', tabindex: -1, title: 'Zoom in: show only this', 'aria-label': `Zoom into ${node.title}`, onclick: () => zoomTo(node.id) }, icon('target', { size: 13 }))
      : null,
    h('span', { class: 'oactions' },
      node.done ? null : h('button', { class: `icon-btn small${node.timing ? ' on' : ''}`, type: 'button', tabindex: -1, title: node.timing ? 'Stop the timer' : 'Start the timer', 'aria-label': node.timing ? `Stop timing ${node.title}` : `Start timing ${node.title}`,
        onclick: () => run([node.timing ? { method: 'POST', url: '/api/timer/stop' } : { method: 'POST', url: '/api/timer/start', body: { itemId: node.id } }], null, node.id) }, icon(node.timing ? 'stop' : 'play', { size: 13 })),
      h('button', { class: 'icon-btn small', type: 'button', tabindex: -1, title: 'Add a subtask', 'aria-label': `Add a subtask to ${node.title}`, onclick: () => { expanded.add(node.id); adding = { parentId: node.id }; render(); } }, icon('plus', { size: 13 })),
      h('button', { class: 'icon-btn small', type: 'button', tabindex: -1, title: 'Open (Ctrl+Enter)', 'aria-label': `Open ${node.title}`, onclick: () => ctx.openDrawer(node.id) }, icon('expand', { size: 13 })),
      moveMenu(r)));
    el.style.setProperty('--depth', depth);
    withAccent(el, node.accent);
    // A click on the title selects the row; a click on the selected row's title renames it.
    text.addEventListener('mousedown', () => { selected = document.activeElement === el; });
    el.addEventListener('click', (e) => { if (selected && e.target.closest('.otext')) startRename(el, r); selected = false; });
    dropTarget(el, (e) => { const b = el.getBoundingClientRect(); return dropZone(e.clientY - b.top, b.height); }, (moved, zone) => {
      if (!dropRequests(map, moved, node.id, zone, ctx.state.group)) { ctx.toast('A task can’t go inside itself', 'error'); return; }
      if (zone === 'inside') expanded.add(node.id);
      saveExpanded();
      run(() => dropRequests(map, moved, node.id, zone, ctx.state.group), 'Moved', moved);
    });
    return el;
  }

  /** The tree is a single Tab stop: the row last used keeps it. */
  function setActive(id) {
    if (activeId === id) return;
    activeId = id;
    root.querySelectorAll(ROWS).forEach((r) => { r.tabIndex = r.dataset.key === id ? 0 : -1; });
  }

  /** The same moves as the keys, as a menu (touch screens can't drag, and have no Tab or Alt). */
  function moveMenu(r) {
    const choices = [['', 'Move…'], ['up', 'Up'], ['down', 'Down'], ['in', 'Into the task above']];
    if (r.outdent) choices.push(['out', 'Out one level']);
    if ((r.node.children ?? []).length) choices.push(['zoom', 'Zoom in (Alt+Enter)']);
    return h('select', {
      class: 'move-select', tabindex: -1, 'aria-label': `Move ${r.node.title}`,
      onchange: (e) => {
        const key = e.target.value;
        e.target.value = '';
        if (key === 'zoom') zoomTo(r.node.id);
        else if (key) move(r.node.id, key);
      },
    }, ...choices.map(([value, label]) => h('option', { value }, label)));
  }

  /** Whether a sibling is on screen next to this task (kept by Show, and in the same group at the top level). */
  const besides = (id) => besidesOf(roots, options(), id);

  /** Up/down among the rows shown, into the task above, or out one level; built from the tree when its turn comes. */
  function move(id, how) {
    const build = {
      up: () => stepRequests(map, id, -1, besides(id)),
      down: () => stepRequests(map, id, 1, besides(id)),
      in: () => {
        const requests = indentRequests(map, id, besides(id));
        if (requests) { expanded.add(requests[0].body.parentId); saveExpanded(); }
        return requests;
      },
      out: () => (drawn.get(id)?.outdent === false ? null : outdentRequests(map, id, ctx.state.group)),
    }[how];
    return run(build, null, id);
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

  function toggleGroup(id, open) {
    if (open) closedGroups.delete(id);
    else closedGroups.add(id);
    saveClosed();
    render();
    focusRow(id);
  }

  /** Arrow keys between rows, Escape out of the zoom then the tree, Insert to add. */
  function commonKey(e, i, rows) {
    const go = (k) => rows[k]?.focus();
    if (e.key === 'Escape') {
      e.preventDefault();
      if (zoomId) zoomBack();
      else root.querySelector('.view-toolbar .btn')?.focus();
    } else if (e.key === 'ArrowDown' && !e.altKey) { e.preventDefault(); go(i + 1); }
    else if (e.key === 'ArrowUp' && !e.altKey) { e.preventDefault(); go(i - 1); }
    else if (e.key === 'Home') { e.preventDefault(); go(0); }
    else if (e.key === 'End') { e.preventDefault(); go(rows.length - 1); }
    else return false;
    return true;
  }

  function onGroupKey(e, r) {
    if (e.target !== e.currentTarget) return;
    const rows = [...root.querySelectorAll(ROWS)];
    const i = rows.findIndex((x) => x.dataset.key === r.id);
    if (commonKey(e, i, rows)) return;
    if (e.key === 'ArrowRight') { e.preventDefault(); if (!r.expanded) toggleGroup(r.id, true); else if (r.hasChildren) rows[i + 1]?.focus(); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); if (r.expanded) toggleGroup(r.id, false); }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleGroup(r.id, !r.expanded); }
    else if (e.key === 'Insert' || (e.key === 'n' && e.altKey)) { e.preventDefault(); closedGroups.delete(r.id); saveClosed(); adding = { groupId: r.id }; render(); }
  }

  function onRowKey(e, r) {
    if (e.target !== e.currentTarget) return;
    const { node, hasChildren, expanded: open } = r;
    const rows = [...root.querySelectorAll(ROWS)];
    const i = rows.findIndex((x) => x.dataset.key === node.id);
    const nest = (e.key === 'Tab' && !e.shiftKey) || (e.key === 'ArrowRight' && e.altKey && e.shiftKey);
    const unnest = (e.key === 'Tab' && e.shiftKey) || (e.key === 'ArrowLeft' && e.altKey && e.shiftKey);
    if (nest || unnest) {
      // Tab only nests when it can; otherwise it moves on like Tab anywhere else.
      if (nest ? indentRequests(map, node.id, besides(node.id)) : r.outdent && outdentRequests(map, node.id, ctx.state.group)) {
        e.preventDefault();
        move(node.id, nest ? 'in' : 'out');
      }
    } else if (commonKey(e, i, rows)) { /* handled */ }
    else if (e.key === 'ArrowDown' && e.altKey) { e.preventDefault(); move(node.id, 'down'); }
    else if (e.key === 'ArrowUp' && e.altKey) { e.preventDefault(); move(node.id, 'up'); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); if (hasChildren && !open) toggle(node.id, true); else rows[i + 1]?.focus(); }
    else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      if (hasChildren && open) toggle(node.id, false);
      else if (r.parent) focusRow(r.parent.id);
      else if (!ctx.state.group && !zoomId) focusRow(node.groupId);
    } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); ctx.openDrawer(node.id); }
    else if (e.key === 'Enter' && e.altKey) { e.preventDefault(); if ((node.children ?? []).length) zoomTo(node.id); }
    else if (e.key === 'Enter' || e.key === 'F2') { e.preventDefault(); startRename(e.currentTarget, r); }
    else if (e.key === ' ') { e.preventDefault(); complete(node); }
    else if (e.key === 'Insert' || (e.key === 'n' && e.altKey)) { e.preventDefault(); adding = { afterId: node.id }; render(); }
  }

  function clearMarks() {
    root.querySelectorAll('.drop-before, .drop-inside, .drop-after').forEach((x) => x.classList.remove('drop-before', 'drop-inside', 'drop-after'));
  }

  // ---- drawing -------------------------------------------------------------------------------------------------

  /** The path above a zoomed tree: All groups › group › each parent › the zoomed task (its details). */
  function pathBar(parts) {
    const crumb = (p, i) => {
      const last = i === parts.length - 1;
      const go = () => {
        if (p.kind === 'all') { zoomTo(null); if (ctx.state.group) ctx.setGroup(null); }
        else if (p.kind === 'group') { zoomTo(null); if (ctx.state.group !== p.id) ctx.setGroup(p.id); }
        else if (last) ctx.openDrawer(p.id);
        else zoomTo(p.id);
      };
      const label = p.kind === 'all' ? 'All groups: leave the zoom' : last ? `${p.title}: open its details` : p.title;
      return h('li', null, h('button', { class: `crumb${last ? ' current' : ''}`, type: 'button', title: label, 'aria-label': label, 'aria-current': last ? 'location' : null, onclick: go },
        p.kind === 'all' ? icon('layers', { size: 13 }) : null, h('span', { class: 'crumb-text' }, p.title)),
      last && p.total ? progress(p.done, p.total) : null);
    };
    return h('nav', { class: 'opath', 'aria-label': 'Where you are' }, h('ol', null, ...parts.map(crumb)));
  }

  function toolbar() {
    const all = !ctx.state.group && !zoomId;
    const showPick = h('select', {
      'aria-label': 'Show',
      onchange: (e) => { show = e.target.value; write(SHOW_KEY, show); render(); },
    }, ...SHOW_CHOICES.map(([value, label]) => h('option', { value, selected: value === show }, label)));
    const levels = LEVELS.filter(([id]) => id !== 'groups' || all);
    return h('div', { class: 'view-toolbar' },
      h('button', { class: 'btn primary', type: 'button', onclick: startAdding }, icon('plus', { size: 16 }), 'New task'),
      h('label', { class: 'inline-select' }, 'Show', showPick),
      h('div', { class: 'open-to', role: 'group', 'aria-label': 'Open to' },
        h('span', { class: 'muted small', 'aria-hidden': 'true' }, 'Open to'),
        h('div', { class: 'seg' }, ...levels.map(([id, label]) => h('button', {
          type: 'button', title: `Open the tree to ${label.toLowerCase()}`,
          onclick: () => {
            const sets = openTo(roots, groups, options(), id);
            expanded = sets.expanded;
            closedGroups = sets.closedGroups;
            saveExpanded();
            saveClosed();
            render();
          },
        }, label)))),
      h('span', { class: 'muted small push-right kbd-hints' }, h('kbd', null, 'Enter'), ' rename · ', h('kbd', null, 'Tab'), ' nest · ', h('kbd', null, 'Alt ↑↓'), ' move · ', h('kbd', null, 'Space'), ' done · ', h('kbd', null, 'Alt Enter'), ' zoom · ', h('kbd', null, 'Esc'), ' up'));
  }

  function emptyText(zoomed) {
    if (ctx.state.query?.trim()) return h('p', { class: 'empty-state' }, 'Nothing here matches the filter.');
    if (zoomed) return h('p', { class: 'empty-state' }, EMPTY_HERE[show], ' Press ', h('kbd', null, 'Esc'), ' to go back up.');
    if (EMPTY[show]) return h('p', { class: 'empty-state' }, EMPTY[show]);
    return h('p', { class: 'empty-state' }, 'No tasks here yet. Press ', h('kbd', null, 'Insert'), ' or the button to add one.');
  }

  function render() {
    const after = focusNext;
    focusNext = null;
    const keep = after?.key ?? ctx.state.refocus ?? document.activeElement?.closest?.('#view-tasks .orow')?.dataset.key;
    // A redraw after a change puts focus back only if the keyboard hasn't moved on (to the toolbar, say).
    const focusHere = !!after || document.activeElement === document.body || !!root.querySelector('.outline, .opath')?.contains(document.activeElement);
    // Where that row was, for when it's no longer shown (finished, under To do).
    const keptAt = keep ? [...root.querySelectorAll(ROWS)].findIndex((r) => r.dataset.key === keep) : -1;

    // What's being renamed is saved first (the row may not be drawn again).
    if (renaming?.input.isConnected && !renaming.cancelled()) rename(renaming.node, renaming.input);
    renaming = null;

    // A zoom on a task that's really gone (deleted, archived, moved out of the group picked, without subtasks) is
    // given up for good: the address changes to what's left. A search or a Show choice only narrows for now (the
    // address stays, and so does the path above the tree).
    let opts = options();
    const lasting = rememberedZoom(roots, opts);
    if (lasting !== zoomId) {
      zoomId = lasting;
      history.replaceState(null, '', lasting ? `#/tasks/${encodeURIComponent(lasting)}` : '#/tasks');
      opts = options();
    }
    const zoom = resolveZoom(roots, opts);
    if (zoom !== zoomId) opts = { ...opts, zoomId: zoom };
    const parts = outlinePath(roots, groups, opts);
    if (parts.length) trail = parts.filter((p) => p.kind === 'task').slice(0, -1).map((p) => p.id);
    const rows = outlineRows(roots, groups, opts);
    drawn = new Map(rows.map((r) => [r.id, r]));
    if (keep && !after?.first) activeId = keep;
    if (after?.first || !rows.some((r) => r.id === activeId)) activeId = rows[0]?.id ?? null;

    const tree = h('div', { class: `outline${opts.zoomId ? ' zoomed' : ''}`, role: 'tree', 'aria-label': parts.length ? `Tasks under ${parts.at(-1).title}` : 'Tasks' });
    const editorAtTop = adding && ((!adding.afterId && !adding.parentId && !adding.groupId) || (adding.parentId && adding.parentId === opts.zoomId)
      || (adding.groupId && !rows.some((r) => r.kind === 'group' && r.id === adding.groupId)));
    if (editorAtTop) tree.append(newRowEditor(0, adding));
    for (const r of rows) {
      tree.append(r.kind === 'group' ? groupRow(r) : row(r));
      if (r.kind === 'group' && adding?.groupId === r.id && !editorAtTop) tree.append(newRowEditor(1, adding));
      if (r.kind === 'task' && adding?.parentId === r.id) tree.append(newRowEditor(r.depth + 1, adding));
      // After the task and everything shown under it.
      if (adding?.afterId === r.id) {
        const next = rows[rows.indexOf(r) + 1];
        if (!next || next.depth <= r.depth) tree.append(newRowEditor(r.depth, adding));
      }
    }

    // "After X" where X's subtasks are shown: the editor goes after the last of them.
    if (adding?.afterId && !tree.querySelector('.orow.adding')) {
      const at = rows.findIndex((r) => r.id === adding.afterId);
      if (at >= 0) {
        let end = at + 1;
        while (end < rows.length && rows[end].depth > rows[at].depth) end++;
        const anchor = tree.querySelectorAll(ROWS)[end - 1];
        anchor?.after(newRowEditor(rows[at].depth, adding));
      }
    }

    if (!rows.length && !adding) tree.append(emptyText(!!opts.zoomId));
    keepToolbarFocus(root, () => root.replaceChildren(...[toolbar(), parts.length ? pathBar(parts) : null, tree].filter(Boolean)));
    if (after?.first) {
      // Nothing to show under the zoom: the keyboard waits on the path (Escape goes back up from there).
      if (activeId) focusRow(activeId);
      else root.querySelector('.opath .crumb.current')?.focus();
    }
    else if (keep && focusHere && !root.querySelector('.orow.adding') && !focusRow(keep) && keptAt >= 0) {
      // Its row is gone: the keyboard goes to the row above it (like deleting a line), never off the page.
      const shownRows = root.querySelectorAll(ROWS);
      const next = shownRows[Math.max(0, Math.min(keptAt - 1, shownRows.length - 1))];
      if (next) focusRow(next.dataset.key);
    }

    applyReveal();
  }

  /**
   * Shows a task in the tree: its group and parents opened, the row in view, focused and briefly highlighted ("Show
   * in tree" from a task's details). Applied by the next drawing of the tree, whichever load it comes from.
   */
  let revealing = null;
  async function reveal(id, ancestorIds = [], { finished = false, groupId = null } = {}) {
    ancestorIds.forEach((a) => expanded.add(a));
    if (groupId) closedGroups.delete(groupId);
    saveExpanded();
    saveClosed();
    // A task the Show choice hides shows for now (not saved as the choice): finished ones under Everything.
    if (finished) show = 'all';
    else if (show === 'now' || show === 'waiting') show = 'todo';
    // Applied by the next drawing that shows the row (whichever load it comes from); given up after two drawings
    // without it, so it never jumps there later out of the blue.
    revealing = { id, misses: 0 };
    await load();
    // Still not in the tree with a search on: the search leaves it out, so it goes (and says so).
    if (revealing?.id === id && !map.has(id) && ctx.state.query?.trim()) {
      revealing.misses = 0;
      ctx.setQuery('');
      ctx.toast('Cleared the search to show the task');
      await load();
    }
  }

  function applyReveal() {
    // Drawn while the view isn't shown yet (the switch to it comes a moment later): that doesn't count as a miss.
    if (!revealing || !root.offsetParent) return;
    if (!focusRow(revealing.id)) {
      if (++revealing.misses >= 2) revealing = null;
      return;
    }
    const el = rowEl(revealing.id);
    revealing = null;
    el.scrollIntoView({ block: 'center' });
    el.classList.add('revealed');
    setTimeout(() => el.classList.remove('revealed'), 2400);
  }

  return {
    root, show: () => load(), refresh: () => load(), poll: () => load({ quiet: true }), title: 'Tasks', reveal, setZoom,
    /** The zoom on screen (null: none), for "Show in tree" to leave it when it doesn't hold the task. */
    zoom: () => zoomId,
  };
}
