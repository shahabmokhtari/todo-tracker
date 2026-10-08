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
    const attention = view && (view.conflicts.length > 0 || view.state === 'error' || !!view.warning);
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

    // DOM replaceChildren writes null as "null": the parts not shown are left out.
    panel.replaceChildren(...[
      view.warning ? h('div', { class: 'note warn sync-warning', role: 'alert' }, view.warning) : null,
      h('div', { class: `sync-status ${view.state}` },
        h('strong', null, stateText[view.state] ?? view.state),
        active ? h('span', { class: 'muted small' }, ` · ${view.provider ?? active.name} · last synced ${ago(view.lastSync)}`) : null),
      view.problem ? h('div', { class: 'note error' }, view.problem) : null,
      view.where ? h('div', { class: 'muted small sync-where' }, view.where) : null,
      h('div', { class: 'row' },
        h('label', { class: 'field' }, h('span', null, 'Sync with'), picker),
        h('button', { class: 'btn primary', type: 'button', disabled: !active || view.state === 'syncing', onclick: () => run(host.post(`${base}/now`)) }, 'Sync now')),
      library(),
      places(),
      view.conflicts.length
        ? h('section', { class: 'drawer-section' },
          h('h3', null, 'Changed on both computers'),
          h('p', { class: 'muted small' }, 'Both computers’ changes are in the task; where they changed the same thing, both versions are kept for now. Keep yours or theirs there, or compare them side by side.'),
          ...view.conflicts.map(conflict))
        : null,
      view.devices.length
        ? h('section', { class: 'drawer-section' },
          h('h3', null, 'Other computers'),
          ...view.devices.map((d) => h('div', { class: 'sync-device row' },
            h('span', null, h('strong', null, d.name), h('span', { class: 'muted small' }, ` · last synced ${ago(d.at)}`)),
            h('button', {
              class: 'btn ghost', type: 'button', title: 'Stop syncing with it (a computer you no longer use, or an old tasks folder)',
              onclick: () => { if (confirm(`Stop syncing with ${d.name}?`)) run(host.post(`${base}/forget`, { device: d.device })); },
            }, 'Forget'))))
        : null,
      h('details', { class: 'sync-providers' },
        h('summary', null, 'Where it can sync'),
        ...view.providers.map((p) => h('div', { class: 'small' }, h('strong', null, p.name), ' – ', h('span', { class: 'muted' }, p.detail))))].filter(Boolean));
  }

  /** Each place: read (its computers' changes come in), write (this computer's tasks go there), both, or not used. */
  function places() {
    const list = (view.places ?? []).filter((p) => p.available || p.mode !== 'off');
    if (list.length < 2) return null;
    const modes = [['both', 'Read and write'], ['read', 'Read only (bring changes in)'], ['write', 'Write only (send this computer’s tasks)'], ['off', 'Not used']];
    const row = (p) => {
      const select = h('select', { 'aria-label': `Use ${p.name}`, disabled: !p.available, onchange: () => run(host.put(`${base}/places`, { provider: p.id, mode: select.value })) },
        ...modes.map(([value, label]) => h('option', { value, selected: p.mode === value }, label)));
      const others = p.otherDevices == null ? '' : p.otherDevices === 0 ? ' · no other computers there yet' : ` · ${p.otherDevices} other computer${p.otherDevices === 1 ? '' : 's'} there`;
      return h('div', { class: 'sync-place row' },
        h('span', null, h('strong', null, p.name), h('span', { class: 'muted small' }, p.available ? others : ' · not available here')),
        select);
    };
    return h('details', { class: 'drawer-section sync-places', open: !!view.warning || list.some((p) => p.mode !== 'both' && p.mode !== 'off') },
      h('summary', null, 'More than one place'),
      h('p', { class: 'muted small' }, 'What’s read is merged into your tasks here (anything changed in both is listed below to settle); what’s written gets this computer’s tasks. Reading and writing every place keeps them all the same.'),
      ...list.map(row));
  }

  function library() {
    const input = h('input', { value: view.library, maxlength: 60, 'aria-label': 'Library', class: 'sync-library' });
    const save = () => {
      const name = input.value.trim();
      if (name && name !== view.library) run(host.put(`${base}/library`, { library: name }));
    };
    input.addEventListener('change', save);
    return h('label', { class: 'field', title: 'Computers sync when they use the same place and the same library name. Give a second tasks folder its own name to keep it separate.' },
      h('span', null, 'Library'), input);
  }

  function conflict(c) {
    const resolve = (choice) => run(host.post(`${base}/resolve`, { key: c.key, choice, place: c.place }));
    const through = (view.places ?? []).filter((p) => p.mode !== 'off').length > 1 ? (view.places.find((p) => p.id === c.place)?.name ?? '') : '';
    const sides = c.ready
      ? [h('button', { class: 'btn', type: 'button', onclick: () => resolve('mine') }, 'Keep mine'),
        h('button', { class: 'btn', type: 'button', onclick: () => resolve('theirs') }, `Keep ${c.peerName}’s`)]
      : [h('span', { class: 'muted small' }, `Waiting for ${c.peerName} to sync before you can pick a side…`)];
    return h('div', { class: 'sync-conflict' },
      h('div', null, h('strong', null, c.path.split('/').pop().replace(/\.md$/, '')), h('span', { class: 'muted small' }, ` · also changed on ${c.peerName}${through ? ` (through ${through})` : ''}`)),
      h('div', { class: 'row' },
        ...sides,
        h('button', { class: 'btn ghost', type: 'button', title: 'Keep the task as it is now (both versions)', onclick: () => resolve('merged') }, 'Keep both'),
        ...view.mergeTools.map((t) => h('button', {
          class: 'btn ghost', type: 'button', title: `Compare in ${t.name} (edit the task on the right)`,
          onclick: async () => {
            try {
              await host.post(`${base}/compare`, { key: c.key, tool: t.id, place: c.place });
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
