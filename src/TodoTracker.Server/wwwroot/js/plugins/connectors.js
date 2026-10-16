// Connectors: keep a group in step with a Notion database, a Microsoft To Do list or a GitHub repository's issues, both
// ways. Each is its own plugin
// (off until switched on in Plugins); this one panel sets them up: sign in, pick the list and the group, look at what
// the first sync will do, then sync (and from then on every few minutes).

const base = '/api/plugins/connectors';
const githubBase = '/api/plugins/connector-github';
const docs = 'https://github.com/shahabmokhtari/todo-tracker/blob/master/docs/connectors.md';

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

/** Settings › Connected apps: a card per connector, with its own switch, then its setup. */
export function createConnectorsPanel(host) {
  const { h, icon } = host;
  let views = [];
  let github = null;
  let preview = {};
  const open = true;
  const extras = { notion: { databases: null }, mstodo: { lists: null } };
  let poll = null;

  const panel = h('div', { class: 'connectors' });

  async function load() {
    try {
      [views, github] = await Promise.all([host.api(base), host.api(githubBase).catch(() => null)]);
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
      h('button', { class: 'btn ghost danger', type: 'button', onclick: () => {
        if (!confirm(`Disconnect ${v.name}? Nothing is deleted on either side.`)) return;
        // Another account may connect next: forget this one's databases and lists.
        extras.notion.databases = null;
        extras.mstodo.lists = null;
        delete preview[v.id];
        run(host.del(`${base}/${v.id}`), 'Disconnected');
      } }, 'Disconnect'));
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
          ? h('p', { class: 'signin', role: 'status' }, 'Open ', /^https:\/\//.test(signIn.verificationUri ?? '') ? h('a', { href: signIn.verificationUri, target: '_blank', rel: 'noopener' }, signIn.verificationUri) : 'microsoft.com/devicelogin', ' and enter ', h('strong', null, signIn.userCode), '. This updates by itself.')
          : h('div', { class: 'row' }, h('button', { class: 'btn primary', type: 'button', onclick: startSignIn }, 'Sign in to Microsoft'), signIn.message ? h('span', { class: 'muted small' }, signIn.message) : null))
        : null,
      signIn.state === 'signedIn' ? h('p', { class: 'muted small' }, `Signed in${signIn.account ? ` as ${signIn.account}` : ''}.`) : null,
      ...(signIn.state === 'signedIn' ? common(v, extras.mstodo.lists, 'To Do list') : []));
  }

  const about = {
    notion: 'Keep a group in step with a Notion database, both ways.',
    mstodo: 'Keep a group in step with a Microsoft To Do list, both ways.',
  };

  /** A connector's card: its switch (applies at once), then its setup while it's on. */
  function card(v) {
    const toggle = h('input', {
      type: 'checkbox', checked: v.on, 'aria-label': `Use ${v.name}`,
      onchange: () => run(host.put(`/api/plugins/${encodeURIComponent(v.pluginId)}`, { enabled: toggle.checked }), toggle.checked ? `${v.name} is on` : `${v.name} is off`),
    });
    const head = h('label', { class: 'connector-head' }, toggle, h('span', null, h('strong', null, v.name), h('span', { class: 'muted small' }, about[v.id] ?? '')));
    if (!v.on) return h('section', { class: 'connector off' }, head);
    const body = v.id === 'notion' ? notion(v) : mstodo(v);
    body.prepend(head);
    body.querySelector('h3')?.remove();
    return body;
  }

  /**
   * GitHub Issues: each group with its own private repository (made with the GitHub CLI, so nothing to paste), or a
   * repository you pick. Snoozes, reminders and the rest travel as labels (due:, snooze:, remind:…).
   */
  function githubCard(g) {
    // While the tasks live on GitHub it stays on (move them back first).
    const locked = g.mainPlace || g.mainPlaceAsked;
    const toggle = h('input', {
      type: 'checkbox', checked: g.on, 'aria-label': 'Use GitHub Issues', disabled: locked && g.on,
      title: locked ? 'Your tasks live on GitHub: untick "Keep my tasks on GitHub" first' : '',
      onchange: () => run(host.put('/api/plugins/connector-github', { enabled: toggle.checked }), toggle.checked ? 'GitHub Issues is on' : 'GitHub Issues is off'),
    });
    const head = h('label', { class: 'connector-head' }, toggle, h('span', null, h('strong', null, 'GitHub Issues'),
      h('span', { class: 'muted small' }, 'Each group in its own private repository: tasks are issues, both ways.')));
    if (!g.on) return h('section', { class: 'connector off' }, head);
    const ready = !g.missing;
    const every = h('input', {
      type: 'checkbox', checked: g.everyGroup, disabled: !ready || g.mainPlace, 'aria-label': 'Every group gets its own repository',
      onchange: async () => {
        await run(host.put(`${githubBase}/every-group`, { on: every.checked }), every.checked ? 'Making the repositories (a moment each)…' : null);
        // They're made in the background: look again in a bit.
        if (every.checked) [5000, 15000, 40000].forEach((ms) => setTimeout(load, ms));
      },
    });
    const row = (x) => {
      const id = encodeURIComponent(x.groupId);
      const repoLink = x.repo ? h('a', { href: `https://github.com/${x.repo}/issues`, target: '_blank', rel: 'noopener' }, x.repo) : h('span', { class: 'muted' }, 'not connected');
      const other = () => {
        const repo = prompt(`Keep "${x.group}" in step with which repository? (owner/name)`, x.repo ?? '');
        if (repo) run(host.post(`${githubBase}/groups/${id}/connect`, { repo }), `${x.group} is in step with ${repo}`);
      };
      return h('li', { class: 'github-group' },
        h('div', { class: 'row' },
          h('strong', null, x.group), repoLink,
          x.repo
            ? h('span', { class: 'buttons' },
              h('button', { class: 'btn small', type: 'button', disabled: !ready, onclick: () => run(host.post(`${githubBase}/groups/${id}/sync`), 'Synced') }, 'Sync now'),
              g.mainPlace ? null : h('button', { class: 'btn small ghost', type: 'button', onclick: () => {
                if (confirm(`Stop keeping "${x.group}" in step with ${x.repo}? Nothing is deleted on either side.`)) run(host.del(`${githubBase}/groups/${id}`), 'Stopped');
              } }, 'Stop'))
            : h('span', { class: 'buttons' },
              h('button', { class: 'btn small primary', type: 'button', disabled: !ready, title: 'Makes a private repository for this group with the GitHub CLI', onclick: () => run(host.post(`${githubBase}/groups/${id}/connect`, {}), `${x.group} has its repository`) }, 'Make its repository'),
              h('button', { class: 'btn small ghost', type: 'button', disabled: !ready, onclick: other }, 'Use another…'))),
        x.lastRun ? h('p', { class: 'muted small' }, `Last sync ${new Date(x.lastRun).toLocaleString()}: ${x.lastResult ?? ''}${x.repo ? ` · ${x.linked} linked` : ''}`) : null,
        x.problems?.length ? h('ul', { class: 'problems' }, ...x.problems.map((p) => h('li', null, icon('alert', { size: 14 }), p))) : null);
    };
    // Where the tasks live: the tasks folder, or GitHub only (the app keeps its own copy).
    const main = h('input', {
      type: 'checkbox', checked: g.mainPlaceAsked, disabled: !ready && !g.mainPlaceAsked, 'aria-label': 'Keep my tasks on GitHub, without a tasks folder',
      onchange: async () => {
        const on = main.checked;
        const question = on
          ? 'Keep your tasks on GitHub instead of a tasks folder?\n\nEvery group gets its own private repository, and the app keeps its copy in its own folder. Your tasks folder is left as it is. Takes effect when Todo Tracker restarts.'
          : 'Keep your tasks in a tasks folder again?\n\nThe app\'s copy goes into your tasks folder (what\'s there now is kept next to it, renamed). The repositories stay on GitHub. Takes effect when Todo Tracker restarts.';
        if (!confirm(question)) {
          main.checked = !on;
          return;
        }

        const result = await run(host.put(`${githubBase}/main-place`, { on }));
        if (result) {
          host.toast(`${on ? 'Your tasks will live on GitHub' : 'Your tasks will live in the tasks folder again'}: restart Todo Tracker to switch.`);
        } else {
          main.checked = !on;
        }
      },
    });
    return h('section', { class: 'connector' }, head,
      ready
        ? h('p', { class: 'muted small' }, `Signed in${g.account ? ` as ${g.account}` : ''} with the GitHub CLI. Snoozes, reminders, the board column, labels, icon and color travel as issue labels (due:, snooze:, remind:, stage:…).`)
        : h('p', { class: 'hint' }, g.missing),
      h('label', { class: 'check' }, every, h('span', null, g.mainPlace ? 'Every group has its own repository (GitHub is where your tasks live)' : 'Every group gets its own private repository (new groups too)')),
      h('label', { class: 'check' }, main, h('span', null, 'Keep my tasks on GitHub, without a tasks folder',
        h('span', { class: 'muted small' }, ' (other computers find the repositories and bring the tasks in)'))),
      g.mainPlaceAsked !== g.mainPlace ? h('p', { class: 'hint' }, 'Restart Todo Tracker to finish moving your tasks.') : null,
      g.problems?.length ? h('ul', { class: 'problems' }, ...g.problems.map((p) => h('li', null, icon('alert', { size: 14 }), p))) : null,
      h('ul', { class: 'github-groups' }, ...g.groups.map(row)));
  }

  function render() {
    panel.replaceChildren(
      h('p', { class: 'muted' }, 'Tasks stay in step both ways. Nothing is ever deleted: a task deleted here is archived there, and one gone there stays here. ', h('a', { href: docs, target: '_blank', rel: 'noopener' }, 'How it works')),
      ...views.map(card),
      github ? githubCard(github) : null);
  }

  return {
    element: panel,
    async load() {
      await load();
      render();
    },
    stop() {
      clearInterval(poll);
      poll = null;
    },
  };
}
