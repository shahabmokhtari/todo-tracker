import { connect, connectionLost, createClient, extrasOn, newest, normalizeServerUrl, pageSource, pair } from './lib/client.js';
import { relativeTime, priorityMeta, metaChips, summaryLine } from './lib/format.js';
import { icon } from './lib/icons.js';

// Safari names the API `browser` (with promises); Edge and Chrome name it `chrome`.
const ext = globalThis.browser ?? globalThis.chrome;
// Safari has no side panel: the same page opens as the toolbar button's popup, sized for that.
if (!ext.sidePanel) document.documentElement.classList.add('popup');

const $ = (id) => document.getElementById(id);
let client = null;
let group = null;
let dashboard = null;
let extras = null; // the extras switched on in One Thing (null: unknown, everything shows)
let selectedId = null;
// Once the user starts typing, the note stays bound to that task even if the lists reshuffle.
let pinned = null;
// The connection screen is up: nothing redraws behind it, and it stays until you connect or cancel.
let setupOpen = false;
// The status line says One Thing couldn't be reached: cleared once it answers again (other messages stay).
let unreachable = false;
// Tab switches, actions and the 30 s check can overlap: only the newest answer is shown.
const loadPanel = newest(() => Promise.all([client.dashboard(group), extrasOn(client)]));

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (k === 'class') node.className = v;
    else node.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return node;
}

async function init() {
  const { serverUrl, token, groupId } = await ext.storage.local.get(['serverUrl', 'token', 'groupId']);
  group = groupId || null;
  if (!serverUrl || !token) return showSetup();
  client = createClient({ serverUrl, token });
  await refresh();
}

function showSetup(message = '', { cancel = false } = {}) {
  setupOpen = true;
  $('setup').hidden = false;
  $('main').hidden = true;
  $('setup-error').textContent = message;
  // Cancel goes back to the tasks, so only while there's a connection to go back to.
  $('setup-cancel').hidden = !cancel;
}

function closeSetup() {
  setupOpen = false;
  $('setup').hidden = true;
  $('main').hidden = false;
}

function setStatus(text, { trouble = false } = {}) {
  $('status').textContent = text;
  unreachable = trouble;
}

async function refresh() {
  if (!client) return;
  let reply;
  try {
    reply = await loadPanel();
  } catch (err) {
    if (err.unauthorized) return showSetup(connectionLost());
    if (err.status === 404 && group) {
      group = null;
      await ext.storage.local.remove('groupId');
      return refresh();
    }
    return setStatus(`Can't reach One Thing: ${err.message}`, { trouble: true });
  }
  if (!reply.current) return;
  [dashboard, extras] = reply.value;
  $('feedback').hidden = !extras?.has('feedback');
  if (unreachable) setStatus('');
  if (!setupOpen) closeSetup();
  render();
}

async function run(action, message) {
  try {
    await action();
    setStatus(message ?? '');
  } catch (err) {
    setStatus(err.message);
  }
  await refresh();
}

function render() {
  const d = dashboard;
  $('tabs').replaceChildren(
    tab(null, 'All', d.groups.reduce((n, g) => n + g.now, 0)),
    ...d.groups.map((g) => tab(g.id, g.name, g.now, g.color)));

  if (!d.now.some((c) => c.id === selectedId)) selectedId = d.focus?.id ?? null;
  const f = d.focus;
  $('summary').textContent = summaryLine(d);
  $('focus').replaceChildren(f
    ? card(f, true)
    : el('div', { class: 'hero empty' },
      el('span', { class: 'eyebrow' }, icon('sparkles', { size: 13 }), 'All clear'),
      el('div', { class: 'hero-title' }, 'Nothing is due.'),
      d.waiting[0] ? el('div', { class: 'muted' }, `Next: ${d.waiting[0].title} ${relativeTime(d.waiting[0].wakeAt)}`) : null));
  const selected = d.now.find((c) => c.id === selectedId);
  const target = pinned ?? (selected ? { id: selected.id, title: selected.title } : null);
  $('note').hidden = !target;
  $('note-target').textContent = target ? `Note for “${target.title}”` : '';

  $('now-count').textContent = d.now.length || '';
  const rest = d.now.filter((c) => c.id !== f?.id);
  $('now').replaceChildren(...(rest.length ? rest.map((c) => card(c)) : [el('li', { class: 'empty-row' }, 'Nothing else right now.')]));
  $('waiting-count').textContent = d.waiting.length || '';
  $('waiting').replaceChildren(...d.waiting.map((c) => {
    const li = el('li', { class: 'item' },
      el('span', { class: 'dot' }),
      el('div', { class: 'grow' }, el('div', { class: 'title' }, c.title), el('div', { class: 'chips' }, ...metaChips(c, { waiting: true, plan: planOn() }).map(chip))));
    li.style.setProperty('--prio', priorityMeta(c.priority).color);
    return li;
  }));
}

const chip = (c) => el('span', { class: `chip ${c.tone}` }, c.text);
/** Planned times show while the Plan extra is on (or when the list of extras can't be read). */
const planOn = () => !extras || extras.has('plan');

function tab(id, name, count, color) {
  const button = el('button', {
    class: 'tab', 'aria-pressed': String(group === id),
    onclick: async () => { group = id; await ext.storage.local.set({ groupId: id }); refresh(); },
  }, id ? el('span', { class: 'tab-dot' }) : null, name, count ? el('span', { class: 'badge' }, count) : null);
  if (color) button.style.setProperty('--group', color);
  return button;
}

