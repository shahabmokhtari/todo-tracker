import { api, post, patch, put, del, h, upload, text, ApiError, setKeepalive } from './api.js';
import { relativeTime, priorityMeta, snoozeOptions, progressPercent, stepLabel, isSafeHttpUrl, greeting, metaChips, summaryLine, pomodoroFraction, fileSize, parseTags, formatTags, queryFor, hasTerm, toggleTerm, obsidianLink, whenText } from './format.js';
import { icon, ring } from './icons.js';
import { createAutosave, changedFields } from './autosave.js';
import { step, drop, beforeOf, changed } from './order.js';
import { splitEmbeds, embedUrl, acceptPastedMedia, pastesDone } from './media.js';
import { createShell } from './shell.js';
import { createLater } from './later.js';
import { createBoardView } from './views/board.js';
import { createOutlineView } from './views/outline.js';
import { createDoneView } from './views/done.js';
import { createReportsView } from './views/reports.js';
import { createSettingsView } from './views/settings.js';
import { followTheme as follow } from './themes.js';
import { duration, toLocalInput as localInput } from './timefmt.js';

/** One embedded file: a picture (its name if it can't be shown), or a link for other files. */
function embedNode(itemId, name, image) {
  const url = embedUrl(itemId, name);
  if (!image) return h('a', { class: 'embed-file', href: url, target: '_blank', rel: 'noopener' }, icon('paperclip', { size: 13 }), name);
  const img = h('img', { src: url, alt: name, loading: 'lazy' });
  const link = h('a', { class: 'embed', href: url, target: '_blank', rel: 'noopener', title: name }, img);
  img.addEventListener('error', () => link.replaceWith(h('span', { class: 'embed-missing', title: 'Not found among this task’s attachments' }, icon('paperclip', { size: 13 }), name)), { once: true });
  return link;
}

/** Text with its embedded files (![[Pasted image.png]]) shown as pictures or links; the rest stays plain text. */
function withEmbeds(value, itemId) {
  return splitEmbeds(value).map((part) => (part.embed ? embedNode(itemId, part.embed, part.image) : part.text));
}

/** Pasting a screenshot (or any file) into this box attaches it to the task and embeds it at the caret. */
function acceptMedia(box, itemId, onUploaded) {
  acceptPastedMedia(box, {
    upload: (file, name) => upload(`/api/items/${itemId}/attachments`, file, name),
    onError: (err) => toast(err.message, 'error'),
    onUploaded,
  });
  return box;
}

const $ = (sel) => document.querySelector(sel);
const state = {
  dashboard: null,
  group: localStorage.getItem('tt.group') || null,
  drawerId: null,
  query: '',
  vault: null,
  plugins: null, // ids of the plugins that are on (null until loaded: everything shows)
};

/** Notes autosave while typed: the first pause creates the note, later pauses update the same note. */
const noteSessions = new Map();

function noteSession(itemId, key = itemId) {
  if (!noteSessions.has(key)) {
    const session = { noteId: null };
    session.autosave = createAutosave({
      delay: 1200,
      save: async (text) => {
        if (!text.trim()) return;
        if (session.noteId) await patch(`/api/items/${itemId}/notes/${session.noteId}`, { text });
        else session.noteId = (await post(`/api/items/${itemId}/notes`, { text })).id;
      },
      onState: (s) => setSaveState(s),
    });
    noteSessions.set(key, session);
  }
  return noteSessions.get(key);
}

/**
 * Finishes the note being typed (saves it now) so the next keystrokes start a new note. Returns false when it couldn't
 * be saved: the session (and the text in the box) are kept so nothing typed is lost and the next try continues it.
 */
async function finishNote(key) {
  const session = noteSessions.get(key);
  if (!session) return true;
  if (!(await session.autosave.flush())) return false;
  if (noteSessions.get(key) === session) noteSessions.delete(key);
  return true;
}

function setSaveState(s) {
  const el = document.getElementById('save-state');
  if (!el) return;
  el.dataset.state = s;
  el.textContent = { pending: 'Editing…', saving: 'Saving…', saved: 'Saved', error: 'Couldn’t save – will retry' }[s] ?? '';
}

// ---------- boot & refresh ----------

async function refresh({ background = false } = {}) {
  // Background refreshes never interrupt typing: an open note box or a focused field keeps its content.
  if (background && isEditing()) return;
  try {
    const params = new URLSearchParams();
    if (state.group) params.set('group', state.group);
    if (state.query) params.set('q', state.query);
    const query = params.toString() ? `?${params}` : '';
    state.dashboard = await api(`/api/dashboard${query}`);
    if (!state.vault) api('/api/vault').then((v) => { state.vault = v; renderVault(); }).catch(() => {});
    showBoard(true);
    await dragDone();
    const drafts = collectNoteDrafts();
    render();
    restoreNoteDrafts(drafts);
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) return showBoard(false);
    if (err instanceof ApiError && err.status === 404 && state.group) {
      setGroup(null);
      return refresh();
    }
    toast(err.message, 'error');
  }
}

/** True only when something unsaved would be lost: typed capture text, a non-empty note, or dirty drawer fields. */
function isEditing() {
  if ($('#capture-input').value.trim()) return true;
  if ([...noteSessions.values()].some((s) => s.autosave.dirty)) return true;
  if ([...document.querySelectorAll('.inline-note:not([hidden]) input')].some((i) => i.value.trim())) return true;
  return !$('#drawer').hidden && !!state.drawerAutosave?.dirty;
}

function collectNoteDrafts() {
  const drafts = new Map();
  const active = document.activeElement;
  document.querySelectorAll('.inline-note:not([hidden])').forEach((form) => {
    const input = form.querySelector('input');
    drafts.set(form.dataset.id, { value: input.value, focused: input === active, start: input.selectionStart, end: input.selectionEnd });
  });
  return drafts;
}

function restoreNoteDrafts(drafts) {
  drafts.forEach((draft, id) => {
    const form = document.querySelector(`.inline-note[data-id="${CSS.escape(id)}"]`);
    if (!form) return;
    const { value, focused = false, start = value.length, end = value.length } = typeof draft === 'string' ? { value: draft } : draft;
    const input = form.querySelector('input');
    form.hidden = false;
    input.value = value;
    if (focused) {
      // Keep the caret where it was, so the next keystroke doesn't trigger page shortcuts.
      input.focus();
      input.setSelectionRange(start, end);
    }
  });
}

/** Signed in: the views; signed out: the sign-in form (only redrawn when that changes). */
function showBoard(authenticated) {
  if (state.signedIn === authenticated) return;
  state.signedIn = authenticated;
  $('#login').hidden = authenticated;
  document.body.classList.toggle('signed-out', !authenticated);
  shell.route();
}

/** The dashboard (top bar, Today) and the view on screen, after a change. */
async function refreshAll() {
  await refresh();
  await currentView()?.refresh();
}

const currentView = () => ctx.views[shell.current()];

/** Runs an action, then refreshes. Returns true on success so callers only clear inputs when nothing was lost. */
async function act(promise, message) {
  let ok = true;
  try {
    await promise;
    if (message) toast(message);
  } catch (err) {
    ok = false;
    toast(err.message, 'error');
  }
  await refresh();
  await currentView()?.refresh();
  if (state.drawerId) await openDrawer(state.drawerId);
  return ok;
}

function toast(message, tone = 'ok') {
  const el = $('#toast');
  el.className = `toast ${tone}`;
  el.replaceChildren(icon(tone === 'error' ? 'x' : 'check', { size: 16 }), h('span', null, message));
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (el.hidden = true), 3200);
}

function setGroup(id) {
  state.group = id;
  if (id) localStorage.setItem('tt.group', id);
  else localStorage.removeItem('tt.group');
}

// ---------- rendering ----------

function render() {
  const d = state.dashboard;
  // Any redraw (a live update, a plugin loading) keeps the keyboard where it was on the board.
  const kept = state.refocus ? { id: state.refocus } : boardFocus();
  $('#greeting').textContent = greeting();
  $('#summary').textContent = summaryLine(d);
  renderTabs(d);
  renderFocus(d);
  renderSection('#sec-now', d.now.filter((c) => c.id !== d.focus?.id).map((c) => card(c, { orderable: true })), d.now.length, 'Nothing else right now. Nice.');
  renderSection('#sec-waiting', d.waiting.map((c) => card(c, { waiting: true })), d.waiting.length, 'Nothing is parked.');
  // Workstreams are tasks with subtasks; single tasks already live in Do now / Waiting.
  const streams = d.overview.filter((o) => o.hasChildren);
  renderSection('#sec-overview', streams.map(workstream), streams.length, 'Tasks with subtasks or rollout steps show up here with progress.');
  renderSection('#sec-notes', d.recentNotes.map(noteRow), d.recentNotes.length, 'Notes you and your agents log appear here.');
  renderPomodoro(d.pomodoro);
  renderProblems(d.problems ?? []);
  renderFilter();
  shell.update(d);
  document.title = d.now.length ? `(${d.now.length}) Todo Tracker` : 'Todo Tracker';
  if (kept) focusOrderControl(kept.id, kept.label);
}

/** The board task row (and which of its controls) the keyboard is on; nothing outside the board counts. */
function boardFocus() {
  const active = document.activeElement;
  const row = active && $('#board').contains(active) ? active.closest('[data-order-id]') : null;
  return row ? { id: row.dataset.orderId, label: active.getAttribute('aria-label') } : null;
}

/** Files that can't be read are shown, never silently ignored (the app won't overwrite them). */
function renderProblems(problems) {
  const el = $('#problems');
  el.hidden = problems.length === 0;
  if (!problems.length) return;
  el.replaceChildren(icon('alert', { size: 16 }),
    h('span', null, problems.length === 1 ? `A task file needs fixing: ${problems[0].path} – ${problems[0].message}` : `${problems.length} task files need fixing (first: ${problems[0].path}).`));
}

function renderFilter() {
  const input = $('#filter');
  if (document.activeElement !== input) input.value = state.query;
  $('#filter-clear').hidden = !state.query;
}

