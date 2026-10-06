// Ask AI: chat with GitHub Copilot or Claude Code inside Todo Tracker. They read and change tasks through Todo
// Tracker's own tools; reading needs no permission, changing tasks asks first, anything else always asks.

const base = '/api/plugins/agent-chat';
const statusText = {
  idle: 'Ready when you are.',
  starting: 'Starting the agent…',
  ready: '',
  busy: 'Thinking…',
  error: '',
};
const toolStatus = { pending: '…', in_progress: '…', completed: '✓', failed: '✕' };
const choiceLabels = { allow: 'Allow', 'allow-chat': 'Allow for this chat', reject: 'Don’t allow' };

export function activate(host) {
  const { h, icon } = host;
  let source = null;
  let lastStatus = null;

  const log = h('div', { class: 'chat-log', 'aria-live': 'polite' });
  const status = h('div', { class: 'chat-status muted small' });
  const picker = h('select', { class: 'chat-picker', 'aria-label': 'Agent', onchange: () => run(host.post(`${base}/select`, { agent: picker.value })) });
  const input = h('textarea', {
    rows: 2, maxlength: 8000, class: 'chat-input', 'aria-label': 'Message', placeholder: 'Ask anything – e.g. “add: call the bank tomorrow, renew passport !!”',
    onkeydown: (e) => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(); } },
  });
  const sendButton = h('button', { class: 'btn primary', type: 'button', title: 'Send (Enter)', onclick: () => send() }, icon('send', { size: 16 }), 'Send');
  const stopButton = h('button', { class: 'btn', type: 'button', hidden: true, title: 'Stop the answer', onclick: () => run(host.post(`${base}/cancel`)) }, icon('stop', { size: 14 }), 'Stop');
  const newButton = h('button', { class: 'btn ghost', type: 'button', title: 'Start a new conversation', onclick: () => run(host.post(`${base}/new`)) }, 'New chat');
  const panel = h('div', { class: 'chat' },
    h('div', { class: 'chat-top' }, picker, newButton),
    log,
    status,
    h('div', { class: 'chat-compose' }, input, h('div', { class: 'chat-actions' }, stopButton, sendButton)));

  async function run(promise) {
    try {
      render(await promise);
    } catch (err) {
      host.toast(err.message, 'error');
    }
  }

  async function send() {
    const text = input.value.trim();
    if (!text) return;
    try {
      render(await host.post(`${base}/message`, { text }));
      input.value = '';
    } catch (err) {
      host.toast(err.message, 'error');
    }
  }

  function entry(e) {
    switch (e.kind) {
      case 'user':
        return h('div', { class: 'msg user' }, e.text);
      case 'agent':
        return h('div', { class: 'msg agent' }, e.text);
      case 'tool':
        return h('div', { class: `tool ${e.status ?? ''}` }, `${toolStatus[e.status] ?? '…'} ${e.text}`);
      case 'permission':
        return h('div', { class: `ask ${e.status}` },
          h('div', null, e.text),
          e.choices
            ? h('div', { class: 'row' }, ...e.choices.map((c) => h('button', {
              class: `btn ${c === 'reject' ? 'ghost' : c === 'allow' ? 'primary' : ''}`.trim(), type: 'button',
              onclick: () => run(host.post(`${base}/answer`, { entryId: e.id, choice: c })),
            }, choiceLabels[c] ?? c)))
            : h('div', { class: 'muted small' }, { allowed: 'Allowed', rejected: 'Not allowed', expired: 'Not answered – not allowed', cancelled: 'Stopped' }[e.status] ?? e.status));
      default:
        return h('div', { class: `note ${e.status ?? ''}` }, e.text);
    }
  }

  function render(state) {
    if (!state) return;
    const installed = state.agents.filter((a) => a.installed);
    picker.replaceChildren(...installed.map((a) => h('option', { value: a.id, selected: a.id === state.agent }, a.name)));
    picker.hidden = installed.length < 2;
    const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 40;
    if (!installed.length) {
      log.replaceChildren(h('div', { class: 'note' }, 'To chat, install one of these (then reopen this panel):'),
        ...state.agents.map((a) => h('div', { class: 'note' }, h('strong', null, a.name), ' – ', a.hint ?? '')));
    } else if (!state.entries.length) {
      const name = installed.find((a) => a.id === state.agent)?.name ?? 'the agent';
      log.replaceChildren(h('div', { class: 'note muted' }, `Tell ${name} what's on your mind. It can add, find, organize and finish tasks – you'll be asked before it changes anything.`));
    } else {
      log.replaceChildren(...state.entries.map(entry));
    }
    if (atBottom) log.scrollTop = log.scrollHeight;
    const busy = state.status === 'busy' || state.status === 'starting';
    status.textContent = state.status === 'error' ? (state.problem ?? 'The agent stopped.') : statusText[state.status] ?? '';
    status.classList.toggle('error', state.status === 'error');
    stopButton.hidden = !busy;
    sendButton.disabled = busy || !installed.length;
    input.disabled = !installed.length;
    // When an answer finishes, the board may have new or changed tasks.
    if (lastStatus === 'busy' && state.status !== 'busy') host.refresh();
    lastStatus = state.status;
  }

  async function open() {
    const opened = await host.openPanel('Ask AI', panel, {
      iconName: 'chat',
      onClose: () => { source?.close(); source = null; },
    });
    if (!opened) return;
    source?.close();
    source = new EventSource(`${base}/stream`);
    source.addEventListener('state', (e) => render(JSON.parse(e.data)));
    input.focus();
  }

  host.addHeaderButton({ iconName: 'chat', label: 'Ask AI', onClick: open });
  if (new URLSearchParams(location.search).has('ask')) open();
}
