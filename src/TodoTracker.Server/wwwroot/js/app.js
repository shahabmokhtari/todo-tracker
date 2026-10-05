import { api, post, patch, put, del, h, upload, text, ApiError, setKeepalive } from './api.js';
import { relativeTime, priorityMeta, snoozeOptions, progressPercent, stepLabel, isSafeHttpUrl, greeting, metaChips, summaryLine, pomodoroFraction, fileSize, parseTags, queryFor } from './format.js';
import { icon, ring } from './icons.js';
import { createAutosave, changedFields } from './autosave.js';

const $ = (sel) => document.querySelector(sel);
const state = {
  dashboard: null,
  group: localStorage.getItem('tt.group') || null,
  drawerId: null,
  query: '',
  vault: null,
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

function showBoard(authenticated) {
  $('#login').hidden = authenticated;
  $('#board').hidden = !authenticated;
}

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
  $('#greeting').textContent = greeting();
  $('#summary').textContent = summaryLine(d);
  renderTabs(d);
  renderFocus(d);
  renderSection('#sec-now', d.now.filter((c) => c.id !== d.focus?.id).map((c) => card(c)), d.now.length, 'Nothing else right now. Nice.');
  renderSection('#sec-waiting', d.waiting.map((c) => card(c, { waiting: true })), d.waiting.length, 'Nothing is parked.');
  // Workstreams are tasks with subtasks; single tasks already live in Do now / Waiting.
  const streams = d.overview.filter((o) => o.hasChildren);
  renderSection('#sec-overview', streams.map(workstream), streams.length, 'Tasks with subtasks or rollout steps show up here with progress.');
  renderSection('#sec-notes', d.recentNotes.map(noteRow), d.recentNotes.length, 'Notes you and your agents log appear here.');
  renderPomodoro(d.pomodoro);
  renderProblems(d.problems ?? []);
  renderFilter();
  document.title = d.now.length ? `(${d.now.length}) Todo Tracker` : 'Todo Tracker';
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
  refresh();
}

function renderVault() {
  const v = state.vault;
  if (!v) return;
  $('#vault').replaceChildren(
    icon('folder', { size: 14 }),
    h('span', { class: 'muted', title: v.path }, 'Saved as markdown in ', h('code', null, v.path)),
    h('a', { href: v.obsidianUrl, class: 'link' }, 'Open in Obsidian'));
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
      onclick: () => { setGroup(id); refresh(); },
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
    h('button', { class: 'focus-title', onclick: () => openDrawer(f.id) }, f.title),
    sub.length ? h('div', { class: 'focus-sub' }, ...sub) : null,
    f.needsAttention && f.reminderMessage ? h('div', { class: 'reminder-banner' }, icon('clock', { size: 16 }), f.reminderMessage) : null,
    f.lastNote ? h('blockquote', { class: 'last-note' }, f.lastNote) : null,
    actions(f, { big: true }));
  el.style.setProperty('--prio', meta.color);
  $('#focus').replaceChildren(el);
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