function setQuery(q) {
  state.query = (q ?? '').trim();
  renderFilter();
  // Every view follows the filter: Today, Board, Tasks and Done.
  refreshAll();
}

/**
 * The tag & label picker next to the filter: a small panel of toggles (click one to add it to the filter, again to
 * take it out). Esc closes it and goes back to its button.
 */
async function toggleFilterMenu() {
  const menu = $('#filter-menu');
  if (!menu.hidden) return closeFilterMenu();
  closeMenus();
  let tags = [];
  try { tags = await api('/api/tags'); } catch { /* labels alone still help */ }
  renderFilterMenu(tags);
  menu.hidden = false;
  $('#filter-pick').setAttribute('aria-expanded', 'true');
  menu.querySelector('button')?.focus();
}

function closeFilterMenu({ focusButton = false } = {}) {
  $('#filter-menu').hidden = true;
  $('#filter-pick').setAttribute('aria-expanded', 'false');
  if (focusButton) $('#filter-pick').focus();
}

function renderFilterMenu(tags) {
  const menu = $('#filter-menu');
  const labels = state.dashboard?.labels ?? [];
  // Redrawn after each click, so what's on shows at once (and focus stays on the same toggle).
  const pick = (term) => {
    setQuery(toggleTerm(state.query, term));
    renderFilterMenu(tags);
    menu.querySelector(`button[data-term="${CSS.escape(term)}"]`)?.focus();
  };
  const entry = (term, content, title) => {
    const on = hasTerm(state.query, term);
    return h('button', { type: 'button', 'aria-pressed': String(on), class: on ? 'on' : '', title, dataset: { term }, onclick: () => pick(term) },
      h('span', { class: 'check', 'aria-hidden': 'true' }, on ? '✓' : ''), ...content);
  };
  const swatch = (color) => {
    const s = h('span', { class: 'swatch', 'aria-hidden': 'true' });
    s.style.setProperty('--label', color);
    return s;
  };
  menu.replaceChildren(
    h('h2', { class: 'menu-head' }, 'Labels', h('small', null, 'colored categories, picked from a list')),
    ...(labels.length ? labels.map((l) => entry(queryFor({ label: l.name }), [swatch(l.color), l.name], `Show tasks labeled "${l.name}"`)) : [h('p', { class: 'menu-empty' }, 'No labels yet: add them in a task\'s details.')]),
    h('h2', { class: 'menu-head' }, 'Tags', h('small', null, 'free words you type in a task, separated by commas')),
    ...(tags.length ? tags.map((t) => entry(queryFor({ tag: t.name }), [h('span', null, `#${t.name}`), h('span', { class: 'count' }, String(t.count))], `Show #${t.name}`)) : [h('p', { class: 'menu-empty' }, 'No tags yet: add them in a task\'s details, separated by commas.')]),
    state.query ? h('button', { type: 'button', class: 'menu-clear', onclick: () => { setQuery(''); renderFilterMenu(tags); } }, 'Clear the filter') : null);
}

/** "Open in Obsidian" when it's on this computer; else a link to get it (its site, or its app on a phone). */
function obsidianAnchor(url, { class: cls, iconOnly = false, file = '' } = {}) {
  const link = obsidianLink({ url, installed: state.vault?.obsidianInstalled ?? true, userAgent: navigator.userAgent, touchPoints: navigator.maxTouchPoints });
  const title = link.get ? 'Get Obsidian (to open your tasks there)' : file ? `Open in Obsidian (${file})` : 'Open in Obsidian';
  const attrs = { class: cls, href: link.href, title, 'aria-label': link.label, ...(link.get ? { target: '_blank', rel: 'noopener' } : {}) };
  return h('a', attrs, iconOnly ? icon('obsidian') : link.label);
}

function renderVault() {
  const v = state.vault;
  if (!v) return;
  $('#vault').replaceChildren(
    icon('folder', { size: 14 }),
    h('span', { class: 'muted', title: v.path }, 'Saved as markdown in ', h('code', null, v.path)),
      pluginOn('obsidian') ? obsidianAnchor(v.obsidianUrl, { class: 'link' }) : null);
}

const tagChip = (t) => h('button', { class: 'chip tag', title: `Show #${t}`, onclick: (e) => { e.stopPropagation(); setQuery(queryFor({ tag: t })); } }, `#${t}`);

function labelChip(l, { onclick } = {}) {
  const el = h('button', { class: 'chip label', title: `Show "${l.name}"`, onclick: onclick ?? ((e) => { e.stopPropagation(); setQuery(queryFor({ label: l.name })); }) }, h('span', { class: 'swatch', 'aria-hidden': 'true' }), l.name);
  el.style.setProperty('--label', l.color);
  return el;
}

function renderTabs(d) {
  const total = d.groups.reduce((n, g) => n + g.now, 0);
  const attention = d.groups.some((g) => g.attention > 0);
  const tab = (id, name, count, color, alert) => {
    const el = h('button', {
      role: 'tab',
      class: 'tab',
      'aria-selected': String((state.group || null) === id),
      onclick: () => { setGroup(id); shell.closeNav(); refreshAll(); },
      ondblclick: id ? () => editGroup(d.groups.find((g) => g.id === id)) : undefined,
      title: id ? 'Double-click to rename or delete' : 'All groups',
    }, id ? h('span', { class: 'dot' }) : null, name, count ? h('span', { class: `badge${alert ? ' alert' : ''}` }, count) : null);
    if (color) el.style.setProperty('--group', color);
    return el;
  };

  $('#tabs').replaceChildren(
    tab(null, 'All', total, null, attention),
    ...d.groups.map((g) => tab(g.id, g.name, g.now, g.color, g.attention > 0)),
    h('button', { class: 'tab add', title: 'Add group', 'aria-label': 'Add group', onclick: addGroup }, icon('plus', { size: 16 })));
}

function renderFocus(d) {
  const f = d.focus;
  if (!f) {
    const next = d.waiting[0];
    $('#focus').replaceChildren(h('div', { class: 'focus-card empty' },
      h('div', { class: 'focus-top' }, h('span', { class: 'eyebrow' }, icon('sparkles', { size: 14 }), 'All clear')),
      h('div', { class: 'empty-row' },
        h('div', { class: 'empty-illustration' }, icon('check', { size: 28 })),
        h('div', null,
          h('div', { class: 'focus-title' }, 'Nothing is due. Enjoy the calm.'),
          next ? h('div', { class: 'muted' }, `Next up: ${next.title} ${relativeTime(next.wakeAt)}`) : h('div', { class: 'muted' }, 'Capture the next thing whenever it comes to mind.')))));
    return;
  }

  const meta = priorityMeta(f.priority);
  const chips = metaChips(f).filter((c) => c.tone !== 'step');
  const sub = [];
  if (f.stepNumber) {
    sub.push(h('span', { class: 'step-progress' }, ring((f.stepNumber - 1) / f.stepCount, { size: 22, stroke: 3 }), stepLabel(f)));
  }
  const tagged = [...(f.labels ?? []).map((l) => labelChip(l)), ...(f.tags ?? []).map(tagChip)];
  if (chips.length || tagged.length) sub.push(h('div', { class: 'meta' }, ...chips.map(chip), ...tagged));

  const el = h('div', { class: `focus-card${f.needsAttention ? ' attention' : ''}` },
    h('div', { class: 'focus-top' },
      h('span', { class: 'eyebrow' }, icon(f.needsAttention ? 'clock' : 'target', { size: 14 }), f.needsAttention ? 'Reminder' : 'Do this now'),
      h('span', { class: 'prio-pill' }, meta.label)),
    h('button', { class: 'focus-title', title: 'Open details (subtasks, notes, time, files)', onclick: () => openDrawer(f.id) }, f.title),
    sub.length ? h('div', { class: 'focus-sub' }, ...sub) : null,
    f.needsAttention && f.reminderMessage ? h('div', { class: 'reminder-banner' }, icon('clock', { size: 16 }), f.reminderMessage) : null,
    f.lastNote ? h('blockquote', { class: 'last-note' }, f.lastNote) : null,
    actions(f, { big: true }));
  el.style.setProperty('--prio', meta.color);
  // Drag the focus card down to do something else first, or drop a task on it to make that the focus.
  sortable(el, f.id, { scope: 'now', ids: () => blockIds(f.id), onOrder: arrangeNow, alwaysBefore: true });
  if (blockIds(f.id).length > 1) {
    el.querySelector('.focus-top').append(h('button', {
      class: 'icon-btn order-btn', type: 'button', 'aria-label': 'Do something else first', title: 'Do something else first (Alt+↓)',
      onclick: () => arrangeNow(step(blockIds(f.id), f.id, 1), f.id),
    }, icon('down', { size: 16 })));
  }
  $('#focus').replaceChildren(el);
}

// ---------- manual order (drag, arrows, Alt+↑/↓) ----------

const nowIds = () => state.dashboard?.now.map((c) => c.id) ?? [];

/**
 * Tasks with a due reminder always come first, so moves stay within a block: the reminder block or the rest.
 * (Moving a task above a reminder would just snap back.)
 */
function blockIds(id) {
  const now = state.dashboard?.now ?? [];
  const attention = now.find((c) => c.id === id)?.needsAttention ?? false;
  return now.filter((c) => !!c.needsAttention === attention).map((c) => c.id);
}

/** Shows the new order of one block right away, then saves the whole Do now order (one save at a time). */
function arrangeNow(blockOrder, movedId) {
  const d = state.dashboard;
  if (!d || !blockOrder.length) return;
  const attention = d.now.find((c) => c.id === blockOrder[0])?.needsAttention ?? false;
  const other = d.now.filter((c) => !!c.needsAttention !== attention).map((c) => c.id);
  const ids = attention ? [...blockOrder, ...other] : [...other, ...blockOrder];
  if (!changed(nowIds(), ids)) return;
  const byId = new Map(d.now.map((c) => [c.id, c]));
  d.now = ids.map((id) => byId.get(id)).filter(Boolean);
  d.focus = d.now[0] ?? null;
  state.refocus = movedId;
  const drafts = collectNoteDrafts();
  render();
  restoreNoteDrafts(drafts);
  saveOrder(ids);
}

