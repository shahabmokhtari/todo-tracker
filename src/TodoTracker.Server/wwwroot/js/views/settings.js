// Settings: everything to set up in one place: how it looks (light, dark or the system's), label colors, connected apps (Notion,
// Microsoft To Do: switch on, sign in, pick what to keep in step), sync, phones, which extras are on, and the keyboard.

import { createConnectorsPanel } from '../plugins/connectors.js';
import { createPhonesSection } from '../plugins/phones.js';
import { THEMES, chooseTheme } from '../themes.js';
import { describeHook } from '../hook-prompt.js';
import { energyLine } from '../format.js';

const CONNECTOR_PLUGINS = new Set(['connector-notion', 'connector-mstodo']);

// A label name may hold "/" (Q3/Q4): it's sent as a path (the server route takes the rest of the path), since an
// encoded slash (%2F) isn't decoded in a route value.
const labelUrl = (name) => `/api/labels/${encodeURIComponent(name).replace(/%2F/gi, '/')}`;

const SHORTCUTS = [
  ['N', 'Add a task'],
  ['/', 'Filter'],
  ['Ctrl K', 'Find anything, run a command'],
  ['Alt 1 … 7', 'Today, Board, Tasks, Done, Reports, Plan, Settings'],
  ['Alt ↑ ↓', 'Move a task up or down'],
  ['Alt ← →', 'Move a card between columns (Board)'],
  ['Tab / Shift Tab', 'Nest and un-nest (Tasks)'],
  ['Esc', 'Close'],
];

