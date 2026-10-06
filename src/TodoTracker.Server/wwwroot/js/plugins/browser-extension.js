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
  let timer = null;

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

  function pairing() {
    const out = h('div', { class: 'pairing' });
    const show = async () => {
      clearInterval(timer);
      try {
        const { code, expiresAt } = await host.post('/api/plugins/browser-extension/pair');
        const left = h('span', { class: 'muted small' });
        const tick = () => {
          const seconds = Math.max(0, Math.round((new Date(expiresAt) - Date.now()) / 1000));
          left.textContent = seconds ? `works once, for ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` : 'expired – get a new one';
          if (!seconds) clearInterval(timer);
        };
        tick();
        timer = setInterval(tick, 1000);
        out.replaceChildren(h('div', { class: 'pair-code', 'aria-label': 'Pairing code' }, `${code.slice(0, 3)} ${code.slice(3)}`), left,
          h('button', { class: 'btn ghost', type: 'button', onclick: show }, 'New code'));
      } catch (err) {
        host.toast(err.message, 'error');
      }
    };
    out.append(h('button', { class: 'btn primary', type: 'button', onclick: show }, 'Get a pairing code'));
    return out;
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
    const tabs = h('div', { class: 'seg', role: 'tablist' });
    const select = (id) => {
      tabs.querySelectorAll('button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.id === id)));
      body.replaceChildren(steps(id, info.serverUrl));
    };
    tabs.append(...Object.entries(browsers).map(([id, b]) => h('button', { type: 'button', role: 'tab', dataset: { id }, onclick: () => select(id) }, b.name)));
    select(detected);
    await host.openPanel('Browser extension', h('div', null,
      h('p', { class: 'muted' }, 'See what to do now, add tasks, and save notes about the page you’re on, right from the browser. ',
        'The extension isn’t in the browser stores yet, so it’s set up by hand once – it takes about two minutes.'),
      tabs,
      body,
      h('section', { class: 'drawer-section' }, h('h3', null, 'Pairing code'), pairing())),
    { iconName: 'puzzle', onClose: () => clearInterval(timer) });
  }

  host.addHeaderButton({ iconName: 'puzzle', label: 'Browser extension', onClick: open });
  if (new URLSearchParams(location.search).has('extension')) open();
}