let orderSaving = Promise.resolve();
let pendingOrder = null;

/** Saves the latest order; quick moves never race (the newest order is always the one saved last). */
function saveOrder(ids) {
  pendingOrder = ids;
  orderSaving = orderSaving.then(async () => {
    if (!pendingOrder) return;
    const send = pendingOrder;
    pendingOrder = null;
    try {
      await post('/api/now/order', { ids: send });
    } catch (err) {
      toast(err.message, 'error');
    }
    if (!pendingOrder) {
      await refresh({ background: true });
      state.refocus = null;
    }
  });
}

/** Keeps the keyboard on the moved task (on the same control, if given) after a redraw of `scope`. */
function focusOrderControl(id, label = null, scope = $('#board')) {
  if (!id) return;
  const el = scope.querySelector(`[data-order-id="${CSS.escape(id)}"]`);
  const same = label ? [...(el?.querySelectorAll('[aria-label]') ?? [])].find((c) => c.getAttribute('aria-label') === label) : null;
  (same ?? el?.querySelector('.title, .focus-title, .link') ?? el)?.focus({ preventScroll: true });
}

let dragging = null; // { id, scope }
const DRAG_TYPE = 'application/x-todo-tracker-task';
let dragWaiters = [];

/** Resolves once no drag is in progress (at most 10 s): redrawing under a drag would lose the drop. */
function dragDone() {
  return dragging
    ? new Promise((resolve) => {
      dragWaiters.push(resolve);
      setTimeout(resolve, 10000);
    })
    : Promise.resolve();
}

function endDrag() {
  dragging = null;
  const waiters = dragWaiters;
  dragWaiters = [];
  waiters.forEach((resolve) => resolve());
}

// A drag also ends when its row was replaced meanwhile (the row's own dragend may not come).
document.addEventListener('dragend', endDrag, true);

/**
 * Makes `el` reorderable within its `scope`: drag it (or drop others on it), ↑/↓ buttons, and Alt+↑/Alt+↓.
 * `ids()` is the current order; `onOrder(newIds, movedId)` applies a new one.
 */
function sortable(el, id, { scope, ids, onOrder, buttons = false, alwaysBefore = false }) {
  el.dataset.orderId = id;
  el.draggable = true;
  el.classList.add('sortable');
  const clear = () => el.classList.remove('drop-before', 'drop-after');
  el.addEventListener('dragstart', (e) => {
    if (e.target !== el) return;
    dragging = { id, scope };
    e.dataTransfer.effectAllowed = 'move';
    // A private type: dropping a card on a text box must not paste its id.
    e.dataTransfer.setData(DRAG_TYPE, id);
    el.classList.add('dragging');
  });
  el.addEventListener('dragend', () => {
    el.classList.remove('dragging');
    document.querySelectorAll('.drop-before, .drop-after').forEach((x) => x.classList.remove('drop-before', 'drop-after'));
    endDrag();
  });
  const after = (e) => {
    if (alwaysBefore) return false;
    const r = el.getBoundingClientRect();
    return e.clientY > r.top + r.height / 2;
  };
  const accepts = () => dragging && dragging.scope === scope && dragging.id !== id && ids().includes(dragging.id);
  el.addEventListener('dragover', (e) => {
    if (!accepts()) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'move';
    clear();
    el.classList.add(after(e) ? 'drop-after' : 'drop-before');
  });
  el.addEventListener('dragleave', (e) => { if (!el.contains(e.relatedTarget)) clear(); });
  el.addEventListener('drop', (e) => {
    if (!accepts()) return;
    e.preventDefault();
    e.stopPropagation();
    clear();
    const moved = dragging.id;
    onOrder(drop(ids(), moved, id, after(e)), moved);
  });
  el.addEventListener('keydown', (e) => {
    if (!e.altKey || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) return;
    // Typing in a note keeps Alt/Option+arrows for the text.
    if (e.target.closest?.('input, textarea, select, [contenteditable="true"]')) return;
    e.preventDefault();
    e.stopPropagation();
    onOrder(step(ids(), id, e.key === 'ArrowUp' ? -1 : 1), id);
  });
  if (!buttons) return null;
  const current = ids();
  const index = current.indexOf(id);
  const arrow = (delta, iconName, name) => h('button', {
    class: 'icon-btn order-btn', type: 'button', 'aria-label': name, title: `${name} (Alt+${delta < 0 ? '↑' : '↓'})`, disabled: index + delta < 0 || index + delta >= current.length,
    onclick: (e) => { e.stopPropagation(); onOrder(step(ids(), id, delta), id); },
  }, icon(iconName, { size: 14 }));
  return h('span', { class: 'order' },
    h('span', { class: 'grip', title: 'Drag to reorder', 'aria-hidden': 'true' }, icon('grip', { size: 14 })),
    arrow(-1, 'up', 'Move up'),
    arrow(1, 'down', 'Move down'));
}

function renderSection(selector, rows, count, emptyText) {
  const section = $(selector);
  section.querySelector('.count').textContent = count ? String(count) : '';
  section.querySelector('.list').replaceChildren(...(rows.length ? rows : [h('li', { class: 'empty' }, emptyText)]));
  const key = `tt.open.${section.id}`;
  if (!section.dataset.bound) {
    section.dataset.bound = '1';
    const saved = localStorage.getItem(key);
    if (saved !== null) section.open = saved === '1';
    section.addEventListener('toggle', () => localStorage.setItem(key, section.open ? '1' : '0'));
  }
}

const chip = (c) => h('span', { class: `chip ${c.tone}` }, c.text);

function card(c, { waiting = false, orderable = false } = {}) {
  const li = h('li', { class: `item${c.needsAttention ? ' attention' : ''}` },
    h('span', { class: 'prio', title: `${priorityMeta(c.priority).label} priority` }),
    h('div', { class: 'body' },
      h('button', { class: 'title link', title: 'Open details (subtasks, notes, time, files)', onclick: () => openDrawer(c.id) }, c.title),
      h('div', { class: 'meta' }, ...metaChips(c, { waiting }).map(chip), ...(c.labels ?? []).map((l) => labelChip(l)), ...(c.tags ?? []).map(tagChip),
        c.attachmentCount ? h('span', { class: 'chip muted', title: 'Attachments' }, icon('paperclip', { size: 12 }), String(c.attachmentCount)) : null),
      c.needsAttention && c.reminderMessage ? h('div', { class: 'reminder' }, icon('clock', { size: 14 }), c.reminderMessage) : null),
    actions(c, { waiting }));
  li.style.setProperty('--prio', priorityMeta(c.priority).color);
  if (orderable) {
    const controls = sortable(li, c.id, { scope: 'now', ids: () => blockIds(c.id), onOrder: arrangeNow, buttons: true });
    li.insertBefore(controls, li.lastElementChild); // before the actions
  }
  return li;
}

/** Icon button for rows; labeled button for the focus card. Both expose the same accessible name. */
function actionButton({ name, iconName, big, tone = '', primary = false, onclick, title = name }) {
  return big
    ? h('button', { class: `btn ${primary ? 'primary' : ''}`.trim(), title, onclick }, icon(iconName, { size: 16 }), name)
    : h('button', { class: `icon-btn ${tone}`.trim(), title, 'aria-label': name, onclick }, icon(iconName));
}

function actions(c, { waiting = false, big = false } = {}) {
  const bar = h('div', { class: `actions${big ? ' big' : ''}` });
  // Saved as you type; Enter finishes the note (and starts a fresh one next time).
  const noteInput = h('input', {
    placeholder: 'What did you do? What is next? (saves as you type)', maxlength: 10000, 'aria-label': 'Note',
    oninput: () => noteSession(c.id, `card:${c.id}`).autosave.schedule(noteInput.value),
    onkeydown: (e) => { if (e.key === 'Escape') { e.stopPropagation(); finish(); } },
    // Leaving the box finishes the note, so coming back starts a new one instead of overwriting it.
    onblur: () => { if (noteInput.value.trim()) finish(); },
  });
  acceptMedia(noteInput, c.id);
  let finishing = false;
  const finish = async () => {
    if (finishing) return;
    finishing = true;
    try {
      // A picture still uploading goes into this note, not the next one.
      await pastesDone();
      const hadText = noteInput.value.trim();
      if (!(await finishNote(`card:${c.id}`))) return toast('Couldn’t save the note yet – it’s kept here, try again', 'error');
      noteInput.value = '';
      noteBox.hidden = true;
      if (hadText) await act(Promise.resolve(), 'Note saved');
    } finally {
      finishing = false;
    }
  };
  const noteBox = h('form', { class: 'inline-note', hidden: true, dataset: { id: c.id }, onsubmit: (e) => { e.preventDefault(); finish(); } },
    noteInput, h('button', { class: 'btn', type: 'submit', title: 'Finish this note (Enter)', 'aria-label': 'Finish note' }, icon('check', { size: 16 }), 'Finish'));

  if (!waiting && !c.hasChildren) bar.append(actionButton({ name: 'Done', iconName: 'check', big, tone: 'ok', primary: true, onclick: () => act(post(`/api/items/${c.id}/complete`), `Done: ${c.title}`) }));
  if (c.needsAttention && c.reminderId) bar.append(actionButton({ name: 'Dismiss', iconName: 'bellOff', big, title: 'Dismiss reminder', onclick: () => act(post(`/api/items/${c.id}/reminders/${c.reminderId}/dismiss`)) }));
  if (waiting) bar.append(actionButton({ name: 'Do now', iconName: 'undo', big, title: 'Bring back now', onclick: () => act(post(`/api/items/${c.id}/schedule`, { clear: true })) }));
  bar.append(snoozeButton(c, big));
  bar.append(actionButton({ name: 'Note', iconName: 'pencil', big, title: 'Add note', onclick: () => { noteBox.hidden = !noteBox.hidden; noteBox.querySelector('input').focus(); } }));
  if (!waiting) bar.append(actionButton({ name: 'Focus', iconName: 'target', big, tone: 'warn', title: 'Start a focus session (Pomodoro)', onclick: () => act(post('/api/pomodoro/start', { itemId: c.id }), 'Focus started') }));
  if (!waiting) {
    bar.append(c.timing
      ? actionButton({ name: 'Stop timer', iconName: 'stop', big, tone: 'on', title: 'Stop timing this task', onclick: () => act(post('/api/timer/stop'), 'Timer stopped') })
      : actionButton({ name: 'Timer', iconName: 'timer', big, title: 'Time this task (stops any other timer)', onclick: () => act(post('/api/timer/start', { itemId: c.id }), 'Timer started') }));
  }
  // Said out loud: the title opens it too, but nothing else told you so.
  bar.append(actionButton({ name: big ? 'Details' : 'Open', iconName: 'expand', big, title: 'Open details: subtasks, notes, time, files', onclick: () => openDrawer(c.id) }));
  return h('div', { class: 'action-wrap' }, bar, noteBox);
}

