import { createClient, normalizeServerUrl, pageSource } from './lib/client.js';
import { relativeTime, priorityMeta, stepLabel } from './lib/format.js';

const $ = (id) => document.getElementById(id);
let client = null;
let group = null;
let dashboard = null;
let selectedId = null;
// Once the user starts typing, the note stays bound to that task even if the lists reshuffle.
let pinned = null;

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
  const { serverUrl, token, groupId } = await chrome.storage.local.get(['serverUrl', 'token', 'groupId']);
  group = groupId || null;
  if (!serverUrl || !token) return showSetup();
  client = createClient({ serverUrl, token });
  await refresh();
}

function showSetup(message = '') {
  $('setup').hidden = false;
  $('main').hidden = true;
  $('setup-error').textContent = message;
}

async function refresh() {
  try {
    dashboard = await client.dashboard(group);
    $('setup').hidden = true;
    $('main').hidden = false;
    render();
  } catch (err) {
    if (err.unauthorized) return showSetup('The token was rejected. Copy it again from the sidebar menu.');
    if (err.status === 404 && group) {
      group = null;
      await chrome.storage.local.remove('groupId');
      return refresh();
    }
    $('status').textContent = `Can't reach Todo Tracker: ${err.message}`;
  }
}

async function run(action, message) {
  try {
    await action();
    $('status').textContent = message ?? '';
  } catch (err) {
    $('status').textContent = err.message;
  }
  await refresh();
}

function render() {
  const d = dashboard;
  $('tabs').replaceChildren(
    tab(null, 'All', d.groups.reduce((n, g) => n + g.now, 0)),
    ...d.groups.map((g) => tab(g.id, g.name, g.now)));

  if (!d.now.some((c) => c.id === selectedId)) selectedId = d.focus?.id ?? null;
  const f = d.focus;
  $('focus').replaceChildren(f
    ? card(f, true)
    : el('div', { class: 'empty' }, 'Nothing is due ✨', d.waiting[0] ? el('div', { class: 'muted' }, `Next: ${d.waiting[0].title} ${relativeTime(d.waiting[0].wakeAt)}`) : null));
  const selected = d.now.find((c) => c.id === selectedId);
  const target = pinned ?? (selected ? { id: selected.id, title: selected.title } : null);
  $('note').hidden = !target;
  $('note-target').textContent = target ? `Note for: ${target.title}` : '';

  $('now-count').textContent = d.now.length || '';
  $('now').replaceChildren(...d.now.filter((c) => c.id !== f?.id).map((c) => card(c)));
  $('waiting-count').textContent = d.waiting.length || '';
  $('waiting').replaceChildren(...d.waiting.map((c) => el('li', { class: 'item' },
    el('span', { class: 'bar', style: `background:${priorityMeta(c.priority).color}` }),
    el('div', null, el('div', { class: 'title' }, c.title), el('div', { class: 'muted' }, `back ${relativeTime(c.wakeAt)}`)))));
}

function tab(id, name, count) {
  return el('button', {
    class: 'tab', 'aria-pressed': String(group === id),
    onclick: async () => { group = id; await chrome.storage.local.set({ groupId: id }); refresh(); },
  }, name, count ? el('span', { class: 'badge' }, count) : null);
}

function card(c, big = false) {
  const meta = [c.breadcrumb?.join(' › '), stepLabel(c), c.deadline ? `due ${relativeTime(c.deadline)}` : null].filter(Boolean).join(' · ');
  return el('li', { class: `item${big ? ' big' : ''}${c.needsAttention ? ' attention' : ''}${c.id === selectedId ? ' selected' : ''}` },
    el('span', { class: 'bar', style: `background:${priorityMeta(c.priority).color}` }),
    el('div', { class: 'grow', onclick: () => { selectedId = c.id; render(); }, title: 'Select for notes' },
      el('div', { class: 'title' }, c.title),
      meta ? el('div', { class: 'muted' }, meta) : null,
      c.needsAttention && c.reminderMessage ? el('div', { class: 'reminder' }, `⏰ ${c.reminderMessage}`) : null),
    el('div', { class: 'actions' },
      c.hasChildren ? null : el('button', { title: 'Done', onclick: () => run(() => client.complete(c.id), `Done: ${c.title}`) }, '✓'),
      el('button', { title: 'Snooze 1 hour', onclick: () => run(() => client.snooze(c.id, 60), 'Snoozed 1 hour') }, '⏰')));
}

async function currentTab() {
  const [active] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
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
    const token = $('token').value.trim();
    await chrome.storage.local.set({ serverUrl, token });
    client = createClient({ serverUrl, token });
    await refresh();
  } catch (err) {
    $('setup-error').textContent = err.message;
  }
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
    $('status').textContent = 'Note saved';
  } catch (err) {
    // Keep the text so nothing typed is lost.
    $('status').textContent = err.message;
  }
  await refresh();
});

$('open').addEventListener('click', () => run(async () => chrome.tabs.create({ url: await client.launchUrl('/') })));
$('settings').addEventListener('click', async () => {
  const { serverUrl } = await chrome.storage.local.get('serverUrl');
  $('server').value = serverUrl || 'http://127.0.0.1:5317';
  showSetup();
});

chrome.tabs.onActivated.addListener(showPage);
chrome.tabs.onUpdated.addListener((_, info) => info.title && showPage());
setInterval(() => client && refresh(), 30000);
showPage();
init();
