// Connect AI apps: step-by-step setup for Claude, Copilot, VS Code, ChatGPT… Pick the app, copy one thing, done.

export function activate(host) {
  const { h, icon } = host;

  async function copy(snippet) {
    try {
      await navigator.clipboard.writeText(snippet);
      host.toast('Copied');
    } catch {
      host.toast('Couldn’t copy – select the text and copy it', 'error');
    }
  }

  async function open() {
    let info;
    try {
      info = await host.api('/api/plugins/connect-ai');
    } catch (err) {
      host.toast(err.message, 'error');
      return;
    }
    await host.openPanel('Connect an AI app', h('div', null,
      h('p', { class: 'muted' }, 'Let Claude, Copilot and other AI apps see what you are working on and add, organize and finish tasks for you. ',
        'Everything they change shows up here, marked with who did it, and can be undone from the history.'),
      ...info.setups.map((s, i) => h('details', { class: 'connect', open: i === 0, dataset: { id: s.id } },
        h('summary', null, s.app),
        h('p', { class: 'muted small' }, s.steps),
        h('pre', { class: 'snippet' }, h('code', null, s.snippet)),
        h('div', { class: 'row' },
          h('button', { class: 'btn', type: 'button', onclick: () => copy(s.snippet) }, icon('note', { size: 16 }), 'Copy'),
          s.link ? h('a', { class: 'link', href: s.link, target: '_blank', rel: 'noopener noreferrer' }, 'How to set it up') : null)))),
    { iconName: 'sparkles' });
  }

  host.addHeaderButton({ iconName: 'sparkles', label: 'Connect an AI app', onClick: open });
  if (new URLSearchParams(location.search).has('connect')) open();
}