/** The quick choices come from the server (its clock and zone decide "this evening", "next Monday"…); kept a minute. */
let snoozeChoices = { at: 0, list: null };
async function quickChoices() {
  if (snoozeChoices.list && Date.now() - snoozeChoices.at < 60000) return snoozeChoices.list;
  try {
    const answer = await Promise.race([api('/api/snooze'), new Promise((_, reject) => setTimeout(() => reject(new Error('slow')), 1500))]);
    snoozeChoices = { at: Date.now(), list: answer.choices };
    return answer.choices;
  } catch {
    return snoozeOptions().map((o) => ({ label: o.label, minutes: o.minutes }));
  }
}

function snoozeButton(c, big) {
  const menu = h('div', { class: 'menu snooze-menu', hidden: true, role: 'menu', 'aria-label': `Snooze ${c.title}` });
  const fill = (choices) => {
    const now = new Date();
    menu.replaceChildren(
      ...choices.map((o) => h('button', {
        role: 'menuitem', class: 'choice',
        onclick: () => act(post(`/api/items/${c.id}/schedule`, o.id ? { choice: o.id, notify: true } : { inMinutes: o.minutes, notify: true }),
          o.at ? `Snoozed until ${whenText(o.at, now)}` : `Snoozed: ${o.label.toLowerCase()}`),
      }, icon('clock', { size: 15 }), h('span', { class: 'menu-label' }, o.label), o.at ? h('span', { class: 'menu-hint' }, whenText(o.at, now)) : null)),
      h('div', { class: 'menu-sep', role: 'separator' }),
      h('button', { role: 'menuitem', class: 'wide', onclick: () => later.openWhen(c) }, icon('calendar', { size: 15 }), h('span', { class: 'menu-label' }, 'Pick a time…')),
      h('button', { role: 'menuitem', class: 'wide', onclick: () => later.openAfter(c) }, icon('link', { size: 15 }), h('span', { class: 'menu-label' }, 'After another task…')));
  };
  const button = actionButton({
    name: 'Later', iconName: 'clock', big, title: 'Snooze: later today, next week, a time you type, or after another task',
    onclick: async (e) => {
      e.stopPropagation();
      const opening = menu.hidden;
      closeMenus();
      if (!opening) return;
      fill(await quickChoices());
      menu.hidden = false;
      fitMenu(menu);
      button.setAttribute('aria-expanded', 'true');
      menu.querySelector('button')?.focus();
    },
  });
  button.setAttribute('aria-haspopup', 'menu');
  menu.addEventListener('keydown', (e) => {
    const items = [...menu.querySelectorAll('button')];
    const i = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1) % items.length]?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length]?.focus(); }
    else if (e.key === 'Escape') { e.stopPropagation(); closeMenus(); button.focus(); }
  });
  return h('span', { class: 'menu-wrap' }, button, menu);
}

/** A menu that doesn't fit below its button opens above it, or (no room either way) scrolls. */
function fitMenu(menu) {
  menu.classList.remove('up', 'start');
  menu.style.maxHeight = '';
  const margin = 8;
  // Clipped on the left (right-aligned under a button near the left edge of a panel): align it to the button's left instead.
  const clip = menu.closest('.drawer, .palette-box')?.getBoundingClientRect();
  if (menu.getBoundingClientRect().left < Math.max(margin, clip ? clip.left + margin : 0)) menu.classList.add('start');
  const rect = menu.getBoundingClientRect();
  // Inside a panel that scrolls (task details), scroll it so the whole menu shows.
  if (clip) { menu.scrollIntoView({ block: 'nearest' }); return; }
  const top = margin;
  const bottom = innerHeight - margin;
  if (rect.bottom <= bottom) return;
  const button = menu.parentElement.getBoundingClientRect();
  const above = button.top - top - 6;
  const below = bottom - rect.top;
  if (above >= rect.height || above > below) {
    menu.classList.add('up');
    if (above < rect.height) menu.style.maxHeight = `${Math.max(120, above)}px`;
  } else {
    menu.style.maxHeight = `${Math.max(120, below)}px`;
  }
}

function closeMenus() {
  document.querySelectorAll('.menu').forEach((m) => (m.hidden = true));
  document.querySelectorAll('[aria-haspopup="menu"][aria-expanded="true"]').forEach((b) => b.setAttribute('aria-expanded', 'false'));
  $('#filter-pick')?.setAttribute('aria-expanded', 'false');
}
document.addEventListener('click', closeMenus);

function workstream(o) {
  const pct = progressPercent(o.doneLeaves, o.totalLeaves);
  const bar = h('div', { class: 'progress', role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-label': `${pct}% done` }, h('span'));
  bar.firstChild.style.width = `${pct}%`;
  const chips = [{ text: `${o.doneLeaves}/${o.totalLeaves} done`, tone: 'muted' }];
  if (o.actionableCount) chips.push({ text: `${o.actionableCount} now`, tone: 'step' });
  if (o.waitingCount) chips.push({ text: `${o.waitingCount} waiting`, tone: 'info' });
  if (o.nextWakeAt) chips.push({ text: `next ${relativeTime(o.nextWakeAt)}`, tone: 'muted' });
  const li = h('li', { class: 'item' },
    h('span', { class: 'prio' }),
    h('div', { class: 'body' },
      h('button', { class: 'title link', onclick: () => openDrawer(o.id) }, o.title),
      bar,
      h('div', { class: 'meta' }, ...chips.map(chip))),
    h('a', { class: 'report-link', href: `report.html?id=${o.id}`, title: 'Full report', 'aria-label': `Full report for ${o.title}` }, icon('report')));
  li.style.setProperty('--prio', priorityMeta(o.priority).color);
  return li;
}

function avatar(kind, author) {
  const letter = kind === 'user' ? 'Y' : (author.split(':').pop().trim()[0] || '?').toUpperCase();
  return h('span', { class: `avatar ${kind}`, 'aria-hidden': 'true' }, letter);
}

function noteRow(n) {
  return h('li', { class: 'note' },
    h('div', { class: 'note-text' }, ...withEmbeds(n.text, n.itemId)),
    h('div', { class: 'meta' },
      h('span', { class: 'author' }, avatar(n.authorKind, n.author), n.author),
      h('span', null, '·'),
      h('button', { class: 'link', onclick: () => openDrawer(n.itemId) }, n.itemTitle),
      h('span', null, '·'),
      h('span', null, relativeTime(n.at)),
      isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'author' }, icon('link', { size: 13 }), n.sourceTitle || 'source') : null));
}

// ---------- pomodoro ----------

function renderPomodoro(p) {
  $('#pomodoro').hidden = !pluginOn('focus-timer');
  const label = { idle: 'Focus timer', focus: 'Focus', shortBreak: 'Break', longBreak: 'Long break' }[p.phase] ?? p.phase;
  const buttons = [];
  if (p.phase === 'idle') {
    buttons.push(h('button', { class: 'icon-btn', title: 'Start focus', 'aria-label': 'Start focus', onclick: () => act(post('/api/pomodoro/start', { itemId: state.dashboard.focus?.id })) }, icon('play', { size: 16 })));
  } else {
    buttons.push(p.running
      ? h('button', { class: 'icon-btn', title: 'Pause', 'aria-label': 'Pause', onclick: () => act(post('/api/pomodoro/pause')) }, icon('pause', { size: 16 }))
      : h('button', { class: 'icon-btn', title: 'Resume', 'aria-label': 'Resume', onclick: () => act(post('/api/pomodoro/resume')) }, icon('play', { size: 16 })));
    buttons.push(h('button', { class: 'icon-btn', title: 'Skip', 'aria-label': 'Skip', onclick: () => act(post('/api/pomodoro/skip')) }, icon('skip', { size: 16 })));
    buttons.push(h('button', { class: 'icon-btn', title: 'Reset', 'aria-label': 'Reset', onclick: () => act(post('/api/pomodoro/reset')) }, icon('reset', { size: 16 })));
  }
  const dial = h('span', { class: 'pomo-dial', id: 'pomo-dial' }, ring(pomodoroFraction(p), { size: 34, stroke: 3 }));
  const el = $('#pomodoro');
  el.className = `pomodoro ${p.phase === 'focus' ? 'focus' : p.phase === 'idle' ? '' : 'break'}`.trim();
  el.replaceChildren(dial,
    h('span', { class: 'pomo-text' },
      h('span', { class: 'pomo-time', id: 'pomo-time' }, clock(p)),
      h('span', { class: 'pomo-label' }, p.itemTitle && p.phase !== 'idle' ? `${label} · ${p.itemTitle}` : label)),
    ...buttons);
}

function clock(p, now = Date.now()) {
  const seconds = p.running && p.endsAt ? Math.max(0, Math.ceil((new Date(p.endsAt) - now) / 1000)) : p.remainingSeconds;
  return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
}

