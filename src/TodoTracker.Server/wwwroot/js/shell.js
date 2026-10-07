// The app shell: views (hash routes), the side navigation (bottom bar on phones), the timer in the top bar, the break
// screen after a focus session, and the command palette (Ctrl+K).

import { breakState, tipFor } from './breaks.js';
import { rank } from './palette.js';
import { clock, duration } from './timefmt.js';

export const VIEWS = [
  { id: 'today', name: 'Today', icon: 'sun', key: '1' },
  { id: 'board', name: 'Board', icon: 'kanban', key: '2' },
  { id: 'tasks', name: 'Tasks', icon: 'tree', key: '3' },
  { id: 'done', name: 'Done', icon: 'archive', key: '4' },
  { id: 'reports', name: 'Reports', icon: 'pie', key: '5' },
];

/** The view and task a location hash names: #/board, #/task/<id> (a task, full size, over the last view). */
export function parseRoute(hash) {
  const parts = String(hash ?? '').replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts[0] === 'task' && parts[1]) return { view: null, task: decodeURIComponent(parts[1]) };
  // A panel a plugin opens over the view on screen (#/ask: the chat).
  if (parts[0] === 'ask') return { view: null, task: null, panel: parts[0] };
  return { view: VIEWS.some((v) => v.id === parts[0]) ? parts[0] : 'today', task: null };
}

