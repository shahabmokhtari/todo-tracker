// Sync: keeps the tasks folder in step with your other computers through OneDrive, iCloud Drive or a GitHub gist.
// Shared by every sync provider plugin (each is its own plugin); the panel shows where it syncs, when it last did, and
// anything both computers changed in the same place (keep yours, keep theirs, or compare them in a merge tool).

const base = '/api/plugins/sync';
let started = false;

const stateText = {
  idle: 'Up to date',
  syncing: 'Syncing…',
  error: 'Couldn’t sync',
  off: 'Sync is off',
  unavailable: 'Nothing to sync with',
};

function ago(iso) {
  if (!iso) return 'not yet';
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  return hours < 24 ? `${hours} h ago` : new Date(iso).toLocaleDateString();
}

export function activate(host) {
  if (started) return; // one module for all sync plugins
  started = true;
  const { h, icon } = host;
  let view = null;
  let open = false;

  const button = host.addHeaderButton({ iconName: 'cloud', label: 'Sync', onClick: () => show() });
  button.classList.add('sync-btn');

  async function load() {
    try {
      view = await host.api(base);
      badge();
      if (open) render();
    } catch {
      // The status is optional; the next check tries again.
    }
  }

  function badge() {
    const attention = view && (view.conflicts.length > 0 || view.state === 'error');
    button.classList.toggle('attention', !!attention);
    button.title = view ? `Sync: ${stateText[view.state] ?? view.state}${view.conflicts.length ? ` – ${view.conflicts.length} to look at` : ''}` : 'Sync';
  }

  async function run(promise) {
    try {
      view = await promise;
      badge();
      render();
      host.refresh();
    } catch (err) {
      host.toast(err.message, 'error');
    }
  }

  const panel = h('div', { class: 'sync' });

  function render() {
    if (!view) return;
    const active = view.providers.find((p) => p.active);
    const choices = [
      h('option', { value: 'auto', selected: view.choice === 'auto' }, 'Automatic (first available)'),
      ...view.providers.map((p) => h('option', { value: p.id, selected: view.choice === p.id }, p.available ? p.name : `${p.name} (not available)`)),
      h('option', { value: 'off', selected: view.choice === 'off' }, 'Off'),
    ];
    const picker = h('select', { class: 'sync-picker', 'aria-label': 'Sync with', onchange: () => run(host.put(`${base}/provider`, { provider: picker.value })) }, ...choices);

    panel.replaceChildren(
      h('div', { class: `sync-status ${view.state}` },
        h('strong', null, stateText[view.state] ?? view.state),
        active ? h('span', { class: 'muted small' }, ` · ${active.name} · last synced ${ago(view.lastSync)}`) : null),
      view.problem ? h('div', { class: 'note error' }, view.problem) : null,
      view.where ? h('div', { class: 'muted small sync-where' }, view.where) : null,
      h('div', { class: 'row' },
        h('label', { class: 'field' }, h('span', null, 'Sync with'), picker),
        h('button', { class: 'btn primary', type: 'button', disabled: !active || view.state === 'syncing', onclick: () => run(host.post(`${base}/now`)) }, 'Sync now')),
      view.conflicts.length
        ? h('section', { class: 'drawer-section' },
          h('h3', null, 'Changed on both computers'),
          h('p', { class: 'muted small' }, 'Both versions are kept in the task for now. Keep one, or compare them side by side.'),
          ...view.conflicts.map(conflict))
        : null,
      h('details', { class: 'sync-providers' },
        h('summary', null, 'Where it can sync'),
        ...view.providers.map((p) => h('div', { class: 'small' }, h('strong', null, p.name), ' – ', h('span', { class: 'muted' }, p.detail)))));
  }

  function conflict(c) {
    const resolve = (choice) => run(host.post(`${base}/resolve`, { key: c.key, choice }));
    return h('div', { class: 'sync-conflict' },
      h('div', null, h('strong', null, c.path.split('/').pop().replace(/\.md$/, '')), h('span', { class: 'muted small' }, ` · also changed on ${c.peerName}`)),
      h('div', { class: 'row' },
        h('button', { class: 'btn', type: 'button', onclick: () => resolve('mine') }, 'Keep mine'),
        h('button', { class: 'btn', type: 'button', onclick: () => resolve('theirs') }, `Keep ${c.peerName}’s`),
        h('button', { class: 'btn ghost', type: 'button', title: 'Keep the task as it is now (both versions merged)', onclick: () => resolve('merged') }, 'Keep both'),
        ...view.mergeTools.map((t) => h('button', {
          class: 'btn ghost', type: 'button', title: `Compare in ${t.name} (edit the task on the right)`,
          onclick: async () => {
            try {
              await host.post(`${base}/compare`, { key: c.key, tool: t.id });
            } catch (err) {
              host.toast(err.message, 'error');
            }
          },
        }, t.name))));
  }

  async function show() {
    const opened = await host.openPanel('Sync', panel, { iconName: 'cloud', onClose: () => { open = false; } });
    if (!opened) return;
    open = true;
    panel.replaceChildren(h('div', { class: 'muted' }, 'Loading…'));
    await load();
  }

  load();
  setInterval(load, 30000);
  if (new URLSearchParams(location.search).has('sync')) show();
}