setInterval(() => {
  const p = state.dashboard?.pomodoro;
  const el = document.getElementById('pomo-time');
  if (p && el) {
    el.textContent = clock(p);
    document.getElementById('pomo-dial')?.replaceChildren(ring(pomodoroFraction(p), { size: 34, stroke: 3 }));
    if (p.running && p.endsAt && new Date(p.endsAt) <= Date.now() && !clock.pending) {
      clock.pending = true;
      setTimeout(() => { clock.pending = false; refresh(); }, 2000);
    }
  }
}, 1000);

// ---------- groups ----------

async function addGroup() {
  const name = prompt('New group name (e.g. Personal, Side project):');
  if (name?.trim()) await act(post('/api/groups', { name }), `Added ${name}`);
}

async function editGroup(g) {
  const name = prompt(`Rename "${g.name}" (leave empty to delete the group):`, g.name);
  if (name === null) return;
  if (name.trim()) return act(patch(`/api/groups/${g.id}`, { name, color: g.color }));
  const target = state.dashboard.groups.find((x) => x.id !== g.id);
  if (target && confirm(`Delete "${g.name}" and move its tasks to "${target.name}"?`)) {
    if (state.group === g.id) setGroup(null);
    await act(del(`/api/groups/${g.id}?moveTo=${target.id}`));
  }
}

// ---------- drawer (task details) ----------

/** Saves the drawer's fields and notes (except `keepKey`'s note). Returns false when something couldn't be saved. */
async function flushDrawer(keepKey = null) {
  const results = await Promise.all([
    state.drawerAutosave?.flush() ?? true,
    ...[...noteSessions.keys()].filter((k) => k.startsWith('drawer:') && k !== keepKey).map(finishNote),
  ]);
  return results.every((ok) => ok !== false);
}

async function closeDrawer() {
  // Everything typed is saved before the panel closes; if that fails the panel stays open with the text in it.
  if (!(await flushDrawer())) return toast('Couldn’t save your changes yet – they’re kept, try again', 'error');
  state.panelClosed?.();
  state.panelClosed = null;
  state.drawerId = null;
  state.drawerAutosave = null;
  state.drawerNote = null;
  $('#drawer').hidden = true;
  $('#scrim').hidden = true;
  // Back to the view from a task's own address (#/task/<id>), without reloading the view.
  if (location.hash.startsWith('#/task/')) history.replaceState(null, '', `#/${shell.current() ?? 'today'}`);
  refresh({ background: true });
  currentView()?.refresh();
}

$('#scrim').addEventListener('click', closeDrawer);

/** What is being typed in the drawer, so re-opening the same task (after an action) keeps it and the caret. */
function drawerDraft() {
  const drawer = $('#drawer');
  const active = document.activeElement;
  const focused = active && drawer.contains(active) && active.dataset?.field ? { field: active.dataset.field, start: active.selectionStart, end: active.selectionEnd } : null;
  const note = state.drawerNote;
  return { note: note?.value ?? '', noteFocused: !!note && active === note, noteStart: note?.selectionStart, noteEnd: note?.selectionEnd, focused };
}

/**
 * Opens a task in the panel. `full` overrides the size for this task only (a #/task/ link opens it full size without
 * changing the size other tasks open at); re-opening the same task (after a change) keeps its size.
 */
