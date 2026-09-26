import { createClient, normalizeServerUrl, pageSource } from './lib/client.js';
import { relativeTime, priorityMeta, metaChips, summaryLine } from './lib/format.js';
import { icon } from './lib/icons.js';

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
      el('div', { class: 'grow' }, el('div', { class: 'title' }, c.title), el('div', { class: 'chips' }, ...metaChips(c, { waiting: true }).map(chip))));
    li.style.setProperty('--prio', priorityMeta(c.priority).color);
    return li;
  }));
}

const chip = (c) => el('span', { class: `chip ${c.tone}` }, c.text);

function tab(id, name, count, color) {
  const button = el('button', {
    class: 'tab', 'aria-pressed': String(group === id),
    onclick: async () => { group = id; await chrome.storage.local.set({ groupId: id }); refresh(); },
  }, id ? el('span', { class: 'tab-dot' }) : null, name, count ? el('span', { class: 'badge' }, count) : null);
  if (color) button.style.setProperty('--group', color);
  return button;
}

function card(c, big = false) {
  const li = el('li', { class: `item${big ? ' hero' : ''}${c.needsAttention ? ' attention' : ''}${c.id === (pinned?.id ?? selectedId) ? ' selected' : ''}` },
    big ? null : el('span', { class: 'dot' }),
    el('div', { class: 'grow', onclick: () => { selectedId = c.id; render(); }, title: 'Select for notes' },
      big ? el('span', { class: 'eyebrow' }, icon(c.needsAttention ? 'clock' : 'target', { size: 13 }), c.needsAttention ? 'Reminder' : 'Do this now') : null,
      el('div', { class: big ? 'hero-title' : 'title' }, c.title),
      el('div', { class: 'chips' }, ...metaChips(c).map(chip)),
      c.needsAttention && c.reminderMessage ? el('div', { class: 'reminder' }, icon('clock', { size: 13 }), c.reminderMessage) : null),
    el('div', { class: 'actions' },
      c.hasChildren ? null : el('button', { class: big ? 'btn primary' : 'icon-btn', title: 'Done', 'aria-label': 'Done', onclick: () => run(() => client.complete(c.id), `Done: ${c.title}`) }, icon('check', { size: 16 }), big ? 'Done' : null),
      el('button', { class: big ? 'btn' : 'icon-btn', title: 'Snooze 1 hour', 'aria-label': 'Snooze 1 hour', onclick: () => run(() => client.snooze(c.id, 60), 'Snoozed 1 hour') }, icon('clock', { size: 16 }), big ? '1h' : null)));
  li.style.setProperty('--prio', priorityMeta(c.priority).color);
  return li;
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

$('open').append(icon('external', { size: 17 }));
$('settings').append(icon('settings', { size: 17 }));
chrome.tabs.onActivated.addListener(showPage);
chrome.tabs.onUpdated.addListener((_, info) => info.title && showPage());
setInterval(() => client && refresh(), 30000);
showPage();
init();
