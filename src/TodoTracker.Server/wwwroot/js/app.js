import { api, post, patch, put, del, h, ApiError } from './api.js';
import { relativeTime, priorityMeta, snoozeOptions, progressPercent, stepLabel, isSafeHttpUrl } from './format.js';

const $ = (sel) => document.querySelector(sel);
const state = {
  dashboard: null,
  group: localStorage.getItem('tt.group') || null,
  drawerId: null,
};

// ---------- boot & refresh ----------

async function refresh({ background = false } = {}) {
  // Background refreshes never interrupt typing: an open note box or a focused field keeps its content.
  if (background && isEditing()) return;
  try {
    const query = state.group ? `?group=${encodeURIComponent(state.group)}` : '';
    state.dashboard = await api(`/api/dashboard${query}`);
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
    toast(err.message);
  }
}

function isEditing() {
  const active = document.activeElement;
  if (active && ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName) && active.closest('#board, #drawer')) return true;
  return [...document.querySelectorAll('.inline-note:not([hidden]) input')].some((i) => i.value.trim());
}

function collectNoteDrafts() {
  const drafts = new Map();
  document.querySelectorAll('.inline-note:not([hidden])').forEach((form) => drafts.set(form.dataset.id, form.querySelector('input').value));
  return drafts;
}

function restoreNoteDrafts(drafts) {
  drafts.forEach((value, id) => {
    const form = document.querySelector(`.inline-note[data-id="${CSS.escape(id)}"]`);
    if (!form) return;
    form.hidden = false;
    form.querySelector('input').value = value;
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
    toast(err.message);
  }
  await refresh();
  if (state.drawerId) await openDrawer(state.drawerId, { keepEdits: true });
  return ok;
}

function toast(message) {
  const el = $('#toast');
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (el.hidden = true), 3500);
}

function setGroup(id) {
  state.group = id;
  if (id) localStorage.setItem('tt.group', id);
  else localStorage.removeItem('tt.group');
}

// ---------- rendering ----------

function render() {
  const d = state.dashboard;
  renderTabs(d);
  renderFocus(d);
  renderSection('#sec-now', d.now.filter((c) => c.id !== d.focus?.id).map((c) => card(c)), d.now.length);
  renderSection('#sec-waiting', d.waiting.map((c) => card(c, { waiting: true })), d.waiting.length);
  renderSection('#sec-overview', d.overview.map(workstream), d.overview.length);
  renderSection('#sec-notes', d.recentNotes.map(noteRow), d.recentNotes.length);
  renderPomodoro(d.pomodoro);
  document.title = d.now.length ? `(${d.now.length}) Todo Tracker` : 'Todo Tracker';
}

function renderTabs(d) {
  const total = d.groups.reduce((n, g) => n + g.now, 0);
  const attention = d.groups.some((g) => g.attention > 0);
  const tab = (id, name, count, color, alert) =>
    h('button', {
      role: 'tab',
      class: 'tab',
      'aria-selected': String((state.group || null) === id),
      onclick: () => { setGroup(id); refresh(); },
      ondblclick: id ? () => editGroup(d.groups.find((g) => g.id === id)) : undefined,
      title: id ? 'Double-click to rename or delete' : 'All groups',
      dataset: color ? { color } : undefined,
    }, name, count ? h('span', { class: `badge${alert ? ' alert' : ''}` }, count) : null);

  const tabs = [tab(null, 'All', total, null, attention), ...d.groups.map((g) => tab(g.id, g.name, g.now, g.color, g.attention > 0))];
  tabs.forEach((t) => t.dataset.color && t.style.setProperty('--group', t.dataset.color));
  $('#tabs').replaceChildren(...tabs, h('button', { class: 'tab add', title: 'Add group', 'aria-label': 'Add group', onclick: addGroup }, '+'));
}

function renderFocus(d) {
  const f = d.focus;
  if (!f) {
    const next = d.waiting[0];
    $('#focus').replaceChildren(h('div', { class: 'focus-card empty' },
      h('div', { class: 'eyebrow' }, 'Do this now'),
      h('div', { class: 'focus-title' }, 'Nothing is due. Enjoy the calm ✨'),
      next ? h('div', { class: 'meta' }, `Next: ${next.title} ${relativeTime(next.wakeAt)}`) : null));
    return;
  }

  const el = h('div', { class: `focus-card${f.needsAttention ? ' attention' : ''}` },
    h('div', { class: 'eyebrow' }, f.needsAttention ? '⏰ Reminder' : 'Do this now'),
    h('button', { class: 'focus-title link', onclick: () => openDrawer(f.id) }, f.title),
    metaLine(f),
    f.needsAttention && f.reminderMessage ? h('div', { class: 'reminder' }, f.reminderMessage) : null,
    f.lastNote ? h('div', { class: 'last-note' }, `Last note: ${f.lastNote}`) : null,
    actions(f, { big: true }));
  el.style.setProperty('--prio', priorityMeta(f.priority).color);
  $('#focus').replaceChildren(el);
}