async function openDrawer(id, { full = null } = {}) {
  const showFull = full ?? (state.drawerId === id && !$('#drawer').hidden ? state.drawerShownFull : state.drawerFull);
  // Anything still being typed is saved first. Re-opening the same task keeps the note being written (same note);
  // another task finishes it so the next keystrokes can't overwrite it.
  const same = state.drawerId === id && !$('#drawer').hidden;
  const draft = same ? drawerDraft() : null;
  const saved = await flushDrawer(same ? `drawer:${id}` : null);
  if (!saved && !same && !$('#drawer').hidden) return toast('Couldn’t save your changes yet – they’re kept, try again', 'error');
  // A plugin panel (e.g. Ask AI) gives way to the task: it stops its live updates.
  state.panelClosed?.();
  state.panelClosed = null;
  state.drawerId = id;
  let item;
  try {
    item = await api(`/api/items/${id}`);
  } catch (err) {
    closeDrawer();
    return toast(err.message, 'error');
  }
  await dragDone();
  if (state.drawerId !== id) return;
  const drawer = $('#drawer');
  const field = (label, control) => h('label', { class: 'field' }, h('span', null, label), control);
  const section = (title, ...children) => h('section', { class: 'drawer-section' }, h('h3', null, title), ...children);

  // ---- Fields: saved automatically as you type ----
  const title = h('input', { value: item.title, maxlength: 300, class: 'title-input', 'aria-label': 'Title', dataset: { field: 'title' } });
  const priority = h('select', { dataset: { field: 'priority' } }, ...['low', 'normal', 'high', 'critical'].map((p) => h('option', { value: p, selected: p === item.priority }, priorityMeta(p).label)));
  const deadline = h('input', { type: 'datetime-local', value: toLocalInput(item.deadline), dataset: { field: 'deadline' } });
  // A pasted file shows up in Attachments right away (without redrawing the panel and losing the caret).
  let attachmentsNode = null;
  const pastedFile = (attachment) => {
    item.attachments.push(attachment);
    const fresh = attachmentsSection(item);
    attachmentsNode?.replaceWith(fresh);
    attachmentsNode = fresh;
  };
  const details = acceptMedia(h('textarea', { rows: 4, maxlength: 10000, placeholder: 'Details, links, context… (markdown; paste images)', dataset: { field: 'details' } }, item.details ?? ''), id, pastedFile);
  // Images embedded in the details show under the box (a text box can't show them); redrawn only when they change.
  const detailImages = h('div', { class: 'embeds' });
  let shownNames = '';
  const showDetailImages = () => {
    const parts = splitEmbeds(details.value).filter((p) => p.embed);
    const names = parts.map((p) => p.embed).join('\n');
    if (names === shownNames) return;
    shownNames = names;
    detailImages.replaceChildren(...parts.map((p) => embedNode(id, p.embed, p.image)));
  };
  showDetailImages();
  details.addEventListener('input', showDetailImages);
  const sequential = h('input', { type: 'checkbox', checked: item.sequential, dataset: { field: 'sequential' } });
  const delay = h('input', { type: 'number', min: 0, step: 1, value: item.stepDelayMinutes ? item.stepDelayMinutes / 60 : '', placeholder: 'hours', dataset: { field: 'delay' } });
  const tags = h('input', { value: formatTags(item.tags), placeholder: 'deep work, q3', 'aria-label': 'Tags', dataset: { field: 'tags' } });
  const labels = new Set(item.labels.map((l) => l.name));

  const fieldGroups = [['title'], ['details'], ['priority'], ['deadline', 'clearDeadline'], ['sequential'], ['stepDelayMinutes', 'clearStepDelay'], ['tags'], ['labels']];
  const values = () => ({
    title: title.value,
    details: details.value,
    priority: priority.value,
    deadline: deadline.value ? new Date(deadline.value).toISOString() : null,
    clearDeadline: !deadline.value,
    sequential: sequential.checked,
    stepDelayMinutes: Number(delay.value) > 0 ? Math.round(Number(delay.value) * 60) : null,
    clearStepDelay: !(Number(delay.value) > 0),
    tags: parseTags(tags.value),
    labels: [...labels],
  });
  // Only fields changed here are sent, so edits made elsewhere meanwhile (Obsidian, agents, another tab) survive.
  let lastSaved = values();
  const autosave = createAutosave({
    delay: 700,
    onState: setSaveState,
    save: async (v) => {
      const changes = changedFields(lastSaved, v, fieldGroups);
      if (!Object.keys(changes).length) return;
      if ('title' in changes && !v.title.trim()) throw new Error('A title is required');
      await patch(`/api/items/${id}`, changes);
      lastSaved = v;
      refresh({ background: true });
    },
  });
  state.drawerAutosave = autosave;
  const changed = () => autosave.schedule(values());
  for (const el of [title, details, delay, tags]) el.addEventListener('input', changed);
  // What the tag box will save, as you type (commas separate tags; a space doesn't).
  const tagPreview = h('div', { class: 'tag-preview', 'aria-live': 'polite' });
  const showTags = () => tagPreview.replaceChildren(...parseTags(tags.value).map((t) => h('span', { class: 'chip tag' }, `#${t}`)));
  tags.addEventListener('input', showTags);
  showTags();
  for (const el of [priority, deadline, sequential]) el.addEventListener('change', changed);

  const labelPicker = h('div', { class: 'label-picker' });
  const renderLabels = () => {
    const known = new Map((state.dashboard?.labels ?? []).map((l) => [l.name.toLowerCase(), l]));
    item.labels.forEach((l) => known.set(l.name.toLowerCase(), l));
    const all = [...known.values()];
    const newLabel = h('input', { placeholder: '+ label', maxlength: 40, 'aria-label': 'New label', class: 'label-input', onkeydown: (e) => {
      if (e.key !== 'Enter' || !newLabel.value.trim()) return;
      e.preventDefault();
      labels.add(newLabel.value.trim());
      item.labels.push({ name: newLabel.value.trim(), color: '#64748b' });
      changed();
      renderLabels();
    } });
    labelPicker.replaceChildren(...all.map((l) => {
      const on = [...labels].some((x) => x.toLowerCase() === l.name.toLowerCase());
      const chipEl = labelChip(l, { onclick: () => { if (on) [...labels].filter((x) => x.toLowerCase() === l.name.toLowerCase()).forEach((x) => labels.delete(x)); else labels.add(l.name); changed(); renderLabels(); } });
      chipEl.classList.toggle('off', !on);
      chipEl.setAttribute('aria-pressed', String(on));
      return chipEl;
    }), newLabel);
  };
  renderLabels();

  const group = h('select', { disabled: !!item.parentId, 'aria-label': 'Group', onchange: () => act(post(`/api/items/${id}/move`, { groupId: group.value }), 'Moved') },
    ...state.dashboard.groups.map((g) => h('option', { value: g.id, selected: g.id === item.groupId }, g.name)));
  const parent = parentPicker(item);

  // ---- Subtasks (any depth) ----
  const subtaskInput = h('input', { placeholder: 'Add a subtask…', maxlength: 300 });
  const stepsInput = h('textarea', { rows: 3, placeholder: 'Rollout steps, one per line' });
  const stepDelay = h('input', { type: 'number', min: 0, value: 24, 'aria-label': 'Hours between the new steps' });

  // ---- Notes: saved as you type ----
  const noteKey = `drawer:${id}`;
  const noteInput = acceptMedia(h('textarea', { rows: 2, maxlength: 10000, placeholder: 'Note: what happened, what is next… (saves as you type; paste images)',
    oninput: () => noteSession(id, noteKey).autosave.schedule(noteInput.value),
    onkeydown: (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); newNote(); } } }), id, pastedFile);
  state.drawerNote = noteInput;
  const newNote = async () => {
    // A picture still uploading goes into this note, not the next one.
    await pastesDone();
    if (!noteInput.value.trim()) return;
    if (!(await finishNote(noteKey))) return toast('Couldn’t save the note yet – it’s kept, try again', 'error');
    noteInput.value = '';
    await act(Promise.resolve());
  };

  const remindIn = h('select', null, ...snoozeOptions().map((o) => h('option', { value: o.minutes }, o.label)));
  const remindMsg = h('input', { placeholder: 'Reminder message (optional)', maxlength: 300 });
  const meta = priorityMeta(item.priority);
  drawer.style.setProperty('--prio', meta.color);

  drawer.replaceChildren(
    h('div', { class: 'drawer-head' },
      h('span', { class: 'prio-pill' }, meta.label),
      h('span', { class: 'crumbs' }, item.path.slice(0, -1).join(' › ')),
      h('span', { id: 'save-state', class: 'save-state', 'aria-live': 'polite' }),
      item.obsidianUrl && pluginOn('obsidian') ? obsidianAnchor(item.obsidianUrl, { class: 'icon-btn', iconOnly: true, file: item.file }) : null,
      h('span', { class: 'task-actions' }, ...taskActions.map((a) => taskActionButton(a, id))),
      h('button', {
        class: 'icon-btn', type: 'button', 'aria-label': showFull ? 'Smaller' : 'Full size', title: showFull ? 'Back to the side panel' : 'Full size (more room to write)', 'aria-pressed': String(showFull),
        onclick: () => setDrawerFull(!showFull, id),
      }, icon(showFull ? 'shrink' : 'expand')),
      h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onclick: closeDrawer }, icon('x'))),
    title,
    // Side panel: one column. Full size: what's being written on the left, time, reminders and history on the right.
    h('div', { class: 'drawer-main' },
    waitingBanner(item),
    h('div', { class: 'chips-row' }, labelPicker),
    field('Tags', tags),
    tagPreview,
    h('p', { class: 'hint' }, 'Labels are colored categories you pick from a list (Urgent, Waiting…). Tags are free words you type, for anything else. Both filter: click one, or use the tag button by the search box.'),
    h('div', { class: 'row' }, field('Priority', priority), field('Deadline', deadline)),
    field('Details', details),
    detailImages,
    h('div', { class: 'row' }, item.parentId ? null : field('Group', group), field('Belongs to', parent),
      item.parentId || item.completedAt ? null : field('Board', h('select', { 'aria-label': 'Board column', onchange: (e) => act(post(`/api/items/${id}/stage`, { stage: e.target.value }), 'Moved') },
        ...[['inbox', 'Inbox'], ['next', 'Next'], ['doing', 'Doing']].map(([v, label]) => h('option', { value: v, selected: v === item.stage }, label))))),
    h('div', { class: 'row buttons' },
      item.completedAt && !item.parentId
        ? (item.archivedAt
          ? h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/unarchive`), 'Back in Done') }, icon('undo', { size: 16 }), 'Unarchive')
          : h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/archive`), 'Archived') }, icon('archive', { size: 16 }), 'Archive'))
        : null,
      item.completedAt
        ? h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reopen`), 'Reopened') }, icon('undo', { size: 16 }), 'Reopen')
        : h('button', { class: 'btn success', onclick: () => {
          const open = item.children.filter((c) => !c.completedAt).length;
          if (open && !confirm(`Also mark ${open} open subtask${open > 1 ? 's' : ''} as done?`)) return;
          act(post(`/api/items/${id}/complete`), 'Done');
        } }, icon('check', { size: 16 }), 'Done'),
      item.completedAt ? null : snoozeButton(item, true),
      h('a', { class: 'btn ghost', href: `report.html?id=${id}`, target: '_blank', rel: 'noopener' }, icon('report', { size: 16 }), 'Full report'),
      h('button', { class: 'btn ghost danger', onclick: () => { if (confirm(`Delete "${item.title}" and all its subtasks? (It goes to the vault's trash.)`)) { autosave.cancel(); closeDrawer(); act(del(`/api/items/${id}`), 'Deleted'); } } }, icon('trash', { size: 16 }), 'Delete')),

    section(`Subtasks${item.sequential ? ' · in order' : ''}`,
      subtaskTree(item.children, id),
      h('form', { class: 'row', onsubmit: (e) => { e.preventDefault(); submitAndClear(subtaskInput, (t) => post('/api/items', { title: t, parentId: id })); } }, subtaskInput, h('button', { class: 'btn', type: 'submit' }, icon('plus', { size: 16 }), 'Add')),
      h('details', null, h('summary', null, 'Steps in order & rollout steps'),
        h('div', { class: 'row' }, h('label', { class: 'check' }, sequential, 'Steps in order'), field('Hours between steps', delay)),
        stepsInput,
        h('div', { class: 'row' }, field('Hours between the new steps', stepDelay),
          h('button', { class: 'btn', onclick: () => {
            const titles = stepsInput.value.split('\n').map((s) => s.trim()).filter(Boolean);
            if (titles.length) act(post(`/api/items/${id}/steps`, { titles, stepDelayMinutes: Math.round(Number(stepDelay.value || 0) * 60) || null }), `Added ${titles.length} steps`);
          } }, icon('layers', { size: 16 }), 'Add steps')))),

    section('Notes',
      h('div', { class: 'note-form' }, noteInput, h('button', { class: 'btn', title: 'Start a new note (Ctrl+Enter)', onclick: newNote }, icon('plus', { size: 16 }), 'New note')),
      h('ul', { class: 'notes' }, ...item.notes.map((n) => h('li', null, h('div', { class: 'note-text' }, ...withEmbeds(n.text, id)), h('div', { class: 'meta' },
        h('span', { class: 'author' }, avatar(n.authorKind, n.author), n.author), h('span', null, '·'), h('span', null, relativeTime(n.at)),
        isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'author' }, icon('link', { size: 13 }), n.sourceTitle || 'source') : null))))),

    (attachmentsNode = attachmentsSection(item)),
    item.hasRich ? richSection(item) : null),

    h('div', { class: 'drawer-side' },
    timeSection(item),
    section('Reminders',
      h('ul', { class: 'reminders' }, ...item.reminders.filter((r) => !r.dismissedAt).map((r) => h('li', null,
        h('span', { class: 'author' }, icon('clock', { size: 14 }), `${r.message} · ${relativeTime(r.dueAt)}`),
        h('button', { class: 'link', onclick: () => act(post(`/api/items/${id}/reminders/${r.id}/dismiss`)) }, 'Dismiss')))),
      h('div', { class: 'row' }, remindIn, remindMsg, h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reminders`, { inMinutes: Number(remindIn.value), message: remindMsg.value || null }), 'Reminder set') }, 'Remind me'))),

    pluginOn('history') ? historySection(item) : null));
  $('#scrim').hidden = false;
  drawer.dataset.itemId = id;
  state.drawerShownFull = showFull;
  drawer.classList.toggle('full', showFull);
  drawer.hidden = false;
  // Full size has its own address (#/task/<id>): Back closes it. The side panel stays on the view's address.
  const taskHash = `#/task/${id}`;
  if (showFull && location.hash !== taskHash) {
    if (location.hash.startsWith('#/task/')) history.replaceState(null, '', taskHash);
    else history.pushState(null, '', taskHash);
  } else if (!showFull && location.hash.startsWith('#/task/')) {
    history.replaceState(null, '', `#/${shell.current() ?? 'today'}`);
  }
  restoreDrawerDraft(drawer, draft, noteInput);
  if (state.refocus) focusOrderControl(state.refocus, null, drawer);

  // Drop files anywhere on the panel to attach them.
  drawer.ondragover = (e) => { if (e.dataTransfer?.types.includes('Files')) { e.preventDefault(); drawer.classList.add('dropping'); } };
  drawer.ondragleave = (e) => { if (e.target === drawer) drawer.classList.remove('dropping'); };
  drawer.ondrop = (e) => { e.preventDefault(); drawer.classList.remove('dropping'); uploadFiles(id, e.dataTransfer.files); };
}

function restoreDrawerDraft(drawer, draft, noteInput) {
  if (!draft) return;
  if (draft.note) {
    noteInput.value = draft.note;
    if (draft.noteFocused) {
      noteInput.focus();
      noteInput.setSelectionRange(draft.noteStart ?? draft.note.length, draft.noteEnd ?? draft.note.length);
    }
  }
  if (draft.focused) {
    const el = drawer.querySelector(`[data-field="${CSS.escape(draft.focused.field)}"]`);
    if (el) {
      el.focus();
      if (typeof draft.focused.start === 'number' && typeof el.setSelectionRange === 'function') {
        try { el.setSelectionRange(draft.focused.start, draft.focused.end); } catch { /* not a text field */ }
      }
    }
  }
}

// ---------- plugins ----------

/**
 * Opens the side panel with a plugin's content (the same panel as task details). `onClose` runs when it closes.
 * Returns false if what was typed in the panel couldn't be saved first.
 */
async function openPanel(title, content, { iconName = null, onClose = null } = {}) {
  if (!(await flushDrawer())) {
    toast('Couldn’t save your changes yet – they’re kept, try again', 'error');
    return false;
  }
  state.panelClosed?.();
  state.panelClosed = onClose;
  state.drawerId = null;
  state.drawerAutosave = null;
  state.drawerNote = null;
  const drawer = $('#drawer');
  drawer.style.removeProperty('--prio');
  drawer.classList.remove('full');
  delete drawer.dataset.itemId;
  drawer.replaceChildren(
    h('div', { class: 'drawer-head' },
      h('span', { class: 'connect-title' }, iconName ? icon(iconName, { size: 18 }) : null, title),
      h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onclick: closeDrawer }, icon('x'))),
    content);
  $('#scrim').hidden = false;
  drawer.hidden = false;
  return true;
}

