// Settings: everything to set up in one place: how it looks (light, dark or the system's), connected apps (Notion,
// Microsoft To Do: switch on, sign in, pick what to keep in step), sync, which extras are on, and the keyboard.

import { createConnectorsPanel } from '../plugins/connectors.js';
import { THEMES, chooseTheme } from '../themes.js';

const CONNECTOR_PLUGINS = new Set(['connector-notion', 'connector-mstodo']);

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

  function render(settings, list) {
    connectors ??= createConnectorsPanel(ctx.pluginHost);
    root.replaceChildren(h('div', { class: 'settings' },
      appearance(settings.theme ?? 'system'),
      section('connected', 'Connected apps', null, connectors.element),
      sync(list),
      plugins(list),
      section('keys', 'Keyboard', null, h('dl', { class: 'shortcuts' }, ...SHORTCUTS.flatMap(([keys, what]) => [h('dt', null, h('kbd', null, keys)), h('dd', null, what)])))));
  }

  async function load() {
    try {
      const [settings, list] = await Promise.all([ctx.api('/api/settings'), ctx.api('/api/plugins')]);
      render(settings, list);
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