function renderSection(selector, rows, count) {
  const section = $(selector);
  section.querySelector('.count').textContent = count ? String(count) : '';
  section.querySelector('.list').replaceChildren(...(rows.length ? rows : [h('li', { class: 'empty' }, 'Nothing here')]));
  const key = `tt.open.${section.id}`;
  if (!section.dataset.bound) {
    section.dataset.bound = '1';
    const saved = localStorage.getItem(key);
    if (saved !== null) section.open = saved === '1';
    section.addEventListener('toggle', () => localStorage.setItem(key, section.open ? '1' : '0'));
  }
}

function metaLine(c, { waiting = false } = {}) {
  const bits = [];
  if (c.breadcrumb?.length) bits.push(c.breadcrumb.join(' › '));
  const step = stepLabel(c);
  if (step) bits.push(step);
  if (waiting && c.wakeAt) bits.push(`back ${relativeTime(c.wakeAt)}`);
  if (c.deadline) bits.push(c.isOverdue ? `overdue ${relativeTime(c.deadline)}` : `due ${relativeTime(c.deadline)}`);
  if (c.noteCount) bits.push(`${c.noteCount} note${c.noteCount > 1 ? 's' : ''}`);
  return h('div', { class: `meta${c.isOverdue ? ' overdue' : ''}` }, bits.join(' · '));
}

function card(c, { waiting = false } = {}) {
  const li = h('li', { class: `item${c.needsAttention ? ' attention' : ''}` },
    h('span', { class: 'prio', title: priorityMeta(c.priority).label }),
    h('div', { class: 'body' },
      h('button', { class: 'title link', onclick: () => openDrawer(c.id) }, c.title),
      metaLine(c, { waiting }),
      c.needsAttention && c.reminderMessage ? h('div', { class: 'reminder' }, `⏰ ${c.reminderMessage}`) : null),
    actions(c, { waiting }));
  li.style.setProperty('--prio', priorityMeta(c.priority).color);
  return li;
}

function actions(c, { waiting = false, big = false } = {}) {
  const bar = h('div', { class: `actions${big ? ' big' : ''}` });
  const noteBox = h('form', { class: 'inline-note', hidden: true, dataset: { id: c.id }, onsubmit: async (e) => {
    e.preventDefault();
    const input = noteBox.querySelector('input');
    const text = input.value;
    if (!text.trim()) return;
    input.value = '';
    noteBox.hidden = true;
    if (!(await act(post(`/api/items/${c.id}/notes`, { text }), 'Note saved'))) restoreNoteDrafts(new Map([[c.id, text]]));
  } }, h('input', { placeholder: 'What did you do? What is next?', maxlength: 10000, 'aria-label': 'Note' }), h('button', { type: 'submit' }, 'Save'));

  if (!waiting && !c.hasChildren) bar.append(h('button', { class: 'ok', title: 'Done', onclick: () => act(post(`/api/items/${c.id}/complete`), `Done: ${c.title}`) }, big ? '✓ Done' : '✓'));
  if (c.needsAttention && c.reminderId) bar.append(h('button', { title: 'Dismiss reminder', onclick: () => act(post(`/api/items/${c.id}/reminders/${c.reminderId}/dismiss`)) }, big ? 'Dismiss' : '🔕'));
  if (waiting) bar.append(h('button', { title: 'Bring back now', onclick: () => act(post(`/api/items/${c.id}/schedule`, { clear: true })) }, big ? 'Do now' : '↩'));
  bar.append(snoozeButton(c, big));
  bar.append(h('button', { title: 'Add note', onclick: () => { noteBox.hidden = !noteBox.hidden; noteBox.querySelector('input').focus(); } }, big ? '✎ Note' : '✎'));
  if (!waiting) bar.append(h('button', { title: 'Start focus timer', onclick: () => act(post('/api/pomodoro/start', { itemId: c.id }), 'Focus started') }, big ? '▶ Focus' : '▶'));
  return h('div', { class: 'action-wrap' }, bar, noteBox);
}