/** Buttons plugins add to every task's details (Copy for Loop…): onClick gets the task's id. */
const taskActions = [];
const taskActionButton = (a, id) => h('button', { class: 'icon-btn', type: 'button', 'aria-label': a.label, title: a.title ?? a.label, onclick: () => a.onClick(id) }, icon(a.iconName));

/** What plugin modules may use: the API, building blocks, and a few hooks into the app. */
const pluginHost = {
  api, post, put, del, h, icon, toast, openPanel, closePanel: closeDrawer,
  refresh: () => refresh({ background: true }),
  /** The group picked in the tabs (null: all), and every group. */
  group: () => state.group,
  groups: () => state.dashboard?.groups ?? [],
  addHeaderButton({ iconName, label, onClick }) {
    const button = h('button', { class: 'icon-btn', type: 'button', 'aria-label': label, title: label, onclick: onClick }, icon(iconName));
    $('#plugin-buttons').append(button);
    return button;
  },
  addTaskAction(action) {
    taskActions.push(action);
    // A task opened from a link shows before the plugins load: it gets the button too.
    const id = $('#drawer').dataset.itemId;
    if (id) $('#drawer .task-actions')?.append(taskActionButton(action, id));
  },
};

/** Whether a built-in feature (plugin) is on. Until the list loads everything shows. */
const pluginOn = (id) => !state.plugins || state.plugins.has(id);

/** Loads the UI of each enabled plugin (built-in modules served by the app). One failing never breaks the board. */
async function loadPlugins() {
  let plugins = [];
  try {
    plugins = await api('/api/plugins');
  } catch {
    return;
  }
  state.plugins = new Set(plugins.filter((p) => p.enabled).map((p) => p.id));
  if (state.dashboard) render();
  for (const p of plugins.filter((x) => x.enabled && x.webModule)) {
    try {
      const module = await import(p.webModule);
      await module.activate?.(pluginHost);
    } catch (err) {
      console.warn(`Plugin ${p.id} failed to load`, err);
    }
  }
}

// ---------- plugins panel ----------

/** Every optional feature is a plugin: switch them on or off here (applies after a restart). */
/** The extras are in Settings (with appearance and connected apps). */
function openPlugins() {
  location.hash = '#/settings/plugins';
}

function subtaskTree(children, parentId) {
  if (!children.length) return h('p', { class: 'muted small' }, 'No subtasks yet.');
  const order = () => children.map((c) => c.id);
  // Reordering keeps each subtask under the same parent; the panel then refreshes (keeping what is being typed).
  const reorder = async (ids, movedId) => {
    if (!changed(order(), ids)) return;
    state.refocus = movedId;
    await act(post(`/api/items/${movedId}/reorder`, { before: beforeOf(ids, movedId) }));
    state.refocus = null;
  };
  return h('ul', { class: 'subtasks' }, ...children.map((c) => {
    const li = h('li', { class: `sub ${c.state}` });
    const controls = sortable(li, c.id, { scope: `sub:${parentId}`, ids: order, onOrder: reorder, buttons: true });
    li.append(
      h('div', { class: 'sub-row' },
        h('button', { class: 'check-btn', title: c.completedAt ? 'Reopen' : 'Done', 'aria-label': c.completedAt ? `Reopen ${c.title}` : `Complete ${c.title}`, onclick: () => act(post(`/api/items/${c.id}/${c.completedAt ? 'reopen' : 'complete'}`)) }, c.completedAt ? icon('check', { size: 14 }) : null),
        h('button', { class: 'link', onclick: () => openDrawer(c.id) }, c.title),
        ...c.tags.map(tagChip),
        h('span', { class: 'state' }, c.state === 'locked' ? icon('lock', { size: 13 }) : null, stateLabel(c)),
        controls),
      // append() would write "null" for a missing child.
      ...(c.children.length ? [subtaskTree(c.children, c.id)] : []));
    return li;
  }));
}

/** "Belongs to": move the task under another task or back to the top level. Options load on first use. */
function parentPicker(item) {
  const select = h('select', { 'aria-label': 'Belongs to' },
    h('option', { value: '', selected: !item.parentId }, 'Nothing (top level)'),
    item.parentId ? h('option', { value: item.parentId, selected: true }, item.path.at(-2)) : null);
  let loaded = false;
  select.addEventListener('focus', async () => {
    if (loaded) return;
    loaded = true;
    const roots = await api('/api/items');
    const options = [];
    const walk = (nodes, depth) => nodes.forEach((n) => {
      if (n.id === item.id) return; // a task can't go inside itself
      options.push(h('option', { value: n.id, selected: n.id === item.parentId }, `${'  '.repeat(depth)}${depth ? '↳ ' : ''}${n.title}`));
      walk(n.children.filter((c) => !c.completedAt), depth + 1);
    });
    walk(roots, 0);
    select.replaceChildren(h('option', { value: '', selected: !item.parentId }, 'Nothing (top level)'), ...options);
  });
  select.addEventListener('change', () => act(post(`/api/items/${item.id}/move`, select.value ? { parentId: select.value } : { toTopLevel: true, groupId: item.groupId }), 'Moved'));
  return select;
}

/** Full size gives a task the whole window (its own address, #/task/<id>); the side panel keeps the view in sight. */
/** The size button: this task changes size now, and tasks open at that size from now on. */
function setDrawerFull(full, id) {
  state.drawerFull = full;
  localStorage.setItem('tt.drawer.full', full ? '1' : '0');
  openDrawer(id, { full });
}

