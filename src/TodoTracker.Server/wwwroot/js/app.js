import { api, post, patch, put, del, h, ApiError } from './api.js';
import { relativeTime, priorityMeta, snoozeOptions, progressPercent, stepLabel, isSafeHttpUrl, greeting, metaChips, summaryLine, pomodoroFraction } from './format.js';
import { icon, ring } from './icons.js';

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
    toast(err.message, 'error');
  }
}

/** True only when something unsaved would be lost: typed capture text, a non-empty note, or dirty drawer fields. */
function isEditing() {
  if ($('#capture-input').value.trim()) return true;
  if ([...document.querySelectorAll('.inline-note:not([hidden]) input')].some((i) => i.value.trim())) return true;
  return !$('#drawer').hidden && collectDrawerEdits().size > 0;
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
  if (state.drawerId) await openDrawer(state.drawerId, { keepEdits: true });
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
  document.title = d.now.length ? `(${d.now.length}) Todo Tracker` : 'Todo Tracker';
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
  if (chips.length) sub.push(h('div', { class: 'meta' }, ...chips.map(chip)));

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
      h('div', { class: 'meta' }, ...metaChips(c, { waiting }).map(chip)),
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
  const noteBox = h('form', { class: 'inline-note', hidden: true, dataset: { id: c.id }, onsubmit: async (e) => {
    e.preventDefault();
    const input = noteBox.querySelector('input');
    const text = input.value;
    if (!text.trim()) return;
    input.value = '';
    noteBox.hidden = true;
    if (!(await act(post(`/api/items/${c.id}/notes`, { text }), 'Note saved'))) restoreNoteDrafts(new Map([[c.id, text]]));
  } }, h('input', { placeholder: 'What did you do? What is next?', maxlength: 10000, 'aria-label': 'Note' }), h('button', { class: 'btn primary', type: 'submit' }, 'Save'));

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

function closeDrawer() {
  state.drawerId = null;
  $('#drawer').hidden = true;
  $('#scrim').hidden = true;
}

$('#scrim').addEventListener('click', closeDrawer);

async function openDrawer(id, { keepEdits = false } = {}) {
  const edits = keepEdits && $('#drawer').dataset.itemId === id ? collectDrawerEdits() : new Map();
  state.drawerId = id;
  let item;
  try {
    item = await api(`/api/items/${id}`);
  } catch (err) {
    closeDrawer();
    return toast(err.message, 'error');
  }
  const drawer = $('#drawer');
  const close = closeDrawer;
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

  const section = (title, ...children) => h('section', { class: 'drawer-section' }, h('h3', null, title), ...children);
  title.classList.add('title-input');
  title.setAttribute('aria-label', 'Title');
  const meta = priorityMeta(item.priority);
  drawer.style.setProperty('--prio', meta.color);

  drawer.replaceChildren(
    h('div', { class: 'drawer-head' },
      h('span', { class: 'prio-pill' }, meta.label),
      h('span', { class: 'crumbs' }, item.path.slice(0, -1).join(' › ')),
      h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onclick: close }, icon('x'))),
    title,
    h('div', { class: 'row' }, field('Priority', priority), field('Deadline', deadline)),
    field('Details', details),
    h('div', { class: 'row' }, h('label', { class: 'check' }, sequential, 'Steps in order'), field('Hours between steps', delay)),
    field('Group', group),
    h('div', { class: 'row buttons' },
      h('button', { class: 'btn primary', onclick: save }, 'Save'),
      item.completedAt
        ? h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reopen`), 'Reopened') }, icon('undo', { size: 16 }), 'Reopen')
        : h('button', { class: 'btn success', onclick: () => {
          const open = item.children.filter((c) => !c.completedAt).length;
          if (open && !confirm(`Also mark ${open} open subtask${open > 1 ? 's' : ''} as done?`)) return;
          act(post(`/api/items/${id}/complete`), 'Done');
        } }, icon('check', { size: 16 }), 'Done'),
      h('a', { class: 'btn ghost', href: `report.html?id=${id}`, target: '_blank', rel: 'noopener' }, icon('report', { size: 16 }), 'Full report'),
      h('button', { class: 'btn ghost danger', onclick: () => { if (confirm(`Delete "${item.title}" and all its subtasks?`)) { close(); act(del(`/api/items/${id}`), 'Deleted'); } } }, icon('trash', { size: 16 }), 'Delete')),

    section(`Subtasks${item.sequential ? ' · in order' : ''}`,
      h('ul', { class: 'subtasks' }, ...item.children.map((c) => h('li', { class: `sub ${c.state}` },
        h('button', { class: 'check-btn', title: c.completedAt ? 'Reopen' : 'Done', 'aria-label': c.completedAt ? `Reopen ${c.title}` : `Complete ${c.title}`, onclick: () => act(post(`/api/items/${c.id}/${c.completedAt ? 'reopen' : 'complete'}`)) }, c.completedAt ? icon('check', { size: 14 }) : null),
        h('button', { class: 'link', onclick: () => openDrawer(c.id) }, c.title),
        h('span', { class: 'state' }, c.state === 'locked' ? icon('lock', { size: 13 }) : null, stateLabel(c))))),
      h('form', { class: 'row', onsubmit: (e) => { e.preventDefault(); submitAndClear(subtaskInput, (t) => post('/api/items', { title: t, parentId: id })); } }, subtaskInput, h('button', { class: 'btn', type: 'submit' }, icon('plus', { size: 16 }), 'Add')),
      h('details', null, h('summary', null, 'Add rollout steps'),
        stepsInput,
        h('div', { class: 'row' }, field('Hours between steps', stepDelay),
          h('button', { class: 'btn', onclick: () => {
            const titles = stepsInput.value.split('\n').map((s) => s.trim()).filter(Boolean);
            if (titles.length) act(post(`/api/items/${id}/steps`, { titles, stepDelayMinutes: Math.round(Number(stepDelay.value || 0) * 60) || null }), `Added ${titles.length} steps`);
          } }, icon('layers', { size: 16 }), 'Add steps')))),

    section('Reminders',
      h('ul', { class: 'reminders' }, ...item.reminders.filter((r) => !r.dismissedAt).map((r) => h('li', null,
        h('span', { class: 'author' }, icon('clock', { size: 14 }), `${r.message} · ${relativeTime(r.dueAt)}`),
        h('button', { class: 'link', onclick: () => act(post(`/api/items/${id}/reminders/${r.id}/dismiss`)) }, 'Dismiss')))),
      h('div', { class: 'row' }, remindIn, remindMsg, h('button', { class: 'btn', onclick: () => act(post(`/api/items/${id}/reminders`, { inMinutes: Number(remindIn.value), message: remindMsg.value || null }), 'Reminder set') }, 'Remind me'))),

    section('Notes',
      h('form', { class: 'note-form', onsubmit: (e) => { e.preventDefault(); submitAndClear(noteInput, (text) => post(`/api/items/${id}/notes`, { text }), 'Note saved'); } }, noteInput, h('button', { class: 'btn primary', type: 'submit' }, 'Add note')),
      h('ul', { class: 'notes' }, ...item.notes.map((n) => h('li', null, h('div', { class: 'note-text' }, n.text), h('div', { class: 'meta' },
        h('span', { class: 'author' }, avatar(n.authorKind, n.author), n.author), h('span', null, '·'), h('span', null, relativeTime(n.at)),
        isSafeHttpUrl(n.sourceUrl) ? h('a', { href: n.sourceUrl, target: '_blank', rel: 'noopener noreferrer', class: 'author' }, icon('link', { size: 13 }), n.sourceTitle || 'source') : null))))));
  $('#scrim').hidden = false;
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
  if (e.key === 'Escape') { closeMenus(); closeDrawer(); }
  if (e.key === 'n' && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) { e.preventDefault(); $('#capture-input').focus(); }
});

document.addEventListener('visibilitychange', () => document.visibilityState === 'visible' && refresh({ background: true }));
setInterval(() => document.visibilityState === 'visible' && refresh({ background: true }), 15000);
refresh().then(() => {
  // Deep link from the sidebar / Teams: /?item=<id> opens that task's details.
  const item = new URLSearchParams(location.search).get('item');
  if (item && state.dashboard) openDrawer(item);
});

