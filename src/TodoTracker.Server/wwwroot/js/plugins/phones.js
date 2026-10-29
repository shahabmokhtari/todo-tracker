// Settings › Phones: let the iPhone, iPad and Android apps reach the tasks on this computer (over Wi-Fi or Tailscale),
// pair each phone with a QR code, and disconnect one you no longer use. Only the person at this computer sees it.

const BASE = '/api/plugins/phones';

/** What to say about "Let phones connect" now: on, waiting for a restart, or what got in the way. */
export function phoneStatusLine(status) {
  if (status.problem && status.letPhonesConnect) return status.problem;
  if (status.letPhonesConnect && !status.listening) return 'Restart Todo Tracker so phones can connect.';
  if (!status.letPhonesConnect && status.listening) return 'Phones can’t connect any more. Todo Tracker stops listening for them when it restarts.';
  if (!status.letPhonesConnect) return 'Off: phones can’t reach this computer.';
  if (!status.addresses.length) return 'On, but this computer isn’t on a network a phone can reach. Connect it to Wi-Fi (or Tailscale).';
  return `On: phones on the same Wi-Fi or Tailscale reach this computer at ${status.addresses.join(', ')}.`;
}

/** "paired 3 Mar · last used 2 min ago" for a paired phone. */
export function pairedLine(device, now = Date.now()) {
  const paired = `paired ${new Date(device.pairedAt).toLocaleDateString()}`;
  if (!device.lastUsed) return `${paired} · not used yet`;
  const minutes = Math.max(0, Math.round((now - new Date(device.lastUsed)) / 60000));
  const used = minutes < 1 ? 'just now' : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 24 ? `${Math.round(minutes / 60)} h ago` : new Date(device.lastUsed).toLocaleDateString();
  return `${paired} · last used ${used}`;
}