/** Time spent: the timer for this task, the total (with subtasks), every stretch (fix or remove), and time to add by hand. */
function timeSection(item) {
  const timer = state.dashboard?.timer;
  const timing = timer?.running && timer.itemId === item.id;
  const own = [...(item.timeEntries ?? [])].reverse();
  const shown = own.slice(0, 12);
  const start = h('input', { type: 'datetime-local', value: localInput(new Date(Date.now() - 30 * 60_000)), 'aria-label': 'Started at' });
  const minutes = h('input', { type: 'number', min: 1, max: 1440, step: 1, value: 30, 'aria-label': 'Minutes' });
  const entryRow = (e) => {
    const s = new Date(e.start);
    const when = `${s.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} ${s.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
    return h('li', { class: e.end ? '' : 'running' },
      h('span', { class: 'when' }, when),
      h('span', { class: 'what' }, e.end ? duration(e.seconds) : `running · ${duration(e.seconds)}`),
      e.source === 'focus' ? h('span', { class: 'chip warn', title: 'A focus (Pomodoro) session' }, 'focus') : null,
      e.end ? h('button', { class: 'icon-btn small', type: 'button', title: 'Fix the length', 'aria-label': `Fix the time from ${when}`, onclick: () => {
        const value = prompt('How many minutes was it?', String(Math.round(e.seconds / 60)));
        const m = Number(value);
        if (value !== null && m > 0) act(put(`/api/items/${item.id}/time/${e.id}`, { start: e.start, end: new Date(s.getTime() + m * 60_000).toISOString() }), 'Fixed');
      } }, icon('pencil', { size: 13 })) : null,
      h('button', { class: 'icon-btn small', type: 'button', title: 'Remove', 'aria-label': `Remove the time from ${when}`, onclick: () => { if (confirm('Remove this time?')) act(del(`/api/items/${item.id}/time/${e.id}`), 'Removed'); } }, icon('x', { size: 13 })));
  };
  return h('section', { class: 'drawer-section time-section' },
    h('h3', null, 'Time'),
    h('div', { class: 'time-head' },
      item.completedAt ? null : (timing
        ? h('button', { class: 'btn on', type: 'button', onclick: () => act(post('/api/timer/stop'), 'Timer stopped') }, icon('stop', { size: 16 }), 'Stop timer')
        : h('button', { class: 'btn', type: 'button', onclick: () => act(post('/api/timer/start', { itemId: item.id }), 'Timer started') }, icon('timer', { size: 16 }), 'Start timer')),
      h('span', { class: 'time-total' }, h('strong', null, duration(item.timeSpentSeconds ?? 0)), item.children.length ? ' with subtasks' : ' in total')),
    shown.length ? h('ul', { class: 'time-list' }, ...shown.map(entryRow)) : null,
    own.length > shown.length ? h('p', { class: 'muted small' }, `and ${own.length - shown.length} earlier`) : null,
    h('details', null, h('summary', null, 'Add time by hand'),
      h('form', { class: 'row', onsubmit: (e) => {
        e.preventDefault();
        const from = new Date(start.value);
        const m = Number(minutes.value);
        if (!start.value || !(m > 0)) return;
        act(post(`/api/items/${item.id}/time`, { start: from.toISOString(), end: new Date(from.getTime() + m * 60_000).toISOString() }), `Logged ${duration(m * 60)}`);
      } },
      h('label', { class: 'field' }, h('span', null, 'Started'), start),
      h('label', { class: 'field' }, h('span', null, 'Minutes'), minutes),
      h('button', { class: 'btn', type: 'submit' }, icon('plus', { size: 16 }), 'Add'))));
}

function attachmentsSection(item) {
  const picker = h('input', { type: 'file', multiple: true, hidden: true, onchange: () => uploadFiles(item.id, picker.files) });
  return h('section', { class: 'drawer-section' },
    h('h3', null, 'Attachments'),
    item.attachments.length
      ? h('ul', { class: 'attachments' }, ...item.attachments.map((a) => h('li', null,
        icon('paperclip', { size: 14 }),
        h('a', { href: a.url, target: '_blank', rel: 'noopener', class: 'link' }, a.fileName),
        h('span', { class: 'muted small' }, `${fileSize(a.size)} · ${a.addedBy}`),
        h('button', { class: 'icon-btn small', title: 'Remove', 'aria-label': `Remove ${a.fileName}`, onclick: () => { if (confirm(`Remove "${a.fileName}"?`)) act(del(a.url), 'Removed'); } }, icon('x', { size: 14 })))))
      : null,
    h('div', { class: 'dropzone' }, picker,
      h('button', { class: 'btn', onclick: () => picker.click() }, icon('paperclip', { size: 16 }), 'Attach files'),
      h('span', { class: 'muted small' }, 'or drop files here')));
}

async function uploadFiles(id, files) {
  const list = [...(files ?? [])];
  if (!list.length) return;
  await act((async () => { for (const file of list) await upload(`/api/items/${id}/attachments`, file); })(), list.length === 1 ? `Attached ${list[0].name}` : `Attached ${list.length} files`);
}

/** The task's rich HTML version, shown in a locked-down frame (no scripts, separate origin). */
function richSection(item) {
  const frame = h('iframe', { class: 'rich-frame', sandbox: '', src: `/api/items/${item.id}/rich`, title: `Rich version of ${item.title}`, referrerpolicy: 'no-referrer' });
  return h('section', { class: 'drawer-section' },
    h('h3', null, 'Rich view'),
    frame,
    h('div', { class: 'row' },
      h('a', { class: 'btn ghost', href: `/api/items/${item.id}/rich`, target: '_blank', rel: 'noopener' }, 'Open full size'),
      h('button', { class: 'btn ghost danger', onclick: () => { if (confirm('Remove the rich version? (It is kept in the history.)')) act(put(`/api/items/${item.id}/rich`, { html: null }), 'Removed'); } }, 'Remove')));
}

/** Every change is kept as a version; any version can be viewed or restored. */
function historySection(item) {
  const list = h('ul', { class: 'history' });
  const preview = h('pre', { class: 'history-preview', hidden: true });
  const box = h('details', { class: 'drawer-section history-box' }, h('summary', null, h('h3', null, 'History')), list, preview);
  box.addEventListener('toggle', async () => {
    if (!box.open || box.dataset.loaded) return;
    box.dataset.loaded = '1';
    const versions = await api(`/api/items/${item.id}/history`).catch(() => []);
    if (!versions.length) {
      list.replaceChildren(h('li', { class: 'muted small' }, 'Versions appear here a few seconds after changes (needs git).'));
      return;
    }
    list.replaceChildren(...versions.map((v, i) => h('li', null,
      h('span', { class: 'when', title: new Date(v.at).toLocaleString() }, relativeTime(v.at)),
      h('span', { class: 'what' }, v.message.split('\n')[0]),
      h('button', { class: 'link', onclick: async () => {
        preview.hidden = false;
        preview.textContent = await text(`/api/items/${item.id}/history/${v.id}`).catch((e) => e.message);
      } }, 'View'),
      i === 0 ? h('span', { class: 'muted small' }, 'current') : h('button', { class: 'link', onclick: () => {
        if (confirm('Restore this version? The current one stays in the history.')) act(post(`/api/items/${item.id}/history/${v.id}/restore`), 'Restored');
      } }, 'Restore'))));
  });
  return box;
}

/** Clears an input optimistically and puts the text back if the request fails. */
async function submitAndClear(input, request, message) {
  const value = input.value;
  if (!value.trim()) return;
  input.value = '';
  if (!(await act(request(value), message))) input.value = value;
}

/**
 * Why a task is waiting: its own snooze or wait (Do now brings it back), or its parent's (open the parent to change it).
 * Nothing when it isn't waiting, or is waiting for a step before it.
 */
function waitingBanner(item) {
  if (item.state !== 'waiting' || item.completedAt || !(item.wakeAt || item.waitingForTitle)) return null;
  const why = [item.waitingForTitle ? `waits for “${item.waitingForTitle}”` : null, item.wakeAt ? `snoozed until ${whenText(item.wakeAt)}` : null].filter(Boolean).join(' and ');
  const text = item.heldById ? `Its parent “${item.heldByTitle}” ${why}` : `${why[0].toUpperCase()}${why.slice(1)}`;
  return h('div', { class: 'waiting-banner', role: 'status' }, icon(item.waitingForTitle ? 'link' : 'clock', { size: 16 }), h('span', null, text),
    item.heldById
      ? h('button', { class: 'btn ghost', type: 'button', onclick: () => openDrawer(item.heldById) }, icon('expand', { size: 15 }), 'Open parent')
      : h('button', { class: 'btn ghost', type: 'button', onclick: () => act(post(`/api/items/${item.id}/schedule`, { clear: true }), 'Back now') }, icon('undo', { size: 15 }), 'Do now'));
}

function stateLabel(item) {
  if (item.state === 'waiting' && item.waitingForTitle) return `waiting for “${item.waitingForTitle}”`;
  if (item.state === 'waiting' && item.nextActionAt) return `waiting · ${relativeTime(item.nextActionAt)}`;
  return { actionable: 'now', container: 'in progress', locked: 'later', done: 'done', waiting: 'waiting' }[item.state] ?? item.state;
}

function toLocalInput(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// ---------- forms & keyboard ----------

$('#capture-icon').append(icon('plus', { size: 20 }));

$('#capture').addEventListener('submit', (e) => {
  e.preventDefault();
  const input = $('#capture-input');
  const text = input.value.trim();
  if (!text) return;
  input.value = '';
  act(post('/api/capture', { text, groupId: state.group }), 'Added').then((ok) => { if (!ok && !input.value) input.value = text; });
});

$('#login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    await post('/api/login', { token: $('#login-token').value });
    $('#login-error').textContent = '';
    refresh();
  } catch (err) {
    $('#login-error').textContent = err.status === 401 ? 'That token did not match.' : err.message;
  }
});

/** Quick capture: on Today's capture box (from any view). */
function focusCapture() {
  if (shell.current() !== 'today') location.hash = '#/today';
  setTimeout(() => $('#capture-input').focus(), 0);
}

document.addEventListener('keydown', (e) => {
  if (!$('#break').hidden) return; // nothing behind the break screen reacts to keys
  const typing = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName) || document.activeElement?.isContentEditable;
  if (e.key === 'Escape') { closeMenus(); shell.closeNav(); if (!$('#drawer').hidden) closeDrawer(); }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === 'n' && !typing) { e.preventDefault(); focusCapture(); }
  if (e.key === '/' && !typing) { e.preventDefault(); $('#filter').focus(); }
});

// Filter: one search box with the same syntax as the CLI and agents (#tag, label:x, group:x, is:done, words).
let filterTimer;
$('#filter').addEventListener('input', () => { clearTimeout(filterTimer); filterTimer = setTimeout(() => setQuery($('#filter').value), 250); });
$('#filter').addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.stopPropagation(); setQuery(''); $('#filter').blur(); } });
$('#filter-clear').addEventListener('click', () => setQuery(''));
$('#filter-pick').append(icon('tag', { size: 15 }));
  $('#filter-pick').addEventListener('click', (e) => { e.stopPropagation(); toggleFilterMenu(); });
$('#filter-menu').addEventListener('click', (e) => e.stopPropagation());
$('#filter-menu').addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.stopPropagation(); closeFilterMenu({ focusButton: true }); } });
$('#search-icon').append(icon('search', { size: 16 }));

// Nothing typed is ever lost: pending saves are flushed when the page is hidden or closed.
const flushAll = () => {
  // The page may be going away: these requests must outlive it.
  setKeepalive(true);
  state.drawerAutosave?.flush();
  noteSessions.forEach((s) => s.autosave.flush());
  setKeepalive(false);
};
window.addEventListener('pagehide', flushAll);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') flushAll(); });

// ---------- views ----------

let lastFocused = null;
const ctx = {
  api, post, h, icon, toast, act, state, openDrawer, relativeTime, labelChip, openPlugins, refreshAll,
  navigate: (view) => { location.hash = `#/${view}`; },
  groups: () => state.dashboard?.groups ?? [],
  dashboard: () => state.dashboard,
  signedIn: () => !!state.signedIn,
  setGroup: (id) => { setGroup(id); refreshAll(); },
  capture: focusCapture,
  closeDrawer,
  drawerOpen: () => !$('#drawer').hidden,
  pluginOn: (id) => pluginOn(id),
  saveFocus: () => { lastFocused = document.activeElement; },
  restoreFocus: () => { lastFocused?.focus?.(); lastFocused = null; },
  views: {},
};
const shell = createShell(ctx);
const later = createLater(ctx);
ctx.pluginHost = pluginHost;
ctx.views = { board: createBoardView(ctx), tasks: createOutlineView(ctx), done: createDoneView(ctx), reports: createReportsView(ctx), settings: createSettingsView(ctx) };
state.drawerFull = localStorage.getItem('tt.drawer.full') === '1';

/** A view's own background refresh waits while something in it is being typed in or dragged. */
function refreshViewQuietly() {
  const view = currentView();
  const active = document.activeElement;
  if (!view || (active && view.root.contains(active) && ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName)) || view.root.querySelector('.dragging')) return;
  (view.poll ?? view.refresh)();
}

document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { refresh({ background: true }); refreshViewQuietly(); } });
/** The shared theme, changed elsewhere (the sidebar's menu, another window). */
const followTheme = () => follow(() => api('/api/settings').then((s) => s.theme)).catch(() => {});
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') followTheme(); });
// Coming back to this window (from the sidebar, where the theme may have just changed).
window.addEventListener('focus', followTheme);
setInterval(() => { if (document.visibilityState === 'visible') { refresh({ background: true }); refreshViewQuietly(); followTheme(); } }, 15000);
refresh().then(() => {
  // The theme every window shares (js/theme.js already applied the one remembered here).
  followTheme();
  // Deep link from the sidebar / Teams: /?item=<id> opens that task's details.
  loadPlugins();
  const params = new URLSearchParams(location.search);
  const item = params.get('item');
  if (item && state.dashboard) openDrawer(item);
  else if (params.has('plugins') && state.dashboard) openPlugins();
});