function card(c, { waiting = false } = {}) {
  const li = h('li', { class: `item${c.needsAttention ? ' attention' : ''}` },
    h('span', { class: 'prio', title: `${priorityMeta(c.priority).label} priority` }),
    h('div', { class: 'body' },
      h('button', { class: 'title link', onclick: () => openDrawer(c.id) }, c.title),
      h('div', { class: 'meta' }, ...metaChips(c, { waiting }).map(chip), ...(c.labels ?? []).map((l) => labelChip(l)), ...(c.tags ?? []).map(tagChip),
        c.attachmentCount ? h('span', { class: 'chip muted', title: 'Attachments' }, icon('paperclip', { size: 12 }), String(c.attachmentCount)) : null),
      c.needsAttention && c.reminderMessage ? h('div', { class: 'reminder' }, icon('clock', { size: 14 }), c.reminderMessage) : null),
    actions(c, { waiting }));
  li.style.setProperty('--prio', priorityMeta(c.priority).color);
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
  let finishing = false;
  const finish = async () => {
    if (finishing) return;
    finishing = true;
    try {
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
  if (!waiting) bar.append(actionButton({ name: 'Focus', iconName: 'play', big, tone: 'warn', title: 'Start focus timer', onclick: () => act(post('/api/pomodoro/start', { itemId: c.id }), 'Focus started') }));
  return h('div', { class: 'action-wrap' }, bar, noteBox);
}

function snoozeButton(c, big) {
  const menu = h('div', { class: 'menu', hidden: true, role: 'menu' },
    ...snoozeOptions().map((o) => h('button', {
      role: 'menuitem',
      onclick: () => act(post(`/api/items/${c.id}/schedule`, { inMinutes: o.minutes, notify: true }), `Snoozed until ${o.label.toLowerCase()}`),
    }, icon('clock', { size: 15 }), o.label)));
  const button = actionButton({ name: 'Later', iconName: 'clock', big, title: 'Snooze (remind me later)', onclick: (e) => { e.stopPropagation(); closeMenus(); menu.hidden = !menu.hidden; } });
  button.setAttribute('aria-haspopup', 'menu');
  return h('span', { class: 'menu-wrap' }, button, menu);
}

function closeMenus() {
  document.querySelectorAll('.menu').forEach((m) => (m.hidden = true));
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
    h('div', { class: 'note-text' }, n.text),
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
  state.drawerId = null;
  state.drawerAutosave = null;
  state.drawerNote = null;
  $('#drawer').hidden = true;
  $('#scrim').hidden = true;
  refresh({ background: true });
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

async function openDrawer(id) {
  // Anything still being typed is saved first. Re-opening the same task keeps the note being written (same note);
  // another task finishes it so the next keystrokes can't overwrite it.
  const same = state.drawerId === id && !$('#drawer').hidden;
  const draft = same ? drawerDraft() : null;
  const saved = await flushDrawer(same ? `drawer:${id}` : null);
  if (!saved && !same && !$('#drawer').hidden) return toast('Couldn’t save your changes yet – they’re kept, try again', 'error');
  state.drawerId = id;
  let item;
  try {
    item = await api(`/api/items/${id}`);
  } catch (err) {
    closeDrawer();
    return toast(err.message, 'error');
  }
  if (state.drawerId !== id) return;
  const drawer = $('#drawer');
  const field = (label, control) => h('label', { class: 'field' }, h('span', null, label), control);
  const section = (title, ...children) => h('section', { class: 'drawer-section' }, h('h3', null, title), ...children);

  // ---- Fields: saved automatically as you type ----
  const title = h('input', { value: item.title, maxlength: 300, class: 'title-input', 'aria-label': 'Title', dataset: { field: 'title' } });
  const priority = h('select', { dataset: { field: 'priority' } }, ...['low', 'normal', 'high', 'critical'].map((p) => h('option', { value: p, selected: p === item.priority }, priorityMeta(p).label)));
  const deadline = h('input', { type: 'datetime-local', value: toLocalInput(item.deadline), dataset: { field: 'deadline' } });
  const details = h('textarea', { rows: 4, maxlength: 10000, placeholder: 'Details, links, context… (markdown)', dataset: { field: 'details' } }, item.details ?? '');
  const sequential = h('input', { type: 'checkbox', checked: item.sequential, dataset: { field: 'sequential' } });
  const delay = h('input', { type: 'number', min: 0, step: 1, value: item.stepDelayMinutes ? item.stepDelayMinutes / 60 : '', placeholder: 'hours', dataset: { field: 'delay' } });
  const tags = h('input', { value: item.tags.map((t) => `#${t}`).join(' '), placeholder: '#tag #another', 'aria-label': 'Tags', dataset: { field: 'tags' } });
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
  const noteInput = h('textarea', { rows: 2, maxlength: 10000, placeholder: 'Note: what happened, what is next… (saves as you type)',
    oninput: () => noteSession(id, noteKey).autosave.schedule(noteInput.value),
    onkeydown: (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); newNote(); } } });
  state.drawerNote = noteInput;
  const newNote = async () => {
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
      item.obsidianUrl ? h('a', { class: 'icon-btn', href: item.obsidianUrl, title: `Open in Obsidian (${item.file})`, 'aria-label': 'Open in Obsidian' }, icon('obsidian')) : null,
      h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onclick: closeDrawer }, icon('x'))),
    title,
    h('div', { class: 'chips-row' }, labelPicker),
    field('Tags', tags),
    h('div', { class: 'row' }, field('Priority', priority), field('Deadline', deadline)),
    field('Details', details),
    h('div', { class: 'row' }, item.parentId ? null : field('Group', group), field('Belongs to', parent)),
    h('div', { class: 'row buttons' },
      item.completedAt
        ? h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reopen`), 'Reopened') }, icon('undo', { size: 16 }), 'Reopen')
        : h('button', { class: 'btn success', onclick: () => {
          const open = item.children.filter((c) => !c.completedAt).length;
          if (open && !confirm(`Also mark ${open} open subtask${open > 1 ? 's' : ''} as done?`)) return;
          act(post(`/api/items/${id}/complete`), 'Done');
        } }, icon('check', { size: 16 }), 'Done'),
      h('a', { class: 'btn ghost', href: `report.html?id=${id}`, target: '_blank', rel: 'noopener' }, icon('report', { size: 16 }), 'Full report'),
      h('button', { class: 'btn ghost danger', onclick: () => { if (confirm(`Delete "${item.title}" and all its subtasks? (It goes to the vault's trash.)`)) { autosave.cancel(); closeDrawer(); act(del(`/api/items/${id}`), 'Deleted'); } } }, icon('trash', { size: 16 }), 'Delete')),

    section(`Subtasks${item.sequential ? ' · in order' : ''}`,
      subtaskTree(item.children),
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
      h('ul', { class: 'notes' }, ...item.notes.map((n) => h('li', null, h('div', { class: 'note-text' }, n.text), h('div', { class: 'meta' },
        h('span', { class: 'author' }, avatar(n.authorKind, n.author), n.author), h('span', null, '·'), h('span', null, relativeTime(n.at)),
        isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'author' }, icon('link', { size: 13 }), n.sourceTitle || 'source') : null))))),

    attachmentsSection(item),
    item.hasRich ? richSection(item) : null,

    section('Reminders',
      h('ul', { class: 'reminders' }, ...item.reminders.filter((r) => !r.dismissedAt).map((r) => h('li', null,
        h('span', { class: 'author' }, icon('clock', { size: 14 }), `${r.message} · ${relativeTime(r.dueAt)}`),
        h('button', { class: 'link', onclick: () => act(post(`/api/items/${id}/reminders/${r.id}/dismiss`)) }, 'Dismiss')))),
      h('div', { class: 'row' }, remindIn, remindMsg, h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reminders`, { inMinutes: Number(remindIn.value), message: remindMsg.value || null }), 'Reminder set') }, 'Remind me'))),

    historySection(item));
  $('#scrim').hidden = false;
  drawer.dataset.itemId = id;
  drawer.hidden = false;
  restoreDrawerDraft(drawer, draft, noteInput);

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

// ---------- connect an AI app ----------

/** One place to hook up Claude, Copilot, VS Code, ChatGPT…: pick the app, copy one thing, done. */
async function openConnect() {
  if (!(await flushDrawer())) return toast('Couldn’t save your changes yet – they’re kept, try again', 'error');
  let info;
  try {
    info = await api('/api/connect');
  } catch (err) {
    return toast(err.message, 'error');
  }
  state.drawerId = null;
  state.drawerAutosave = null;
  state.drawerNote = null;
  const drawer = $('#drawer');
  drawer.style.removeProperty('--prio');
  delete drawer.dataset.itemId;
  const copy = async (snippet) => {
    try {
      await navigator.clipboard.writeText(snippet);
      toast('Copied');
    } catch {
      toast('Couldn’t copy – select the text and copy it', 'error');
    }
  };
  drawer.replaceChildren(
    h('div', { class: 'drawer-head' },
      h('span', { class: 'connect-title' }, icon('sparkles', { size: 18 }), 'Connect an AI app'),
      h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onclick: closeDrawer }, icon('x'))),
    h('p', { class: 'muted' }, 'Let Claude, Copilot and other AI apps see what you are working on and add, organize and finish tasks for you. ',
      'Everything they change shows up here, marked with who did it, and can be undone from the history.'),
    ...info.setups.map((s, i) => h('details', { class: 'connect', open: i === 0, dataset: { id: s.id } },
      h('summary', null, s.app),
      h('p', { class: 'muted small' }, s.steps),
      h('pre', { class: 'snippet' }, h('code', null, s.snippet)),
      h('div', { class: 'row' },
        h('button', { class: 'btn', type: 'button', onclick: () => copy(s.snippet) }, icon('note', { size: 16 }), 'Copy'),
        s.link ? h('a', { class: 'link', href: s.link, target: '_blank', rel: 'noopener noreferrer' }, 'How to set it up') : null))));
  $('#scrim').hidden = false;
  drawer.hidden = false;
}

function subtaskTree(children) {
  if (!children.length) return h('p', { class: 'muted small' }, 'No subtasks yet.');
  return h('ul', { class: 'subtasks' }, ...children.map((c) => h('li', { class: `sub ${c.state}` },
    h('div', { class: 'sub-row' },
      h('button', { class: 'check-btn', title: c.completedAt ? 'Reopen' : 'Done', 'aria-label': c.completedAt ? `Reopen ${c.title}` : `Complete ${c.title}`, onclick: () => act(post(`/api/items/${c.id}/${c.completedAt ? 'reopen' : 'complete'}`)) }, c.completedAt ? icon('check', { size: 14 }) : null),
      h('button', { class: 'link', onclick: () => openDrawer(c.id) }, c.title),
      ...c.tags.map(tagChip),
      h('span', { class: 'state' }, c.state === 'locked' ? icon('lock', { size: 13 }) : null, stateLabel(c))),
    c.children.length ? subtaskTree(c.children) : null)));
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

function stateLabel(item) {
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

document.addEventListener('keydown', (e) => {
  const typing = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);
  if (e.key === 'Escape') { closeMenus(); if (!$('#drawer').hidden) closeDrawer(); }
  if (e.key === 'n' && !typing) { e.preventDefault(); $('#capture-input').focus(); }
  if (e.key === '/' && !typing) { e.preventDefault(); $('#filter').focus(); }
});

// Filter: one search box with the same syntax as the CLI and agents (#tag, label:x, group:x, is:done, words).
let filterTimer;
$('#filter').addEventListener('input', () => { clearTimeout(filterTimer); filterTimer = setTimeout(() => setQuery($('#filter').value), 250); });
$('#filter').addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.stopPropagation(); setQuery(''); $('#filter').blur(); } });
$('#filter-clear').addEventListener('click', () => setQuery(''));
$('#search-icon').append(icon('search', { size: 16 }));
$('#connect-ai').append(icon('sparkles'));
$('#connect-ai').addEventListener('click', openConnect);

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

document.addEventListener('visibilitychange', () => document.visibilityState === 'visible' && refresh({ background: true }));
setInterval(() => document.visibilityState === 'visible' && refresh({ background: true }), 15000);
refresh().then(() => {
  // Deep link from the sidebar / Teams: /?item=<id> opens that task's details.
  const params = new URLSearchParams(location.search);
  const item = params.get('item');
  if (item && state.dashboard) openDrawer(item);
  else if (params.has('connect') && state.dashboard) openConnect();
});

