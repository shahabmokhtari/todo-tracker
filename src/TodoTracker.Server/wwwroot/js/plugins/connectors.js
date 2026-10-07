// Connectors: keep a group in step with a Notion database or a Microsoft To Do list, both ways. Each is its own plugin
// (off until switched on in Plugins); this one panel sets them up: sign in, pick the list and the group, look at what
// the first sync will do, then sync (and from then on every few minutes).

const base = '/api/plugins/connectors';
const docs = 'https://github.com/shahabmokhtari/todo-tracker/blob/master/docs/connectors.md';
let started = false;

/** What a first sync will do, said plainly. */
export function previewText(p) {
  const parts = [];
  if (p.addHere) parts.push(`add ${p.addHere} task${p.addHere > 1 ? 's' : ''} here`);
  if (p.sendThere) parts.push(`send ${p.sendThere} task${p.sendThere > 1 ? 's' : ''} there`);
  const updates = (p.updateHere ?? 0) + (p.updateThere ?? 0);
  if (updates) parts.push(`update ${updates}`);
  if (p.archive) parts.push(`archive ${p.archive} deleted here`);
  return parts.length ? `It will ${parts.join(', ')}.` : 'Nothing to change: they already match.';
}

export function activate(host) {
  if (started) return; // one module for every connector plugin
  started = true;
  const { h, icon } = host;
  let views = [];
  let preview = {};
  let open = false;
  const extras = { notion: { databases: null }, mstodo: { lists: null } };
  let poll = null;

  host.addHeaderButton({ iconName: 'link', label: 'Connectors (Notion, To Do)', onClick: () => show() });
  const panel = h('div', { class: 'connectors' });

  async function load() {
    try {
      views = await host.api(base);
      if (open) render();
    } catch (err) {
      host.toast(err.message, 'error');
    }
  }

  async function run(promise, message) {
    try {
      const result = await promise;
      if (message) host.toast(message);
      await load();
      return result;
    } catch (err) {
      host.toast(err.message, 'error');
      return null;
    }
  }

  const configure = (id, body) => run(host.put(`${base}/${id}`, body));

  function select(label, options, value, onchange) {
    return h('label', { class: 'field' }, h('span', null, label),
      h('select', { 'aria-label': label, onchange: (e) => onchange(e.target.value) },
        h('option', { value: '', selected: !value, disabled: true }, 'Choose…'),
        ...options.map(([v, text]) => h('option', { value: v, selected: v === value }, text))));
  }

  function common(v, targets, targetLabel) {
    const groups = host.groups();
    const rows = [
      select(targetLabel, targets ?? (v.target ? [[v.target, v.targetName ?? v.target]] : []), v.target, (target) => configure(v.id, { target, targetName: (targets ?? []).find(([t]) => t === target)?.[1] })),
      select('Keep this group in step', groups.map((g) => [g.id, g.name]), v.groupId, (groupId) => configure(v.id, { groupId })),
      select('New tasks', [['both', 'Both ways'], ['importOnly', 'Only bring new ones in'], ['exportOnly', 'Only send new ones out']], v.direction, (direction) => configure(v.id, { direction })),
    ];
    const status = [];
    if (v.lastRun) status.push(h('p', { class: 'muted small' }, `Last sync ${new Date(v.lastRun).toLocaleString()}: ${v.lastResult ?? ''}`));
    if (v.problems?.length) status.push(h('ul', { class: 'problems' }, ...v.problems.map((p) => h('li', null, icon('alert', { size: 14 }), p))));
    const ready = !v.missing;
    const buttons = h('div', { class: 'row buttons' },
      h('button', { class: 'btn', type: 'button', disabled: !ready, onclick: async () => { preview[v.id] = await run(host.post(`${base}/${v.id}/preview`)); render(); } }, 'Preview'),
      h('button', { class: 'btn primary', type: 'button', disabled: !ready, onclick: () => run(host.post(`${base}/${v.id}/sync`), 'Synced') }, v.enabled ? 'Sync now' : 'Start syncing'),
      h('button', { class: 'btn ghost danger', type: 'button', onclick: () => { if (confirm(`Disconnect ${v.name}? Nothing is deleted on either side.`)) run(host.del(`${base}/${v.id}`), 'Disconnected'); } }, 'Disconnect'));
    return [
      ...rows,
      v.missing ? h('p', { class: 'hint' }, v.missing) : h('p', { class: 'muted small' }, v.enabled ? `Syncs every few minutes · ${v.linked} linked` : 'Look at the preview, then start syncing.'),
      preview[v.id] ? h('p', { class: 'preview', role: 'status' }, previewText(preview[v.id])) : null,
      ...status,
      buttons,
    ];
  }

  function notion(v) {
    const token = h('input', { type: 'password', placeholder: 'secret_… or ntn_…', autocomplete: 'off', 'aria-label': 'Notion integration token' });
    const hasToken = v.hasToken;
    if (hasToken && !extras.notion.databases) {
      host.api(`${base}/notion/databases`).then((d) => { extras.notion.databases = d.map((x) => [x.id, x.title]); render(); }).catch(() => {});
    }

    return h('section', { class: 'connector' },
      h('h3', null, 'Notion'),
      hasToken ? null : h('ol', { class: 'steps' },
        h('li', null, 'Create an internal integration at ', h('a', { href: 'https://www.notion.so/my-integrations', target: '_blank', rel: 'noopener' }, 'notion.so/my-integrations'), ' and copy its token.'),
        h('li', null, 'In Notion, open your tasks database › ••• › Connections › add the integration.'),
        h('li', null, 'Paste the token here.')),
      hasToken ? null : h('form', { class: 'row', onsubmit: async (e) => {
        e.preventDefault();
        const d = await run(host.put(`${base}/notion/token`, { token: token.value }), 'Connected to Notion');
        if (d) extras.notion.databases = d.map((x) => [x.id, x.title]);
        render();
      } }, token, h('button', { class: 'btn primary', type: 'submit' }, 'Connect')),
      ...(hasToken ? common(v, extras.notion.databases, 'Notion database') : []));
  }

  function mstodo(v) {
    const client = h('input', { value: v.clientId ?? '', placeholder: '00000000-0000-0000-0000-000000000000', 'aria-label': 'Client id', spellcheck: false });
    const signIn = v.signIn ?? { state: 'signedOut' };
    if (signIn.state === 'signedIn' && !extras.mstodo.lists) {
      host.api(`${base}/mstodo/lists`).then((l) => { extras.mstodo.lists = l.map((x) => [x.id, x.name]); render(); }).catch(() => {});
    }

    async function startSignIn() {
      await run(host.post(`${base}/mstodo/signin`));
      clearInterval(poll);
      poll = setInterval(async () => {
        const s = await host.api(`${base}/mstodo/signin`).catch(() => null);
        if (!s || s.state !== 'waiting') {
          clearInterval(poll);
          poll = null;
          await load();
        }
      }, 3000);
    }

    return h('section', { class: 'connector' },
      h('h3', null, 'Microsoft To Do'),
      h('form', { class: 'row', onsubmit: (e) => { e.preventDefault(); configure('mstodo', { clientId: client.value }); } },
        h('label', { class: 'field grow' }, h('span', null, 'App registration (client id)'), client),
        h('button', { class: 'btn', type: 'submit' }, 'Save')),
      v.clientId ? null : h('p', { class: 'muted small' }, 'Microsoft sign-in needs an app registration (free, about two minutes): ', h('a', { href: docs, target: '_blank', rel: 'noopener' }, 'how to make one'), '.'),
      v.clientId && signIn.state !== 'signedIn'
        ? (signIn.state === 'waiting'
          ? h('p', { class: 'signin', role: 'status' }, 'Open ', h('a', { href: signIn.verificationUri, target: '_blank', rel: 'noopener' }, signIn.verificationUri), ' and enter ', h('strong', null, signIn.userCode), '. This updates by itself.')
          : h('div', { class: 'row' }, h('button', { class: 'btn primary', type: 'button', onclick: startSignIn }, 'Sign in to Microsoft'), signIn.message ? h('span', { class: 'muted small' }, signIn.message) : null))
        : null,
      signIn.state === 'signedIn' ? h('p', { class: 'muted small' }, `Signed in${signIn.account ? ` as ${signIn.account}` : ''}.`) : null,
      ...(signIn.state === 'signedIn' ? common(v, extras.mstodo.lists, 'To Do list') : []));
  }

  function render() {
    panel.replaceChildren(
      h('p', { class: 'muted' }, 'Tasks stay in step both ways. Nothing is ever deleted: a task deleted here is archived there, and one gone there stays here.'),
      ...views.map((v) => (v.id === 'notion' ? notion(v) : mstodo(v))));
  }

  async function show() {
    open = true;
    await load();
    render();
    await host.openPanel('Connectors', panel, { iconName: 'link', onClose: () => { open = false; clearInterval(poll); poll = null; } });
  }
}