export function createShell(ctx) {
  const { h, icon } = ctx;
  const $ = (s) => document.querySelector(s);
  let current = null;
  let dismissedBreak = Number(sessionStorage.getItem('tt.break.dismissed')) || null;

  // ---- navigation ------------------------------------------------------------------------------------------------

  const link = (v, cls) => h('a', {
    class: cls, href: `#/${v.id}`, dataset: { view: v.id }, title: `${v.name} (Alt+${v.key})`,
    onclick: () => closeNav(),
  }, h('span', { class: 'nav-icon' }, icon(v.icon, { size: 18 })), h('span', { class: 'nav-label' }, v.name), h('span', { class: 'nav-count', hidden: true }));
  $('#nav-views').replaceChildren(...VIEWS.map((v) => link(v, 'nav-link')));
  $('#bottom-nav').replaceChildren(...VIEWS.map((v) => link(v, 'bottom-link')));
  $('#palette-icon').append(icon('search', { size: 18 }));
  $('#plugins-btn').replaceChildren(h('span', { class: 'nav-icon' }, icon('layers', { size: 18 })), h('span', { class: 'nav-label' }, 'Plugins'));
  $('#nav-toggle').append(icon('menu'));

  const sidenav = $('#sidenav');
  const navScrim = $('#nav-scrim');
  function closeNav() {
    sidenav.classList.remove('open');
    navScrim.hidden = true;
    $('#nav-toggle').setAttribute('aria-expanded', 'false');
  }

  $('#nav-toggle').addEventListener('click', () => {
    const open = !sidenav.classList.contains('open');
    sidenav.classList.toggle('open', open);
    navScrim.hidden = !open;
    $('#nav-toggle').setAttribute('aria-expanded', String(open));
  });
  navScrim.addEventListener('click', closeNav);

  function setCounts(d) {
    const counts = { today: d?.now?.length ?? 0 };
    document.querySelectorAll('[data-view]').forEach((a) => {
      const badge = a.querySelector('.nav-count');
      const n = counts[a.dataset.view];
      badge.hidden = !n;
      badge.textContent = n ? String(n) : '';
    });
  }

  // ---- routing ---------------------------------------------------------------------------------------------------

  async function route() {
    const { view, task, panel } = parseRoute(location.hash);
    if (panel) {
      if (!current) show(localStorage.getItem('tt.view') || 'today');
      return;
    }

    if (task) {
      if (!current) show(localStorage.getItem('tt.view') || 'today');
      await ctx.openDrawer(task, { full: true });
      return;
    }

    // Another view, or Back from a task's own address: the panel closes (it would cover the view).
    if (ctx.drawerOpen()) await ctx.closeDrawer();
    show(view);
  }

  function show(id) {
    const changed = current !== id;
    current = id;
    localStorage.setItem('tt.view', id);
    const def = VIEWS.find((v) => v.id === id);
    document.querySelectorAll('[data-view]').forEach((a) => a.setAttribute('aria-current', a.dataset.view === id ? 'page' : 'false'));
    $('#view-title').textContent = def.name;
    document.body.dataset.view = id;
    $('#board').hidden = id !== 'today' || !ctx.signedIn();
    for (const v of VIEWS.filter((x) => x.id !== 'today')) {
      const el = document.getElementById(`view-${v.id}`);
      if (el) el.hidden = v.id !== id || !ctx.signedIn();
    }

    if (ctx.signedIn()) ctx.views[id]?.show();
    if (changed) $('#main').scrollTo?.(0, 0);
  }

  window.addEventListener('hashchange', route);

  // ---- timer chip ------------------------------------------------------------------------------------------------

  const chip = $('#timer');
  let timer = null;
  let timerKey = null;

  /** Redrawn only when another task starts timing (a redraw would drop keyboard focus on its Stop button). */
  function renderTimer(t) {
    timer = t?.running ? { ...t, since: Date.now() - t.elapsedSeconds * 1000 } : null;
    chip.hidden = !timer;
    const key = timer ? `${timer.itemId}|${timer.title}` : null;
    if (key === timerKey) return;
    timerKey = key;
    if (!timer) return;
    chip.replaceChildren(
      h('span', { class: 'timer-dot', 'aria-hidden': 'true' }),
      h('button', { class: 'timer-title', type: 'button', title: `Open ${timer.title}`, onclick: () => ctx.openDrawer(timer.itemId) },
        h('span', { class: 'timer-clock', id: 'timer-clock', role: 'timer', 'aria-live': 'off' }, clock(timer.elapsedSeconds)),
        h('span', { class: 'timer-task' }, timer.title)),
      h('button', { class: 'icon-btn small', type: 'button', title: 'Stop the timer', 'aria-label': `Stop timing ${timer.title}`, onclick: () => ctx.act(ctx.post('/api/timer/stop'), 'Timer stopped') }, icon('stop', { size: 14 })));
  }

  // ---- break screen ----------------------------------------------------------------------------------------------

  const screen = $('#break');
  let shown = null;
  let shownAt = 0;
  // The screen can appear mid-sentence: keys and clicks in the first moment are typing, not an answer.
  const settled = () => Date.now() - shownAt > 800;

  function hideBreak(key) {
    dismissedBreak = key;
    sessionStorage.setItem('tt.break.dismissed', String(key));
    screen.hidden = true;
    shown = null;
    ctx.restoreFocus?.();
  }

  function renderBreak(p) {
    // The focus timer is a plugin: switched off, there are no breaks either. In the Windows app's window, with
    // full-screen breaks on, the app itself shows the break on every screen (it sets this flag).
    const native = window.__ttNativeBreaks === true;
    const s = ctx.pluginOn('focus-timer') && !native ? breakState(p, Date.now(), dismissedBreak) : { show: false };
    if (!s.show) {
      if (shown) {
        screen.hidden = true;
        shown = null;
        ctx.restoreFocus?.();
      }

      return;
    }

    const left = Math.max(0, Math.ceil((s.until - Date.now()) / 1000));
    if (shown === s.key) {
      const clockEl = document.getElementById('break-clock');
      if (clockEl) clockEl.textContent = clock(left);
      return;
    }

    shown = s.key;
    shownAt = Date.now();
    const skip = h('button', { class: 'btn ghost light', type: 'button', onclick: async () => { if (!settled()) return; hideBreak(s.key); await ctx.act(ctx.post('/api/pomodoro/skip')); } }, icon('skip', { size: 16 }), 'Skip the break');
    screen.replaceChildren(
      h('div', { class: 'break-card', tabindex: -1 },
        h('span', { class: 'break-icon' }, icon('coffee', { size: 40 })),
        h('h2', { id: 'break-title' }, s.long ? 'Time for a longer break' : 'Time for a break'),
        h('p', { class: 'break-tip' }, tipFor(s.key)),
        h('div', { class: 'break-clock', id: 'break-clock', role: 'timer', 'aria-live': 'off' }, clock(left)),
        h('p', { class: 'break-sub' }, 'Your focus session is done. Step away; the timer tells you when to come back.'),
        h('div', { class: 'break-actions' },
          h('button', { class: 'btn primary', type: 'button', onclick: () => { if (settled()) hideBreak(s.key); } }, 'I’m taking it'),
          skip)));
    ctx.saveFocus?.();
    screen.hidden = false;
    // The card takes focus (not a button): a key pressed while typing can't answer for the user.
    screen.querySelector('.break-card')?.focus();
  }

  screen.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); if (settled()) hideBreak(shown); }
    // Focus stays inside the break screen while it's shown.
    if (e.key === 'Tab') {
      const items = [...screen.querySelectorAll('button')];
      const i = items.indexOf(document.activeElement);
      e.preventDefault();
      items[(i + (e.shiftKey ? -1 : 1) + items.length) % items.length]?.focus();
    }
  });

  // ---- command palette -------------------------------------------------------------------------------------------

  const palette = $('#palette');
  let results = [];
  let active = 0;
  let searchTimer = null;
  let lastFocus = null;

  function commands() {
    const d = ctx.dashboard();
    const list = [
      ...VIEWS.map((v) => ({ title: `Go to ${v.name}`, keywords: `view ${v.id}`, icon: v.icon, hint: `Alt+${v.key}`, run: () => { location.hash = `#/${v.id}`; } })),
      { title: 'New task', keywords: 'add capture create', icon: 'plus', hint: 'N', run: () => ctx.capture() },
      d?.timer?.running ? { title: `Stop the timer (${d.timer.title})`, keywords: 'timer stop', icon: 'stop', run: () => ctx.act(ctx.post('/api/timer/stop'), 'Timer stopped') } : null,
      d?.focus ? { title: `Start the timer on “${d.focus.title}”`, keywords: 'timer start track', icon: 'timer', run: () => ctx.act(ctx.post('/api/timer/start', { itemId: d.focus.id }), 'Timer started') } : null,
      d?.pomodoro?.phase === 'idle' && ctx.pluginOn('focus-timer') ? { title: 'Start a focus session', keywords: 'pomodoro focus', icon: 'target', run: () => ctx.act(ctx.post('/api/pomodoro/start', { itemId: d.focus?.id })) } : null,
      ...(d?.groups ?? []).map((g) => ({ title: `Show ${g.name}`, keywords: 'group tab switch', icon: 'folder', run: () => ctx.setGroup(g.id) })),
      { title: 'Show all groups', keywords: 'group all', icon: 'layers', run: () => ctx.setGroup(null) },
      { title: 'Plugins', keywords: 'settings features', icon: 'settings', run: () => ctx.openPlugins() },
    ];
    return list.filter(Boolean);
  }

  function renderResults(query, tasks = []) {
    const cmds = rank(commands(), query, query ? 6 : 10);
    results = [
      ...tasks.slice(0, 8).map((t) => ({ title: t.title, sub: t.breadcrumb.join(' › '), icon: t.isDone ? 'check' : 'note', run: () => ctx.openDrawer(t.id) })),
      ...cmds,
    ];
    active = Math.min(active, Math.max(0, results.length - 1));
    const list = palette.querySelector('.palette-list');
    list.replaceChildren(...(results.length ? results.map((r, i) => h('li', {
      role: 'option', id: `pal-${i}`, 'aria-selected': String(i === active), class: 'palette-item',
      onmousemove: () => { if (active !== i) { active = i; renderResults(query, tasks); } },
      onclick: () => choose(i),
    }, h('span', { class: 'palette-icon' }, icon(r.icon, { size: 16 })), h('span', { class: 'palette-text' }, h('span', null, r.title), r.sub ? h('span', { class: 'muted small' }, r.sub) : null), r.hint ? h('kbd', null, r.hint) : null))
      : [h('li', { class: 'palette-empty muted' }, 'Nothing matches. Enter adds it as a task.')]));
    palette.querySelector('input').setAttribute('aria-activedescendant', results.length ? `pal-${active}` : '');
  }

  function choose(i) {
    const r = results[i];
    closePalette();
    r?.run();
  }

  function openPalette() {
    lastFocus = document.activeElement;
    // Tasks found for the text in the box (never an older text's); `pending` is a search not answered yet.
    let tasks = [];
    let pending = null;
    const search = async (q) => {
      clearTimeout(searchTimer);
      try {
        const found = await ctx.api(`/api/search?q=${encodeURIComponent(q)}`);
        if (input.value.trim() !== q) return;
        tasks = found;
      } catch {
        // Commands still work.
      }

      if (input.value.trim() === q) {
        pending = null;
        renderResults(q, tasks);
      }
    };
    const input = h('input', {
      type: 'text', placeholder: 'Find a task or a command…', 'aria-label': 'Find a task or a command', role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'palette-list', autocomplete: 'off',
      oninput: () => {
        active = 0;
        tasks = [];
        renderResults(input.value, tasks);
        clearTimeout(searchTimer);
        const q = input.value.trim();
        pending = q || null;
        if (q) searchTimer = setTimeout(() => search(q), 120);
      },
      onkeydown: async (e) => {
        if (e.key === 'ArrowDown') { e.preventDefault(); active = Math.min(results.length - 1, active + 1); renderResults(input.value, tasks); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); active = Math.max(0, active - 1); renderResults(input.value, tasks); }
        else if (e.key === 'Enter') {
          e.preventDefault();
          // Typed fast: wait for the tasks, so an existing task opens instead of a copy being added.
          if (pending) await search(pending);
          if (results.length) choose(active);
          else if (input.value.trim()) { const text = input.value.trim(); closePalette(); ctx.act(ctx.post('/api/capture', { text, groupId: ctx.state.group }), 'Added'); }
        } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePalette(); }
      },
    });
    palette.replaceChildren(h('div', { class: 'palette-box' },
      h('div', { class: 'palette-input' }, icon('search', { size: 18 }), input, h('kbd', null, 'Esc')),
      h('ul', { class: 'palette-list', id: 'palette-list', role: 'listbox', 'aria-label': 'Results' })));
    palette.hidden = false;
    active = 0;
    renderResults('', []);
    input.focus();
  }

  function closePalette() {
    palette.hidden = true;
    lastFocus?.focus?.();
  }

  palette.addEventListener('mousedown', (e) => { if (e.target === palette) closePalette(); });
  $('#palette-btn').addEventListener('click', openPalette);

  // ---- keyboard --------------------------------------------------------------------------------------------------

  document.addEventListener('keydown', (e) => {
    // Nothing behind the break screen reacts to keys.
    if (!screen.hidden) return;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      if (palette.hidden) openPalette();
      else closePalette();
      return;
    }

    if (e.altKey && !e.ctrlKey && /^[1-5]$/.test(e.key)) {
      e.preventDefault();
      location.hash = `#/${VIEWS[Number(e.key) - 1].id}`;
    }
  });

  setInterval(() => {
    if (timer) {
      const el = document.getElementById('timer-clock');
      if (el) el.textContent = clock((Date.now() - timer.since) / 1000);
    }

    const p = ctx.dashboard()?.pomodoro;
    if (p) renderBreak(p);
  }, 1000);

  return {
    route,
    show,
    current: () => current,
    update(d) {
      setCounts(d);
      renderTimer(d?.timer);
      if (d?.pomodoro) renderBreak(d.pomodoro);
    },
    closeNav,
    openPalette,
    timerTitle: () => (timer ? `${timer.title} · ${duration((Date.now() - timer.since) / 1000)}` : null),
  };
}
