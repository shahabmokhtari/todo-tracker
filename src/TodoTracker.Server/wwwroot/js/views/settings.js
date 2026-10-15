// Settings: everything to set up in one place: how it looks (light, dark or the system's), label colors, connected apps (Notion,
// Microsoft To Do: switch on, sign in, pick what to keep in step), sync, which extras are on, and the keyboard.

import { createConnectorsPanel } from '../plugins/connectors.js';
import { THEMES, chooseTheme } from '../themes.js';

const CONNECTOR_PLUGINS = new Set(['connector-notion', 'connector-mstodo']);

// A label name may hold "/" (Q3/Q4): it's sent as a path (the server route takes the rest of the path), since an
// encoded slash (%2F) isn't decoded in a route value.
const labelUrl = (name) => `/api/labels/${encodeURIComponent(name).replace(/%2F/gi, '/')}`;

const SHORTCUTS = [
  ['N', 'Add a task'],
  ['/', 'Filter'],
  ['Ctrl K', 'Find anything, run a command'],
  ['Alt 1 … 6', 'Today, Board, Tasks, Done, Reports, Settings'],
  ['Alt ↑ ↓', 'Move a task up or down'],
  ['Alt ← →', 'Move a card between columns (Board)'],
  ['Tab / Shift Tab', 'Nest and un-nest (Tasks)'],
  ['Esc', 'Close'],
];