function card(c, big = false) {
  const selected = c.id === (pinned?.id ?? selectedId);
  const pick = () => { selectedId = c.id; render(); };
  const li = el('li', { class: `item${big ? ' hero' : ''}${c.needsAttention ? ' attention' : ''}${selected ? ' selected' : ''}` },
    big ? null : el('span', { class: 'dot' }),
    el('div', { class: 'grow', onclick: pick },
      big ? el('span', { class: 'eyebrow' }, icon(c.needsAttention ? 'clock' : 'target', { size: 13 }), c.needsAttention ? 'Reminder' : 'Do this now') : null,
      // A button, so the keyboard can pick the task the note goes to as well.
      el('button', {
        class: `pick ${big ? 'hero-title' : 'title'}`, type: 'button', title: 'Select for notes', 'aria-pressed': String(selected), 'data-id': c.id,
        onclick: (e) => {
          e.stopPropagation();
          pick();
          // Redrawn: the keyboard stays on the same task.
          document.querySelector(`button.pick[data-id="${CSS.escape(c.id)}"]`)?.focus();
        },
      }, c.title),
      el('div', { class: 'chips' }, ...metaChips(c, { plan: planOn() }).map(chip)),
      c.needsAttention && c.reminderMessage ? el('div', { class: 'reminder' }, icon('clock', { size: 13 }), c.reminderMessage) : null),
    el('div', { class: 'actions' },
      c.hasChildren ? null : el('button', { class: big ? 'btn primary' : 'icon-btn', title: 'Done', 'aria-label': 'Done', onclick: () => run(() => client.complete(c.id), `Done: ${c.title}`) }, icon('check', { size: 16 }), big ? 'Done' : null),
      el('button', { class: big ? 'btn' : 'icon-btn', title: 'Snooze 1 hour', 'aria-label': 'Snooze 1 hour', onclick: () => run(() => client.snooze(c.id, 60), 'Snoozed 1 hour') }, icon('clock', { size: 16 }), big ? '1h' : null)));
  li.style.setProperty('--prio', priorityMeta(c.priority).color);
  return li;
}
async function currentTab() {
  const [active] = await ext.tabs.query({ active: true, lastFocusedWindow: true });
  return active;
}

async function showPage() {
  const source = pageSource(await currentTab());
  $('page').textContent = source ? source.title : 'This page can’t be attached.';
  $('attach').disabled = !source;
}

$('setup-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    const serverUrl = normalizeServerUrl($('server').value);
    const typed = $('token').value.trim();
    if (!typed && !$('code').value.trim()) throw new Error('Enter the pairing code shown in One Thing.');
    // A pasted token is checked first, so one One Thing doesn't take is never kept.
    if (typed) await connect({ serverUrl, token: typed });
    const token = typed || await pair({ serverUrl, code: $('code').value });
    await ext.storage.local.set({ serverUrl, token });
    $('code').value = '';
    $('token').value = '';
    client = createClient({ serverUrl, token });
    closeSetup();
    await refresh();
    setStatus(typed ? 'Connected' : 'Paired ✓ – this browser now has its own access to your tasks');
  } catch (err) {
    $('setup-error').textContent = err.message;
  }
});

$('setup-cancel').addEventListener('click', () => {
  closeSetup();
  refresh();
});

$('capture').addEventListener('submit', (e) => {
  e.preventDefault();
  const text = $('capture-input').value.trim();
  if (!text) return;
  $('capture-input').value = '';
  run(() => client.capture(text, group), 'Added');
});

$('note-text').addEventListener('input', () => {
  if (!pinned && selectedId) {
    const card = dashboard?.now.find((c) => c.id === selectedId);
    if (card) pinned = { id: card.id, title: card.title };
  }
  if (!$('note-text').value.trim()) pinned = null;
});

$('note').addEventListener('submit', async (e) => {
  e.preventDefault();
  const text = $('note-text').value.trim();
  const targetId = pinned?.id ?? selectedId;
  if (!text || !targetId) return;
  const source = $('attach').checked ? pageSource(await currentTab()) : null;
  try {
    await client.addNote(targetId, text, source);
    $('note-text').value = '';
    pinned = null;
    setStatus('Note saved');
  } catch (err) {
    // Keep the text so nothing typed is lost.
    setStatus(err.message);
  }
  await refresh();
});

$('open').addEventListener('click', () => run(async () => ext.tabs.create({ url: await client.launchUrl('/') })));
// The feedback sheet of the app (one sheet for every app): a bug, an idea, anything. Shown only while that extra is on.
$('feedback').addEventListener('click', () => run(async () => ext.tabs.create({ url: await client.launchUrl('/?feedback=button') })));
$('settings').addEventListener('click', async () => {
  const { serverUrl } = await ext.storage.local.get('serverUrl');
  $('server').value = serverUrl || 'http://127.0.0.1:5317';
  showSetup('', { cancel: !!client });
});

$('open').append(icon('external', { size: 17 }));
$('feedback').append(icon('chat', { size: 17 }));
$('settings').append(icon('settings', { size: 17 }));
ext.tabs.onActivated.addListener(showPage);
ext.tabs.onUpdated.addListener((_, info) => info.title && showPage());
// Not while the connection screen is up: it would close it (or rewrite its message) under you.
setInterval(() => { if (client && !setupOpen) refresh(); }, 30000);
showPage();
init();
