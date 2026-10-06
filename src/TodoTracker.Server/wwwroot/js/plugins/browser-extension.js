// Browser extension: a step-by-step setup for Edge, Chrome and Safari. Until the extension is in the stores it's
// loaded by hand ("unpacked"); once it is, this becomes a single store link.

const browsers = {
  edge: { name: 'Microsoft Edge', page: 'edge://extensions', pack: 'chromium' },
  chrome: { name: 'Google Chrome', page: 'chrome://extensions', pack: 'chromium' },
  safari: { name: 'Safari (Mac)', pack: 'safari' },
};

/** Which browser this page is open in (Edge also says "Chrome"; Chrome also says "Safari"). */
export function detectBrowser(userAgent = navigator.userAgent) {
  if (/Edg\//.test(userAgent)) return 'edge';
  if (/Chrome\//.test(userAgent) || /Chromium\//.test(userAgent)) return 'chrome';
  if (/Safari\//.test(userAgent)) return 'safari';
  return 'chrome';
}

export function activate(host) {
  const { h, icon } = host;

  async function copy(text, done = 'Copied') {
    try {
      await navigator.clipboard.writeText(text);
      host.toast(done);
    } catch {
      host.toast('Couldn’t copy – select the text and copy it', 'error');
    }
  }

  function steps(id, serverUrl) {
    const b = browsers[id];
    const download = h('a', { class: 'btn primary', href: `/api/plugins/browser-extension/download/${b.pack}`, download: '' }, icon('paperclip', { size: 16 }), 'Download the extension');
    const step = (title, ...body) => h('li', null, h('strong', null, title), ...body.map((x) => (typeof x === 'string' ? h('p', { class: 'muted small' }, x) : x)));
    if (b.pack === 'safari') {
      return h('ol', { class: 'setup-steps' },
        step('Get the Safari extension', 'Safari extensions come inside a small Mac app. Until it is in the App Store: use “Todo Tracker Extension.app” from the Mac build, or make it from the download below with Xcode installed:',
          download, h('pre', { class: 'snippet' }, h('code', null, 'xcrun safari-web-extension-converter todo-tracker-extension-safari --macos-only --app-name "Todo Tracker Extension"'))),
        step('Let the Mac open it', 'A downloaded app that isn’t notarized is blocked the first time: right-click it › Open › Open (or System Settings › Privacy & Security › Open Anyway). In Terminal this does the same:',
          h('pre', { class: 'snippet' }, h('code', null, 'xattr -dr com.apple.quarantine "Todo Tracker Extension.app"'))),
        step('Allow extensions that aren’t from the App Store', 'Safari › Settings › Advanced: turn on “Show features for web developers”. Then Develop › “Allow Unsigned Extensions” (Safari asks again after it restarts until the extension is signed).'),
        step('Turn it on', 'Open “Todo Tracker Extension.app” once, then Safari › Settings › Extensions: tick Todo Tracker and allow it on 127.0.0.1.'),
        step('Pair it', 'Click the Todo Tracker button in the toolbar and type the code below.'));
    }
    return h('ol', { class: 'setup-steps' },
      step('Download it and unzip it', 'Unzip to a folder you keep (for example Documents › Todo Tracker extension): the browser loads it from there.', download),
      step(`Open ${b.page}`, 'Browsers don’t let pages open this one: copy it into the address bar.',
        h('button', { class: 'btn', type: 'button', onclick: () => copy(b.page, 'Copied – paste it into the address bar') }, icon('note', { size: 16 }), `Copy ${b.page}`)),
      step('Turn on Developer mode', id === 'edge' ? 'The switch is in the left column (Edge may later ask about developer-mode extensions: choose Keep).' : 'The switch is at the top right.'),
      step('Load unpacked', 'Choose “Load unpacked” and pick the folder you unzipped (the one with manifest.json in it).'),
      step('Pin it and open it', 'Click the puzzle piece in the toolbar, pin Todo Tracker, then click it: the side panel opens.'),
      step('Pair it', `Type the code below in the panel (it connects to ${serverUrl}).`));
  }

  function pairing(onPaired) {
    const out = h('div', { class: 'pairing' });
    // Announces "Paired with …" (and expiry) to screen readers as well as showing it.
    const status = h('p', { class: 'muted small', role: 'status', 'aria-live': 'polite' });
    // Each code shown gets a generation; a slower answer for an older one (double click, closed panel) is dropped,
    // and its timers are always the ones stopped.
    let generation = 0;
    let handles = [];
    const stop = () => {
      generation++;
      handles.forEach(clearInterval);
      handles = [];
    };
    const show = async () => {
      stop();
      const mine = generation;
      let pairing;
      try {
        pairing = await host.post('/api/plugins/browser-extension/pair');
      } catch (err) {
        host.toast(err.message, 'error');
        return;
      }
      if (mine !== generation) return;
      const { code, expiresAt, id } = pairing;
      const left = h('span', { class: 'muted small' });
      const tick = () => {
        const seconds = Math.max(0, Math.round((new Date(expiresAt) - Date.now()) / 1000));
        left.textContent = seconds ? `works once, for ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` : '';
        if (!seconds && mine === generation) {
          stop();
          status.textContent = 'That code expired – get a new one.';
        }
      };
      const poll = async () => {
        let answer;
        try {
          answer = await host.api(`/api/plugins/browser-extension/pair?id=${encodeURIComponent(id)}`);
        } catch {
          return; // The next poll tries again.
        }
        if (mine !== generation) return;
        if (answer.state === 'none') {
          stop();
          status.textContent = 'That code isn’t valid any more (a newer one was made) – get a new one.';
          return;
        }
        if (answer.state !== 'paired') return;
        stop();
        out.replaceChildren(h('p', { class: 'paired' }, icon('check', { size: 16 }), ` Paired with ${answer.browser}.`), status,
          h('button', { class: 'btn ghost', type: 'button', onclick: show }, 'Pair another browser'));
        status.textContent = `Paired with ${answer.browser}.`;
        onPaired();
      };
      tick();
      handles = [setInterval(tick, 1000), setInterval(poll, 2000)];
      status.textContent = '';
      out.replaceChildren(h('p', { class: 'muted small', id: 'pair-code-label' }, 'Type this code in the extension:'),
        h('div', { class: 'pair-code', 'aria-labelledby': 'pair-code-label' }, `${code.slice(0, 3)} ${code.slice(3)}`), left, status,
        h('button', { class: 'btn ghost', type: 'button', onclick: show }, 'New code'));
    };
    out.append(h('button', { class: 'btn primary', type: 'button', onclick: show }, 'Get a pairing code'), status);
    return { node: out, stop };
  }

  /** Browsers paired so far, each with its own access that can be taken back here. */
  function devices() {
    const list = h('ul', { class: 'paired-browsers' });
    const load = async () => {
      let paired = [];
      try {
        paired = await host.api('/api/plugins/browser-extension/devices');
      } catch (err) {
        host.toast(err.message, 'error');
      }
      list.replaceChildren(...(paired.length ? paired.map((d) => h('li', null,
        h('span', null, d.name, h('span', { class: 'muted small' }, ` · paired ${new Date(d.pairedAt).toLocaleDateString()}`)),
        h('button', {
          class: 'btn ghost', type: 'button', 'aria-label': `Remove ${d.name}`,
          onclick: async () => {
            try {
              await host.del(`/api/plugins/browser-extension/devices/${encodeURIComponent(d.id)}`);
              host.toast(`${d.name} can’t reach Todo Tracker any more`);
              load();
            } catch (err) {
              host.toast(err.message, 'error');
            }
          },
        }, 'Remove'))) : [h('li', { class: 'muted small' }, 'None yet.')]));
    };
    load();
    return { node: list, load };
  }

  async function open() {
    let info;
    try {
      info = await host.api('/api/plugins/browser-extension');
    } catch (err) {
      host.toast(err.message, 'error');
      return;
    }
    const detected = detectBrowser();
    const body = h('div', { class: 'browser-setup' });
    const tabs = h('div', { class: 'seg', role: 'group', 'aria-label': 'Your browser' });
    const select = (id) => {
      tabs.querySelectorAll('button').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.id === id)));
      body.replaceChildren(steps(id, info.serverUrl));
    };
    tabs.append(...Object.entries(browsers).map(([id, b]) => h('button', { type: 'button', dataset: { id }, onclick: () => select(id) }, b.name)));
    select(detected);
    const paired = devices();
    const code = pairing(paired.load);
    await host.openPanel('Browser extension', h('div', null,
      h('p', { class: 'muted' }, 'See what to do now, add tasks, and save notes about the page you’re on, right from the browser. ',
        'The extension isn’t in the browser stores yet, so it’s set up by hand once – it takes about two minutes.'),
      tabs,
      body,
      h('section', { class: 'drawer-section' }, h('h3', null, 'Pairing code'), code.node),
      h('section', { class: 'drawer-section' }, h('h3', null, 'Paired browsers'),
        h('p', { class: 'muted small' }, 'Each browser gets its own access, only to what the extension does (see, add, finish, snooze, notes). Remove one you no longer use.'),
        paired.node)),
    { iconName: 'puzzle', onClose: code.stop });
  }

  host.addHeaderButton({ iconName: 'puzzle', label: 'Browser extension', onClick: open });
  if (new URLSearchParams(location.search).has('extension')) open();
}
