// Ask AI: chats with GitHub Copilot or Claude Code (over ACP), or with a model reached with an API key. They read and
// change tasks through Todo Tracker's own tools; reading needs no permission, changing tasks asks first, anything else
// always asks. Every chat is kept: open, search, rename or delete (with undo) from the list.

const base = '/api/plugins/agent-chat';
const statusText = {
  idle: 'Ready when you are.',
  starting: 'Starting the agent…',
  ready: '',
  busy: 'Thinking…',
  error: '',
};
const toolStatus = { pending: '…', in_progress: '…', completed: '✓', failed: '✕', rejected: '✕', cancelled: '–' };
const choiceLabels = { allow: 'Allow', 'allow-chat': 'Allow for this chat', reject: 'Don’t allow' };
const ADD_MODEL = '__add-model';

/** Ready-made settings for the services people use; "compatible" ones are for anything else that speaks the same API. */
export const presets = [
  { id: 'openai', name: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4.1', key: true },
  { id: 'anthropic', name: 'Anthropic (Claude)', baseUrl: 'https://api.anthropic.com', model: 'claude-sonnet-4-5', key: true },
  { id: 'azure', name: 'Azure OpenAI', baseUrl: '', baseUrlHint: 'https://YOUR-RESOURCE.openai.azure.com', model: '', modelHint: 'Deployment name', key: true },
  { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'openai/gpt-4.1-mini', key: true },
  { id: 'ollama', name: 'Ollama (on this computer)', baseUrl: 'http://127.0.0.1:11434/v1', model: 'llama3.2', key: false },
  { id: 'lmstudio', name: 'LM Studio (on this computer)', baseUrl: 'http://127.0.0.1:1234/v1', model: '', modelHint: 'Model id shown in LM Studio', key: false },
  { id: 'openai-compatible', name: 'Other (OpenAI-compatible)', baseUrl: '', model: '', key: true },
  { id: 'anthropic-compatible', name: 'Other (Anthropic-compatible)', baseUrl: '', model: '', key: true },
];

/** The picker's groups: installed agents, then API models. */
export function groupAgents(agents) {
  return {
    agents: agents.filter((a) => a.kind !== 'api' && a.installed),
    models: agents.filter((a) => a.kind === 'api'),
  };
}

/** "just now", "5 min ago", "3 h ago", "yesterday", or the date. */
export function ago(when, now = new Date()) {
  const minutes = Math.round((now - new Date(when)) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 24 * 60) return `${Math.round(minutes / 60)} h ago`;
  if (minutes < 48 * 60) return 'yesterday';
  return new Date(when).toLocaleDateString();
}

/** Where a model's messages go, said plainly (null for one on this computer). */
export function privacyNote(agent, baseUrl) {
  if (!agent || agent.kind !== 'api' || agent.local) return null;
  let host = 'the model’s service';
  try {
    if (baseUrl) host = new URL(baseUrl).host;
  } catch {
    // keep the general wording
  }
  return `Your messages, and the tasks ${agent.name} looks at, are sent to ${host}.`;
}

export function activate(host) {
  const { h, icon } = host;
  let source = null;
  let lastStatus = null;
  // States can arrive out of order (a POST's reply after a newer streamed one): only newer ones are shown.
  let shown = 0;
  let epoch = null;
  let busy = false;
  let current = null;
  let chatsVersion = -1;
  let models = [];
  let view = 'chat'; // chat | chats | add-model

  const log = h('div', { class: 'chat-log', 'aria-live': 'polite' });
  const status = h('div', { class: 'chat-status muted small', role: 'status' });
  const notice = h('div', { class: 'chat-notice muted small', hidden: true });
  const picker = h('select', {
    class: 'chat-picker', 'aria-label': 'Chat with',
    onchange: () => {
      if (picker.value === ADD_MODEL) {
        picker.value = current?.agent ?? '';
        show('add-model');
        return;
      }
      run(host.post(`${base}/select`, { agent: picker.value }));
    },
  });
  const input = h('textarea', {
    rows: 2, maxlength: 8000, class: 'chat-input', 'aria-label': 'Message', placeholder: 'Ask anything – e.g. “add: call the bank tomorrow, renew passport !!”',
    onkeydown: (e) => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); if (!busy) send(); } },
  });
  const sendButton = h('button', { class: 'btn primary', type: 'button', title: 'Send (Enter)', onclick: () => send() }, icon('send', { size: 16 }), 'Send');
  const stopButton = h('button', { class: 'btn', type: 'button', hidden: true, title: 'Stop the answer', onclick: () => run(host.post(`${base}/cancel`)) }, icon('stop', { size: 14 }), 'Stop');
  const newButton = h('button', { class: 'btn ghost', type: 'button', title: 'Start a new chat (this one is kept)', onclick: () => { show('chat'); run(host.post(`${base}/new`)); } }, icon('plus', { size: 14 }), 'New');
  const chatsButton = h('button', { class: 'btn ghost', type: 'button', 'aria-pressed': 'false', title: 'Your chats', onclick: () => show(view === 'chats' ? 'chat' : 'chats') }, icon('history', { size: 14 }), 'Chats');
  const compose = h('div', { class: 'chat-compose' }, input, h('div', { class: 'chat-actions' }, stopButton, sendButton));

  // The list of chats: search, open, rename, delete (with undo).
  const search = h('input', {
    type: 'search', class: 'chat-search', placeholder: 'Search chats', 'aria-label': 'Search chats',
    oninput: () => { clearTimeout(searchTimer); searchTimer = setTimeout(loadChats, 200); },
  });
  let searchTimer = null;
  let undoTimer = null;
  // Each list load gets a number: a slower answer for an earlier search never replaces a newer one.
  let listRequest = 0;
  const chatList = h('ul', { class: 'chat-list' });
  const undo = h('div', { class: 'chat-undo', role: 'status', hidden: true });
  const chatsPane = h('div', { class: 'chat-pane', hidden: true }, search, undo, chatList);

  // Adding an API model.
  const addPane = h('div', { class: 'chat-pane', hidden: true });

  const panel = h('div', { class: 'chat' },
    h('div', { class: 'chat-top' }, chatsButton, picker, newButton),
    notice,
    chatsPane,
    addPane,
    log,
    status,
    compose);

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

  function show(next) {
    view = next;
    chatsPane.hidden = view !== 'chats';
    addPane.hidden = view !== 'add-model';
    log.hidden = view !== 'chat';
    compose.hidden = view !== 'chat';
    status.hidden = view !== 'chat';
    chatsButton.setAttribute('aria-pressed', String(view === 'chats'));
    if (view === 'chats') {
      loadChats();
      search.focus();
    } else if (view === 'add-model') {
      renderAddModel();
    } else {
      input.focus();
    }
  }

  async function loadChats() {
    const q = search.value.trim();
    const mine = ++listRequest;
    let chats;
    try {
      chats = await host.api(`${base}/chats${q ? `?q=${encodeURIComponent(q)}` : ''}`);
    } catch (err) {
      host.toast(err.message, 'error');
      return;
    }
    if (mine !== listRequest) return;
    const names = new Map((current?.agents ?? []).map((a) => [a.id, a.name]));
    chatList.replaceChildren(...(chats.length ? chats.map((c) => chatItem(c, names)) : [h('li', { class: 'muted small' }, q ? 'No chat mentions that.' : 'No chats yet.')]));
  }

  function chatItem(c, names) {
    const open = h('button', {
      class: `chat-item${c.id === current?.chatId ? ' current' : ''}`, type: 'button', disabled: busy,
      title: busy ? 'Stop the answer first' : null,
      onclick: async () => { await run(host.post(`${base}/chats/${c.id}/open`)); show('chat'); },
    },
    h('span', { class: 'chat-title' }, c.title),
    h('span', { class: 'muted small' }, `${names.get(c.agent) ?? (c.agent.startsWith('api:') ? 'a removed model' : c.agent)} · ${ago(c.updatedAt)}`),
    c.snippet ? h('span', { class: 'muted small chat-snippet' }, c.snippet) : null);
    const rename = h('button', {
      class: 'icon-btn', type: 'button', title: 'Rename', 'aria-label': `Rename ${c.title}`,
      onclick: () => {
        const field = h('input', { type: 'text', value: c.title, maxlength: 60, 'aria-label': 'Chat name', class: 'chat-rename' });
        let finished = false;
        const done = async (save) => {
          if (finished) return;
          finished = true;
          if (save && field.value.trim() && field.value.trim() !== c.title) await run(host.put(`${base}/chats/${c.id}`, { title: field.value }));
          loadChats();
        };
        field.addEventListener('keydown', (e) => {
          if (e.key === 'Enter') { e.preventDefault(); done(true); }
          if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(false); }
        });
        field.addEventListener('blur', () => done(true), { once: true });
        open.replaceWith(field);
        field.select();
      },
    }, icon('pencil', { size: 14 }));
    const remove = h('button', {
      class: 'icon-btn', type: 'button', title: 'Delete', 'aria-label': `Delete ${c.title}`,
      onclick: async () => {
        await run(host.del(`${base}/chats/${c.id}`));
        undo.hidden = false;
        clearTimeout(undoTimer);
        undoTimer = setTimeout(() => { undo.hidden = true; }, 10000);
        undo.replaceChildren(h('span', null, `Deleted “${c.title}”.`), h('button', {
          class: 'btn ghost', type: 'button',
          onclick: async () => { undo.hidden = true; await run(host.post(`${base}/chats/${c.id}/restore`)); loadChats(); },
        }, icon('undo', { size: 14 }), 'Undo'));
        loadChats();
      },
    }, icon('trash', { size: 14 }));
    return h('li', null, open, h('span', { class: 'chat-item-actions' }, rename, remove));
  }

  async function loadModels() {
    try {
      models = await host.api(`${base}/models`);
    } catch {
      models = [];
    }
  }

  function renderAddModel() {
    const kind = h('select', { 'aria-label': 'Service' }, ...presets.map((p) => h('option', { value: p.id }, p.name)));
    const name = h('input', { type: 'text', placeholder: 'Name (optional)', 'aria-label': 'Name' });
    const baseUrl = h('input', { type: 'url', 'aria-label': 'Address', required: true });
    const model = h('input', { type: 'text', 'aria-label': 'Model', required: true });
    const key = h('input', { type: 'password', autocomplete: 'off', 'aria-label': 'API key' });
    const keyRow = h('label', null, 'API key', key, h('span', { class: 'muted small' }, 'Kept on this computer only (encrypted for your account on Windows) and never shown again.'));
    const apply = () => {
      const p = presets.find((x) => x.id === kind.value);
      baseUrl.value = p.baseUrl;
      model.value = p.model;
      model.placeholder = p.modelHint ?? 'Model';
      baseUrl.placeholder = p.baseUrlHint ?? (p.baseUrl || 'https://…');
      keyRow.hidden = !p.key;
    };
    kind.addEventListener('change', apply);
    apply();
    const form = h('form', {
      class: 'chat-model-form',
      onsubmit: async (e) => {
        e.preventDefault();
        try {
          const added = await host.post(`${base}/models`, { preset: kind.value, name: name.value, baseUrl: baseUrl.value, model: model.value, key: keyRow.hidden ? null : key.value });
          key.value = '';
          host.toast(`Added ${added.name}`);
          await loadModels();
          await run(host.post(`${base}/select`, { agent: `api:${added.id}` }));
          show('chat');
        } catch (err) {
          host.toast(err.message, 'error');
        }
      },
    },
    h('label', null, 'Service', kind),
    h('label', null, 'Address', baseUrl),
    h('label', null, 'Model', model),
    keyRow,
    h('label', null, 'Name', name),
    h('div', { class: 'row' }, h('button', { class: 'btn primary', type: 'submit' }, 'Add model'), h('button', { class: 'btn ghost', type: 'button', onclick: () => show('chat') }, 'Cancel')));
    const list = h('ul', { class: 'chat-models' }, ...models.map((m) => h('li', null,
      h('span', null, h('strong', null, m.name), h('span', { class: 'muted small' }, ` · ${m.model} · ${m.local ? 'on this computer' : new URL(m.baseUrl).host}`)),
      h('button', {
        class: 'btn ghost', type: 'button', 'aria-label': `Remove ${m.name}`,
        onclick: async () => {
          try {
            await host.del(`${base}/models/${m.id}`);
            await loadModels();
            renderAddModel();
          } catch (err) {
            host.toast(err.message, 'error');
          }
        },
      }, 'Remove'))));
    addPane.replaceChildren(h('h3', null, 'Add a model with an API key'),
      h('p', { class: 'muted small' }, 'OpenAI, Anthropic, Azure, OpenRouter, or a model on this computer (Ollama, LM Studio). It gets the same task tools as the agents and asks before changing anything.'),
      form,
      models.length ? h('h3', null, 'Your models') : null,
      models.length ? list : null);
    kind.focus();
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

  function renderPicker(state) {
    const { agents, models: apiModels } = groupAgents(state.agents);
    const option = (a) => h('option', { value: a.id, selected: a.id === state.agent }, a.name);
    picker.replaceChildren(
      ...(agents.length ? [h('optgroup', { label: 'Agents' }, ...agents.map(option))] : []),
      ...(apiModels.length ? [h('optgroup', { label: 'API models' }, ...apiModels.map(option))] : []),
      h('option', { value: ADD_MODEL }, '+ Add a model with an API key…'));
    // A chat with someone no longer available still shows who it was with.
    if (state.agent && !state.agents.some((a) => a.id === state.agent && a.installed)) {
      picker.prepend(h('option', { value: state.agent, selected: true, disabled: true }, 'Not available'));
    }
  }

  function render(state) {
    if (!state) return;
    // A restarted app counts versions from the start again.
    if (state.epoch !== epoch) {
      epoch = state.epoch;
      shown = 0;
    }
    if ((state.version ?? 0) < shown) return;
    shown = state.version ?? 0;
    current = state;
    renderPicker(state);
    const available = state.agents.filter((a) => a.installed);
    const selected = state.agents.find((a) => a.id === state.agent);
    const note = privacyNote(selected, models.find((m) => `api:${m.id}` === state.agent)?.baseUrl);
    notice.hidden = !note;
    notice.textContent = note ?? '';
    const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 40;
    if (!available.length) {
      log.replaceChildren(h('div', { class: 'note' }, 'To chat, install one of these (then reopen this panel), or add a model with an API key:'),
        ...state.agents.filter((a) => a.kind !== 'api').map((a) => h('div', { class: 'note' }, h('strong', null, a.name), ' – ', a.hint ?? '')),
        h('button', { class: 'btn', type: 'button', onclick: () => show('add-model') }, icon('plus', { size: 14 }), 'Add a model'));
    } else if (!state.entries.length) {
      const name = selected?.name ?? 'the agent';
      log.replaceChildren(h('div', { class: 'note muted' }, `Tell ${name} what's on your mind. It can add, find, organize and finish tasks – you'll be asked before it changes anything. Your chats are kept (Chats).`));
    } else {
      log.replaceChildren(...state.entries.map(entry));
    }
    if (atBottom) log.scrollTop = log.scrollHeight;
    busy = state.status === 'busy' || state.status === 'starting';
    status.textContent = state.status === 'error' ? (state.problem ?? 'The agent stopped.') : state.problem ?? statusText[state.status] ?? '';
    status.classList.toggle('error', state.status === 'error');
    stopButton.hidden = !busy;
    sendButton.disabled = busy || !available.length;
    // Starting over or switching waits for the answer: stop it first.
    newButton.disabled = busy;
    picker.disabled = busy;
    chatsButton.disabled = busy;
    input.disabled = !available.length;
    // The list of chats changed (a chat was kept, renamed, deleted…): refresh it if it's showing.
    if (state.chatsVersion !== chatsVersion) {
      chatsVersion = state.chatsVersion;
      if (view === 'chats') loadChats();
    }
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
    shown = 0;
    show('chat');
    await loadModels();
    run(host.api(base)); // also looks for newly installed agents
    source = new EventSource(`${base}/stream`);
    source.addEventListener('state', (e) => render(JSON.parse(e.data)));
  }

  host.addHeaderButton({ iconName: 'chat', label: 'Ask AI', onClick: open });
  // #/ask (the desktop's "open the whole chat") opens the chat over the view on screen, without reloading the app.
  const askRoute = () => {
    if (location.hash !== '#/ask') return;
    history.replaceState(null, '', `#/${localStorage.getItem('tt.view') || 'today'}`);
    open();
  };
  window.addEventListener('hashchange', askRoute);
  if (new URLSearchParams(location.search).has('ask')) open();
  else askRoute();
}