/** The Phones section of Settings: `element` to place, `load()` to fill it, `stop()` when Settings closes. */
export function createPhonesSection(ctx) {
  const { h, icon } = ctx;
  const element = h('div', { class: 'phones' });
  let generation = 0;
  let handles = [];
  const stop = () => {
    generation++;
    handles.forEach(clearInterval);
    handles = [];
  };

  async function copy(text) {
    try {
      await navigator.clipboard.writeText(text);
      ctx.toast('Link copied. Open it on the phone.');
    } catch {
      ctx.toast('Couldn’t copy – select the link and copy it', 'error');
    }
  }

  function devices(list) {
    return h('ul', { class: 'paired-browsers' }, ...(list.length ? list.map((d) => h('li', null,
      h('span', null, d.name, h('span', { class: 'muted small' }, ` · ${pairedLine(d)}`)),
      h('button', {
        class: 'btn ghost', type: 'button', 'aria-label': `Disconnect ${d.name}`,
        onclick: async () => {
          try {
            await ctx.api(`${BASE}/devices/${encodeURIComponent(d.id)}`, { method: 'DELETE' });
            ctx.toast(`${d.name} can’t reach Todo Tracker any more`);
            load();
          } catch (err) {
            ctx.toast(err.message, 'error');
          }
        },
      }, 'Disconnect'))) : [h('li', { class: 'muted small' }, 'None yet.')]));
  }

  /** A code to scan: the QR code and the link, until it's used (then the phone's name) or it expires. */
  function pairing(status) {
    const out = h('div', { class: 'phone-pairing' });
    const said = h('p', { class: 'muted small', role: 'status', 'aria-live': 'polite' });
    const show = async () => {
      stop();
      const mine = generation;
      let code;
      try {
        code = await ctx.api(`${BASE}/pair`, { method: 'POST', body: {} });
      } catch (err) {
        ctx.toast(err.message, 'error');
        return;
      }
      if (mine !== generation) return;
      const left = h('span', { class: 'muted small' });
      const tick = () => {
        const seconds = Math.max(0, Math.round((new Date(code.expiresAt) - Date.now()) / 1000));
        left.textContent = seconds ? `Works once, for ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` : '';
        if (!seconds && mine === generation) {
          stop();
          said.textContent = 'That code expired – get a new one.';
        }
      };
      const poll = async () => {
        let answer;
        try {
          answer = await ctx.api(`${BASE}/pair?id=${encodeURIComponent(code.id)}`);
        } catch {
          return; // The next poll tries again.
        }
        if (mine !== generation || answer.state === 'waiting') return;
        stop();
        if (answer.state === 'paired') {
          out.replaceChildren(h('p', { class: 'paired' }, icon('check', { size: 16 }), ` Connected ${answer.phone}.`), said,
            h('button', { class: 'btn ghost', type: 'button', onclick: show }, 'Connect another phone'));
          said.textContent = `Connected ${answer.phone}.`;
          load();
        } else {
          said.textContent = 'That code isn’t valid any more – get a new one.';
        }
      };
      tick();
      handles = [setInterval(tick, 1000), setInterval(poll, 2000)];
      said.textContent = '';
      // The QR code is an SVG the server makes; shown as an image, so nothing in it runs.
      const qr = h('img', { class: 'phone-qr', alt: 'QR code to connect a phone', src: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(code.qrSvg)}` });
      // Native replaceChildren/append would show a null as the text "null": leave it out.
      out.replaceChildren(...[
        h('p', null, 'In Todo Tracker on your phone, choose “Connect to a computer” and scan this code (or open the link on the phone).'),
        qr,
        h('div', { class: 'row' },
          h('input', { class: 'phone-link', type: 'text', readonly: true, value: code.link, 'aria-label': 'Link to connect a phone', onfocus: (e) => e.target.select() }),
          h('button', { class: 'btn', type: 'button', onclick: () => copy(code.link) }, icon('note', { size: 16 }), 'Copy link')),
        code.addresses.length ? null : h('p', { class: 'muted small' }, 'This computer isn’t on a network a phone can reach. Connect it to Wi-Fi (or Tailscale), then get a new code.'),
        left, said,
        h('button', { class: 'btn ghost', type: 'button', onclick: show }, 'New code')].filter(Boolean));
    };
    const can = status.letPhonesConnect && status.listening;
    out.append(
      h('button', { class: 'btn primary', type: 'button', disabled: !can, onclick: show }, icon('plus', { size: 16 }), 'Connect a phone'),
      ...[
        // Waiting for a restart is already said in the status line above.
        can || status.letPhonesConnect ? null : h('p', { class: 'muted small' }, 'Turn on “Let phones connect” first.'),
        said].filter(Boolean));
    return out;
  }

  async function load() {
    stop();
    let status;
    let list;
    try {
      [status, list] = await Promise.all([ctx.api(BASE), ctx.api(`${BASE}/devices`)]);
    } catch (err) {
      // A browser signed in with the token (not the app's own window) can't see this: say where it is.
      element.replaceChildren(h('p', { class: 'muted' }, err.status === 403 ? err.message : `Couldn’t load phones: ${err.message}`));
      return;
    }
    const line = h('p', { class: 'muted small', role: 'status' }, phoneStatusLine(status));
    const toggle = h('input', { type: 'checkbox', checked: status.letPhonesConnect, 'aria-label': 'Let phones connect', onchange: async () => {
      try {
        await ctx.api(`${BASE}/access`, { method: 'PUT', body: { on: toggle.checked } });
        load();
      } catch (err) {
        toggle.checked = !toggle.checked;
        ctx.toast(err.message, 'error');
      }
    } });
    element.replaceChildren(
      h('label', { class: 'plugin-row' }, toggle, h('span', null, h('strong', null, 'Let phones connect'),
        h('span', { class: 'muted small' }, `Opens port ${status.port} on this computer, with its own certificate, for phones you connect here. Nothing else can use it.`))),
      line,
      pairing(status),
      h('h3', null, 'Connected phones'),
      h('p', { class: 'muted small' }, 'Each phone has its own access, only to your tasks (not settings, hooks or AI apps). Disconnect one you no longer use.'),
      devices(list));
  }

  return { element, load, stop };
}