export function createSettingsView(ctx) {
  const { h, icon } = ctx;
  const root = document.getElementById('view-settings');
  let connectors = null;
  let phones = null;

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
    const note = h('p', { class: 'muted small', hidden: true, role: 'status' }, 'Restart Todo Tracker to switch it on.');
    const row = (p) => {
      const toggle = h('input', { type: 'checkbox', checked: p.enabled, 'aria-label': p.name, onchange: async () => {
        try {
          const result = await ctx.api(`/api/plugins/${encodeURIComponent(p.id)}`, { method: 'PUT', body: { enabled: toggle.checked } });
          if (result.restartRequired) note.hidden = false;
          // Off (or a live extra on): its buttons, menus and views follow at once.
          await ctx.reloadPlugins?.();
          if (p.id === 'energy' || p.id === 'phones') await load();
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

  /**
   * Hooks: what runs when something happens (hooks.json on this computer). Add, change, switch on or off, remove; what
   * an AI agent added or changed waits here for approval (it shows exactly what would run). Recent runs, and a try.
   */
  function hooks(status) {
    const when = (at) => new Date(at).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    const events = Object.keys(status.events).filter((e) => e !== 'test');
    const call = async (url, options, message) => {
      try {
        await ctx.api(url, options);
        if (message) ctx.toast(message);
        load();
      } catch (err) {
        ctx.toast(err.message, 'error');
      }
    };

    const form = hookForm(events, status, call);
    const row = (x) => {
      const pending = x.state === 'needs approval';
      const toggle = h('input', { type: 'checkbox', checked: x.hook.enabled !== false, 'aria-label': `${x.label}: on`, title: 'On or off',
        onchange: () => call(`/api/hooks/${encodeURIComponent(x.id)}/enabled`, { method: 'POST', body: { enabled: toggle.checked } }) });
      const what = describeHook(x.hook);
      return h('li', { class: `hook-row${pending ? ' pending' : ''}`, dataset: { id: x.id } },
        h('div', { class: 'hook-head' },
          toggle,
          h('code', null, x.event),
          h('span', { class: 'hook-label' }, x.label),
          h('span', { class: `chip ${pending ? 'warn' : x.state === 'off' ? 'muted' : 'info'}` }, x.state),
          h('span', { class: 'hook-actions' },
            pending && status.canApprove ? h('button', { class: 'btn small primary', type: 'button', onclick: () => call(`/api/hooks/${encodeURIComponent(x.id)}/approve`, { method: 'POST', body: { version: x.version } }, 'Approved: it runs from now on') }, icon('check', { size: 14 }), 'Approve') : null,
            h('button', { class: 'icon-btn small', type: 'button', title: 'Change', 'aria-label': `Change ${x.label}`, onclick: () => form.open(x) }, icon('pencil', { size: 14 })),
            h('button', { class: 'icon-btn small', type: 'button', title: 'Remove', 'aria-label': `Remove ${x.label}`, onclick: () => {
              if (confirm(`Remove the hook "${x.label}"?`)) call(`/api/hooks/${encodeURIComponent(x.id)}`, { method: 'DELETE' }, 'Removed');
            } }, icon('trash', { size: 14 })))),
        pending ? h('p', { class: 'small hook-why' }, `Added or changed by ${x.hook.addedBy ?? 'an agent'}. It runs only after you approve it. What it would do:`) : null,
        pending && !status.canApprove ? h('p', { class: 'small muted' }, `To approve it, open Settings from the Todo Tracker app (the sidebar, Open in browser from the tray, or the Mac app), or run: tt hooks approve ${x.id} ${x.version.slice(0, 12)}`) : null,
        pending ? h('pre', { class: 'hook-sample' }, what) : null,
        x.problem ? h('p', { class: 'hook-problem small' }, x.problem) : null);
    };

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

    return section('hooks', 'Hooks',
      'Run your own script, or call a web address, when something happens: a task done, a reminder, a break. Hooks you add here run right away; ones an AI agent adds or changes wait for your approval.',
      h('p', { class: 'small' }, h('span', { class: 'muted' }, 'File: '), h('code', null, status.path), h('span', { class: 'muted' }, ' (also: tt hooks)')),
      status.problem ? h('p', { class: 'hook-problem', role: 'alert' }, status.problem) : null,
      status.hooks.length ? h('ul', { class: 'hook-list' }, ...status.hooks.map(row)) : h('p', { class: 'muted small' }, 'No hooks yet.'),
      h('div', { class: 'row' }, h('button', { class: 'btn', type: 'button', onclick: () => form.open(null) }, icon('plus', { size: 14 }), 'Add a hook'), pick, tryIt),
      form.element,
      status.runs.length
        ? h('table', { class: 'hook-runs' },
          h('thead', null, h('tr', null, ...['When', 'Event', 'Hook', 'Result'].map((t) => h('th', null, t)))),
          h('tbody', null, ...status.runs.map((r) => h('tr', { class: r.ok ? '' : 'failed' },
            h('td', null, when(r.at)), h('td', null, h('code', null, r.event)), h('td', null, r.hook),
            h('td', null, r.ok ? 'OK' : (r.exitCode != null ? `Failed (${r.exitCode})` : 'Failed'), r.output ? h('details', null, h('summary', null, 'Output'), h('pre', null, r.output)) : null)))))
        : null,
      h('p', { class: 'muted small' }, 'Extensions and scripts can also listen: GET /api/events (server-sent events, with the API token). See docs/hooks.md.'));
  }

  /** Adding or changing a hook: when, what (a command for Windows and/or Linux and macOS, or a web address), and for what. */
  function hookForm(events, status, call) {
    const element = h('form', { class: 'hook-form', hidden: true, 'aria-label': 'Hook' });
    let editing = null;
    const list = (v) => (v ?? []).join(', ');
    const split = (v) => v.split(',').map((x) => x.trim()).filter(Boolean);
    const field = (label, control, hint) => h('label', { class: 'field' }, h('span', null, label), control, hint ? h('span', { class: 'muted small' }, hint) : null);
    const eventPick = h('select', { 'aria-label': 'When' }, h('option', { value: '*' }, 'Every event (*)'), ...events.map((e) => h('option', { value: e, title: status.events[e] }, `${e} — ${status.events[e]}`)));
    const kind = h('select', { 'aria-label': 'What it does' }, h('option', { value: 'command' }, 'Run a command'), h('option', { value: 'http' }, 'Call a web address'));
    const powershell = h('textarea', { rows: 3, 'aria-label': 'PowerShell (Windows)', placeholder: '$e = [Console]::In.ReadToEnd() | ConvertFrom-Json; …' });
    const bash = h('textarea', { rows: 3, 'aria-label': 'bash (Linux, macOS)', placeholder: 'jq -r .task.title >> ~/done.log' });
    const url = h('input', { type: 'url', 'aria-label': 'Web address', placeholder: 'https://example.com/webhook' });
    const name = h('input', { maxlength: 100, 'aria-label': 'Name', placeholder: 'What it is, in a few words' });
    const timeout = h('input', { type: 'number', min: 1, max: 600, 'aria-label': 'Time limit (seconds)', placeholder: '30' });
    const groups = h('input', { 'aria-label': 'Only for groups', placeholder: 'Work, Personal' });
    const tags = h('input', { 'aria-label': 'Only for tags', placeholder: 'deploy' });
    const labels = h('input', { 'aria-label': 'Only for labels', placeholder: 'Urgent' });
    const actors = h('input', { 'aria-label': 'Only when done by', placeholder: 'user, agent, vault…' });
    const commandFields = h('div', null, field('PowerShell (Windows)', powershell), field('bash (Linux, macOS)', bash, 'The event comes as JSON on standard input.'));
    const httpFields = h('div', null, field('Web address', url, 'The event is posted as JSON.'));
    const showKind = () => { commandFields.hidden = kind.value !== 'command'; httpFields.hidden = kind.value !== 'http'; };
    kind.addEventListener('change', showKind);
    const title = h('h3', null, 'Add a hook');

    element.append(title,
      h('div', { class: 'row' }, field('When', eventPick), field('What it does', kind)),
      commandFields, httpFields,
      h('div', { class: 'row' }, field('Name', name), field('Time limit (seconds)', timeout)),
      h('details', null, h('summary', null, 'Only for some tasks'),
        h('div', { class: 'row' }, field('Groups', groups), field('Tags', tags)),
        h('div', { class: 'row' }, field('Labels', labels), field('Done by', actors))),
      h('div', { class: 'row buttons' },
        h('button', { class: 'btn primary', type: 'submit' }, 'Save'),
        h('button', { class: 'btn ghost', type: 'button', onclick: () => { element.hidden = true; } }, 'Cancel')));

    element.addEventListener('submit', (e) => {
      e.preventDefault();
      const hook = {
        type: kind.value,
        powershell: powershell.value || null, bash: bash.value || null, url: url.value || null,
        name: name.value || null, timeoutSec: timeout.value ? Number(timeout.value) : null,
        match: { groups: split(groups.value), tags: split(tags.value), labels: split(labels.value), actors: split(actors.value) },
      };
      // What the form doesn't show (headers, variables, folder) stays as it was.
      if (editing) Object.assign(hook, { headers: editing.hook.headers ?? null, env: editing.hook.env ?? null, cwd: editing.hook.cwd ?? null });
      const body = { event: eventPick.value, hook };
      if (editing) call(`/api/hooks/${encodeURIComponent(editing.id)}`, { method: 'PUT', body }, 'Hook changed');
      else call('/api/hooks', { method: 'POST', body }, 'Hook added');
    });

    function open(entry) {
      editing = entry;
      const x = entry?.hook ?? {};
      title.textContent = entry ? `Change “${entry.label}”` : 'Add a hook';
      eventPick.value = entry?.event ?? 'taskCompleted';
      kind.value = x.type ?? 'command';
      powershell.value = x.powershell ?? '';
      bash.value = x.bash ?? '';
      url.value = x.url ?? '';
      name.value = x.name ?? '';
      timeout.value = x.timeoutSec ?? '';
      groups.value = list(x.match?.groups);
      tags.value = list(x.match?.tags);
      labels.value = list(x.match?.labels);
      actors.value = list(x.match?.actors);
      showKind();
      element.hidden = false;
      eventPick.focus();
    }

    return { element, open };
  }
  /** The Energy extra: how much there is in a day (kept with the tasks, so every app and computer uses it). */
  function energy(info) {
    const input = h('input', { type: 'number', min: 1, max: 100, step: 1, value: String(info.perDay), 'aria-label': 'Energy in a day' });
    const status = h('p', { class: 'muted small', role: 'status' }, energyLine(info));
    const save = async (perDay) => {
      try {
        const next = await ctx.api('/api/energy', { method: 'PUT', body: { perDay } });
        input.value = String(next.perDay);
        status.textContent = energyLine(next);
        ctx.refreshAll?.();
      } catch (err) {
        ctx.toast(err.message, 'error');
      }
    };
    return section('energy', 'Energy', 'How much you have in a day, in ⚡. Each task takes ⚡ to ⚡⚡⚡ an hour (set it in its details; not said counts as ⚡). The plan adds up each day: its blocks’ hours times their ⚡, and shows when it’s more than you have. Every app uses this number.',
      h('div', { class: 'row' },
        h('label', { class: 'field' }, h('span', null, 'Energy in a day'), input),
        h('button', { class: 'btn primary small', type: 'button', onclick: () => save(Number(input.value)) }, 'Save'),
        h('button', { class: 'btn ghost small', type: 'button', onclick: () => save(null) }, 'Back to 10')),
      status);
  }

  function render(settings, list, labelList, hookStatus, energyInfo) {
    connectors ??= createConnectorsPanel(ctx.pluginHost);
    phones ??= createPhonesSection(ctx);
    const phonesOn = list.some((p) => p.id === 'phones' && p.enabled);
    if (!phonesOn) phones.stop();
    root.replaceChildren(h('div', { class: 'settings' },
      appearance(settings.theme ?? 'system'),
      energyInfo && list.some((p) => p.id === 'energy' && p.enabled) ? energy(energyInfo) : null,
      labels(labelList),
      section('connected', 'Connected apps', null, connectors.element),
      sync(list),
      phonesOn ? section('phones', 'Phones', 'Use the Todo Tracker app on your iPhone, iPad or Android phone with the tasks on this computer, at home on Wi-Fi or anywhere with Tailscale.', phones.element) : null,
      hookStatus ? hooks(hookStatus) : null,
      plugins(list),
      section('keys', 'Keyboard', null, h('dl', { class: 'shortcuts' }, ...SHORTCUTS.flatMap(([keys, what]) => [h('dt', null, h('kbd', null, keys)), h('dd', null, what)])))));
  }

  async function load() {
    try {
      const [settings, list, labelList, hookStatus, energyInfo] = await Promise.all([ctx.api('/api/settings'), ctx.api('/api/plugins'), ctx.api('/api/labels'), ctx.api('/api/hooks').catch(() => null), ctx.api('/api/energy').catch(() => null)]);
      render(settings, list, labelList, hookStatus, energyInfo);
      await connectors.load();
      if (list.some((p) => p.id === 'phones' && p.enabled)) await phones.load();
      // #/settings/plugins, #/settings/hooks (a hook waiting for approval links here), #/settings/phones: that section.
      const part = location.hash.match(/^#\/settings\/(plugins|hooks|phones)/)?.[1];
      if (part) root.querySelector(`#settings-${part}`)?.scrollIntoView();
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
  }

  // Task changes elsewhere (a timer, quick add, the filter) don't touch settings: no reload that would wipe a token
  // being typed. Leaving the view stops a sign-in that's waiting.
  return { root, show: load, refresh: () => {}, hide: () => { connectors?.stop(); phones?.stop(); }, poll: () => {}, title: 'Settings' };
}