function snoozeButton(c, big) {
  const menu = h('div', { class: 'menu', hidden: true, role: 'menu' },
    ...snoozeOptions().map((o) => h('button', {
      role: 'menuitem',
      onclick: () => act(post(`/api/items/${c.id}/schedule`, { inMinutes: o.minutes, notify: true }), `Snoozed until ${o.label.toLowerCase()}`),
    }, o.label)));
  const button = h('button', { title: 'Snooze (remind me later)', 'aria-haspopup': 'menu', onclick: (e) => { e.stopPropagation(); closeMenus(); menu.hidden = !menu.hidden; } }, big ? '⏰ Later' : '⏰');
  return h('span', { class: 'menu-wrap' }, button, menu);
}

function closeMenus() {
  document.querySelectorAll('.menu').forEach((m) => (m.hidden = true));
}
document.addEventListener('click', closeMenus);

function workstream(o) {
  const pct = progressPercent(o.doneLeaves, o.totalLeaves);
  const bar = h('div', { class: 'progress', role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('span'));
  bar.firstChild.style.width = `${pct}%`;
  const bits = [`${o.doneLeaves}/${o.totalLeaves} done`];
  if (o.actionableCount) bits.push(`${o.actionableCount} now`);
  if (o.waitingCount) bits.push(`${o.waitingCount} waiting`);
  if (o.nextWakeAt) bits.push(`next ${relativeTime(o.nextWakeAt)}`);
  const li = h('li', { class: 'item' },
    h('span', { class: 'prio' }),
    h('div', { class: 'body' },
      h('button', { class: 'title link', onclick: () => openDrawer(o.id) }, o.title),
      bar,
      h('div', { class: 'meta' }, bits.join(' · '))),
    h('a', { class: 'report-link', href: `report.html?id=${o.id}`, title: 'Full report' }, '📄'));
  li.style.setProperty('--prio', priorityMeta(o.priority).color);
  return li;
}

function noteRow(n) {
  return h('li', { class: 'note' },
    h('div', null, n.text),
    h('div', { class: 'meta' },
      h('button', { class: 'link', onclick: () => openDrawer(n.itemId) }, n.itemTitle),
      ` · ${n.author} · ${relativeTime(n.at)}`,
      isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer' }, ` · ${n.sourceTitle || 'source'}`) : null));
}

// ---------- pomodoro ----------

function renderPomodoro(p) {
  const label = { idle: 'Focus', focus: 'Focus', shortBreak: 'Break', longBreak: 'Long break' }[p.phase] ?? p.phase;
  const time = h('span', { class: 'time', id: 'pomo-time' }, clock(p));
  const buttons = [];
  if (p.phase === 'idle') buttons.push(h('button', { onclick: () => act(post('/api/pomodoro/start', { itemId: state.dashboard.focus?.id })) }, '▶'));
  else {
    buttons.push(p.running
      ? h('button', { title: 'Pause', onclick: () => act(post('/api/pomodoro/pause')) }, '⏸')
      : h('button', { title: 'Resume', onclick: () => act(post('/api/pomodoro/resume')) }, '▶'));
    buttons.push(h('button', { title: 'Skip', onclick: () => act(post('/api/pomodoro/skip')) }, '⏭'));
    buttons.push(h('button', { title: 'Reset', onclick: () => act(post('/api/pomodoro/reset')) }, '⟲'));
  }
  const item = p.itemTitle && p.phase !== 'idle' ? [h('span', { class: 'pomo-item' }, p.itemTitle)] : [];
  $('#pomodoro').replaceChildren(h('span', { class: `phase ${p.phase}` }, `🍅 ${label}`), time, ...buttons, ...item);
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

async function openDrawer(id, { keepEdits = false } = {}) {
  const edits = keepEdits && $('#drawer').dataset.itemId === id ? collectDrawerEdits() : new Map();
  state.drawerId = id;
  let item;
  try {
    item = await api(`/api/items/${id}`);
  } catch (err) {
    state.drawerId = null;
    $('#drawer').hidden = true;
    return toast(err.message);
  }
  const drawer = $('#drawer');
  const close = () => { state.drawerId = null; drawer.hidden = true; };
  const field = (label, control) => h('label', { class: 'field' }, h('span', null, label), control);

  const title = h('input', { value: item.title, maxlength: 300, dataset: { field: 'title' } });
  const priority = h('select', { dataset: { field: 'priority' } }, ...['low', 'normal', 'high', 'critical'].map((p) => h('option', { value: p, selected: p === item.priority }, priorityMeta(p).label)));
  const deadline = h('input', { type: 'datetime-local', value: toLocalInput(item.deadline), dataset: { field: 'deadline' } });
  const details = h('textarea', { rows: 3, maxlength: 10000, placeholder: 'Details, links, context…', dataset: { field: 'details' } }, item.details ?? '');
  const sequential = h('input', { type: 'checkbox', checked: item.sequential, dataset: { field: 'sequential' } });
  const delay = h('input', { type: 'number', min: 0, step: 1, value: item.stepDelayMinutes ? item.stepDelayMinutes / 60 : '', placeholder: 'hours (empty = none)', dataset: { field: 'delay' } });
  const group = h('select', { disabled: !!item.parentId, dataset: { field: 'group' } }, ...state.dashboard.groups.map((g) => h('option', { value: g.id, selected: g.id === item.groupId }, g.name)));

  const save = () => act((async () => {
    await patch(`/api/items/${id}`, {
      title: title.value,
      details: details.value,
      priority: priority.value,
      deadline: deadline.value ? new Date(deadline.value).toISOString() : null,
      clearDeadline: !deadline.value,
      sequential: sequential.checked,
      stepDelayMinutes: Number(delay.value) > 0 ? Math.round(Number(delay.value) * 60) : null,
      clearStepDelay: !(Number(delay.value) > 0),
    });
    if (!item.parentId && group.value !== item.groupId) await post(`/api/items/${id}/move`, { groupId: group.value });
  })(), 'Saved');

  const subtaskInput = h('input', { placeholder: 'Add a subtask…', maxlength: 300, dataset: { field: 'subtask' } });
  const stepsInput = h('textarea', { rows: 3, placeholder: 'Rollout steps, one per line' });
  const stepDelay = h('input', { type: 'number', min: 0, value: 24, 'aria-label': 'Hours between steps' });
  const noteInput = h('textarea', { rows: 2, placeholder: 'Note: what happened, what is next…', maxlength: 10000, dataset: { field: 'note' } });
  const remindIn = h('select', null, ...snoozeOptions().map((o) => h('option', { value: o.minutes }, o.label)));
  const remindMsg = h('input', { placeholder: 'Reminder message (optional)', maxlength: 300 });

  drawer.replaceChildren(
    h('div', { class: 'drawer-head' },
      h('div', { class: 'crumbs' }, item.path.slice(0, -1).join(' › ')),
      h('button', { class: 'close', 'aria-label': 'Close', onclick: close }, '✕')),
    field('Title', title),
    h('div', { class: 'row' }, field('Priority', priority), field('Deadline', deadline)),
    field('Details', details),
    h('div', { class: 'row' }, h('label', { class: 'check' }, sequential, ' Steps in order'), field('Hours between steps', delay)),
    field('Group', group),
    h('div', { class: 'row buttons' },
      h('button', { class: 'primary', onclick: save }, 'Save'),
      item.completedAt
        ? h('button', { onclick: () => act(post(`/api/items/${id}/reopen`), 'Reopened') }, 'Reopen')
        : h('button', { class: 'ok', onclick: () => {
          const open = item.children.filter((c) => !c.completedAt).length;
          if (open && !confirm(`Also mark ${open} open subtask${open > 1 ? 's' : ''} as done?`)) return;
          act(post(`/api/items/${id}/complete`), 'Done');
        } }, '✓ Done'),
      h('a', { class: 'button', href: `report.html?id=${id}`, target: '_blank', rel: 'noopener' }, 'Full report'),
      h('button', { class: 'danger', onclick: () => { if (confirm(`Delete "${item.title}" and all its subtasks?`)) { close(); act(del(`/api/items/${id}`), 'Deleted'); } } }, 'Delete')),

    h('h3', null, `Subtasks${item.sequential ? ' (in order)' : ''}`),
    h('ul', { class: 'subtasks' }, ...item.children.map((c) => h('li', { class: `sub ${c.state}` },
      h('button', { class: 'check-btn', title: c.completedAt ? 'Reopen' : 'Done', onclick: () => act(post(`/api/items/${c.id}/${c.completedAt ? 'reopen' : 'complete'}`)) }, c.completedAt ? '☑' : '☐'),
      h('button', { class: 'link', onclick: () => openDrawer(c.id) }, c.title),
      h('span', { class: 'state' }, stateLabel(c))))),
    h('form', { class: 'row', onsubmit: (e) => { e.preventDefault(); submitAndClear(subtaskInput, (title) => post('/api/items', { title, parentId: id })); } }, subtaskInput, h('button', { type: 'submit' }, 'Add')),
    h('details', null, h('summary', null, 'Add rollout steps'),
      stepsInput,
      h('div', { class: 'row' }, field('Hours between steps', stepDelay),
        h('button', { onclick: () => {
          const titles = stepsInput.value.split('\n').map((s) => s.trim()).filter(Boolean);
          if (titles.length) act(post(`/api/items/${id}/steps`, { titles, stepDelayMinutes: Math.round(Number(stepDelay.value || 0) * 60) || null }), `Added ${titles.length} steps`);
        } }, 'Add steps'))),

    h('h3', null, 'Reminders'),
    h('ul', { class: 'reminders' }, ...item.reminders.filter((r) => !r.dismissedAt).map((r) => h('li', null,
      `${r.message} · ${relativeTime(r.dueAt)}`,
      h('button', { class: 'link', onclick: () => act(post(`/api/items/${id}/reminders/${r.id}/dismiss`)) }, 'dismiss')))),
    h('div', { class: 'row' }, remindIn, remindMsg, h('button', { onclick: () => act(post(`/api/items/${id}/reminders`, { inMinutes: Number(remindIn.value), message: remindMsg.value || null }), 'Reminder set') }, 'Remind me')),

    h('h3', null, 'Notes'),
    h('form', { class: 'note-form', onsubmit: (e) => { e.preventDefault(); submitAndClear(noteInput, (text) => post(`/api/items/${id}/notes`, { text }), 'Note saved'); } }, noteInput, h('button', { type: 'submit' }, 'Add note')),
    h('ul', { class: 'notes' }, ...item.notes.map((n) => h('li', null, h('div', null, n.text), h('div', { class: 'meta' }, `${n.author} · ${relativeTime(n.at)}`,
      isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer' }, ` · ${n.sourceTitle || 'source'}`) : null)))));
  drawer.dataset.itemId = id;
  drawer.querySelectorAll('[data-field]').forEach((el) => { el.dataset.original = fieldValue(el); });
  edits.forEach((value, name) => {
    const el = drawer.querySelector(`[data-field="${name}"]`);
    if (el) setFieldValue(el, value);
  });
  drawer.hidden = false;
}

const fieldValue = (el) => (el.type === 'checkbox' ? String(el.checked) : el.value);
const setFieldValue = (el, v) => { if (el.type === 'checkbox') el.checked = v === 'true'; else el.value = v; };

/** Unsaved drawer edits (fields that differ from what the server sent), so a rebuild never discards typing. */
function collectDrawerEdits() {
  const edits = new Map();
  $('#drawer').querySelectorAll('[data-field]').forEach((el) => {
    if (fieldValue(el) !== el.dataset.original) edits.set(el.dataset.field, fieldValue(el));
  });
  return edits;
}

/** Clears an input optimistically and puts the text back if the request fails. */
async function submitAndClear(input, request, message) {
  const value = input.value;
  if (!value.trim()) return;
  input.value = '';
  input.dataset.original = '';
  if (!(await act(request(value), message))) {
    const el = $('#drawer').querySelector(`[data-field="${input.dataset.field}"]`);
    if (el) el.value = value;
  }
}

function stateLabel(item) {
  if (item.state === 'waiting' && item.nextActionAt) return `waiting · ${relativeTime(item.nextActionAt)}`;
  return { actionable: 'now', container: 'in progress', locked: '🔒 later', done: 'done', waiting: 'waiting' }[item.state] ?? item.state;
}

function toLocalInput(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// ---------- forms & keyboard ----------

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
  if (e.key === 'Escape') { closeMenus(); $('#drawer').hidden = true; state.drawerId = null; }
  if (e.key === 'n' && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) { e.preventDefault(); $('#capture-input').focus(); }
});

document.addEventListener('visibilitychange', () => document.visibilityState === 'visible' && refresh({ background: true }));
setInterval(() => document.visibilityState === 'visible' && refresh({ background: true }), 15000);
refresh().then(() => {
  // Deep link from the sidebar / Teams: /?item=<id> opens that task's details.
  const item = new URLSearchParams(location.search).get('item');
  if (item && state.dashboard) openDrawer(item);
});