export function createSettingsView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-settings');
  let connectors = null;

  const section = (id, title, intro, ...body) => h('section', { class: 'settings-section', id: `settings-${id}`, 'aria-labelledby': `settings-${id}-title` },
    h('h2', { id: `settings-${id}-title` }, title),
    intro ? h('p', { class: 'muted' }, intro) : null,
    ...body);

  function appearance(theme) {
    const choose = async (value) => {
      root.querySelectorAll('.theme-choice').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.value === value)));
      try {
        await chooseTheme(value, () => ctx.api('/api/settings/theme', { method: 'PUT', body: { theme: value } }));
      } catch (err) {
        ctx.toast(err.message, 'error');
      }
    };
    return section('appearance', 'Appearance', 'Every window follows it: this one, the browser and the sidebar.',
      h('div', { class: 'seg theme-seg', role: 'group', 'aria-label': 'Theme' },
        ...THEMES.map(([value, label, iconName]) => h('button', {
          type: 'button', class: 'theme-choice', dataset: { value }, 'aria-pressed': String(theme === value), onclick: () => choose(value),
        }, icon(iconName, { size: 15 }), label))));
  }

  function plugins(list) {
    const note = h('p', { class: 'muted small', hidden: true, role: 'status' }, 'Restart Todo Tracker to apply the change.');
    const row = (p) => {
      const toggle = h('input', { type: 'checkbox', checked: p.enabled, 'aria-label': p.name, onchange: async () => {
        try {
          const result = await ctx.api(`/api/plugins/${encodeURIComponent(p.id)}`, { method: 'PUT', body: { enabled: toggle.checked } });
          if (result.restartRequired) note.hidden = false;
        } catch (err) {
          toggle.checked = !toggle.checked;
          ctx.toast(err.message, 'error');
        }
      } });
      return h('label', { class: 'plugin-row' }, toggle, h('span', null, h('strong', null, p.name), h('span', { class: 'muted small' }, p.description)));
    };
    return section('plugins', 'Extras', 'Every extra is a plugin. Turn off what you don’t use to keep Todo Tracker calm.',
      h('div', { class: 'plugins' }, ...list.filter((p) => !CONNECTOR_PLUGINS.has(p.id)).map(row)), note);
  }

  function sync(list) {
    const on = list.some((p) => p.id.startsWith('sync-') && p.enabled);
    return section('sync', 'Sync', on ? 'Your tasks folder stays in step with your other computers.' : 'Switch on a sync extra below (OneDrive, iCloud Drive or a GitHub gist).',
      on ? h('button', { class: 'btn', type: 'button', onclick: () => {
        const open = document.querySelector('.sync-btn');
        if (open) open.click();
        else ctx.toast('Sync is still starting. Try again in a moment.', 'error');
      } }, icon('cloud', { size: 16 }), 'Sync settings') : null);
  }

  /** Labels: each one's color (tasks without their own color show it), its name, and removing it. */
  function labels(list) {
    const save = async (label, body, message) => {
      try {
        await ctx.api(labelUrl(label.name), { method: 'PATCH', body: { name: body.name ?? label.name, color: body.color ?? label.color } });
        ctx.toast(message);
        load();
      } catch (err) {
        ctx.toast(err.message, 'error');
      }
    };
    const row = (label) => {
      const color = h('input', { type: 'color', value: label.color, 'aria-label': `Color of ${label.name}`, title: 'Color' });
      color.addEventListener('change', () => save(label, { color: color.value }, `${label.name}: new color`));
      const name = h('input', { type: 'text', value: label.name, maxlength: 40, 'aria-label': `Name of ${label.name}` });
      name.addEventListener('change', () => { if (name.value.trim() && name.value.trim() !== label.name) save(label, { name: name.value.trim() }, 'Renamed'); });
      const remove = h('button', { class: 'icon-btn small', type: 'button', title: 'Remove this label', 'aria-label': `Remove ${label.name}`, onclick: async () => {
        if (!confirm(`Remove the label "${label.name}" from every task?`)) return;
        try {
          await ctx.api(labelUrl(label.name), { method: 'DELETE' });
          load();
        } catch (err) {
          ctx.toast(err.message, 'error');
        }
      } }, icon('trash', { size: 14 }));
      return h('div', { class: 'label-row' }, color, name, remove);
    };
    return section('labels', 'Labels', 'Each label has a color: a task without a color of its own shows its first label’s.',
      list.length ? h('div', { class: 'label-rows' }, ...list.map(row)) : h('p', { class: 'muted small' }, 'No labels yet. Add one from a task’s details.'));
  }

  /** Hooks: what runs when something happens (hooks.json on this computer; the app never writes it), recent runs, a try. */
  function hooks(status) {
    const when = (at) => new Date(at).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    const events = Object.keys(status.events);
    const pick = h('select', { 'aria-label': 'Event to try' }, ...events.map((e) => h('option', { value: e, selected: e === 'taskCompleted' }, e)));
    const tryIt = h('button', { class: 'btn', type: 'button', onclick: async () => {
      tryIt.disabled = true;
      try {
        const runs = await ctx.api('/api/hooks/test', { method: 'POST', body: { event: pick.value } });
        ctx.toast(runs.length ? `${runs.filter((r) => r.ok).length} of ${runs.length} hook${runs.length > 1 ? 's' : ''} worked` : 'No hook runs for that event', runs.every((r) => r.ok) ? undefined : 'error');
        load();
      } catch (err) {
        ctx.toast(err.message, 'error');
      } finally {
        tryIt.disabled = false;
      }
    } }, icon('play', { size: 14 }), 'Try it');
    const sample = h('pre', { class: 'hook-sample' }, status.sample);
    const copy = h('button', { class: 'btn small ghost', type: 'button', onclick: async () => {
      try { await navigator.clipboard.writeText(status.sample); ctx.toast('Copied'); } catch { ctx.toast('Couldn’t copy: select the text instead', 'error'); }
    } }, icon('note', { size: 14 }), 'Copy');
    return section('hooks', 'Hooks',
      'Run your own script, or call a web address, when something happens: a task done, a reminder, a break. Hooks live in a file on this computer only; the app (and the AI tools) never change it.',
      h('p', { class: 'small' }, h('span', { class: 'muted' }, 'File: '), h('code', null, status.path)),
      status.problem ? h('p', { class: 'hook-problem', role: 'alert' }, status.problem) : null,
      status.exists
        ? (status.hooks.length
          ? h('ul', { class: 'hook-list' }, ...status.hooks.map((x) => h('li', null, h('code', null, x.event), ' ', h('span', { class: 'muted small' }, x.type), ' ', x.label,
            x.problem ? h('span', { class: 'hook-problem small' }, ` – ${x.problem}`) : null)))
          : h('p', { class: 'muted small' }, 'The file has no hooks yet.'))
        : h('div', null, h('p', { class: 'muted small' }, 'No hooks yet. Save this as that file to start (change what it runs first):'), sample, copy),
      h('div', { class: 'row' }, pick, tryIt),
      status.runs.length
        ? h('table', { class: 'hook-runs' },
          h('thead', null, h('tr', null, ...['When', 'Event', 'Hook', 'Result'].map((t) => h('th', null, t)))),
          h('tbody', null, ...status.runs.map((r) => h('tr', { class: r.ok ? '' : 'failed' },
            h('td', null, when(r.at)), h('td', null, h('code', null, r.event)), h('td', null, r.hook),
            h('td', null, r.ok ? 'OK' : (r.exitCode != null ? `Failed (${r.exitCode})` : 'Failed'), r.output ? h('details', null, h('summary', null, 'Output'), h('pre', null, r.output)) : null)))))
        : null,
      h('p', { class: 'muted small' }, 'Extensions and scripts can also listen: GET /api/events (server-sent events, with the API token). See docs/hooks.md.'));
  }

  function render(settings, list, labelList, hookStatus) {
    connectors ??= createConnectorsPanel(ctx.pluginHost);
    root.replaceChildren(h('div', { class: 'settings' },
      appearance(settings.theme ?? 'system'),
      labels(labelList),
      section('connected', 'Connected apps', null, connectors.element),
      sync(list),
      hookStatus ? hooks(hookStatus) : null,
      plugins(list),
      section('keys', 'Keyboard', null, h('dl', { class: 'shortcuts' }, ...SHORTCUTS.flatMap(([keys, what]) => [h('dt', null, h('kbd', null, keys)), h('dd', null, what)])))));
  }

  async function load() {
    try {
      const [settings, list, labelList, hookStatus] = await Promise.all([ctx.api('/api/settings'), ctx.api('/api/plugins'), ctx.api('/api/labels'), ctx.api('/api/hooks').catch(() => null)]);
      render(settings, list, labelList, hookStatus);
      await connectors.load();
      if (location.hash.includes('plugins')) root.querySelector('#settings-plugins')?.scrollIntoView();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  // Task changes elsewhere (a timer, quick add, the filter) don't touch settings: no reload that would wipe a token
  // being typed. Leaving the view stops a sign-in that's waiting.
  return { root, show: load, refresh: () => {}, hide: () => connectors?.stop(), poll: () => {}, title: 'Settings' };
}
