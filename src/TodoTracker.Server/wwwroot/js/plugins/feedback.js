// Send feedback: a bug, an idea or anything else, to the people who make Onething (it becomes a GitHub issue). The
// sheet sends the note to this computer's Onething, which passes it to the issues relay: the browser never talks to
// the relay itself. tests/fixtures/feedback.json holds the same words and rules as the iPhone, Mac and Android apps.

export const MAX_MESSAGE = 2000;
/** The counter shows once the note is this close to the limit. */
export const COUNTER_FROM = 200;

export const KINDS = [['bug', 'Bug'], ['idea', 'Idea'], ['other', 'Other']];

export const TEXTS = {
  sent: 'Thanks — we read every note.',
  retry: 'Couldn’t send — check your connection and try again.',
  failed: 'Couldn’t send your note: something went wrong on our side. Please try again later.',
  finePrint: 'We’ll include your app version, device model and language.',
  contactHint: 'Only if you’d like a reply',
};

const SOURCES = ['button', 'shake', 'page', 'qr'];

/**
 * The note as it will be sent: control characters removed (new lines and tabs stay), trimmed, cut to `max` code
 * points, trimmed again. Empty: nothing to send.
 */
export function cleanMessage(text, max = MAX_MESSAGE) {
  // eslint-disable-next-line no-control-regex
  const kept = String(text ?? '').replace(/[\p{Cc}]/gu, (c) => (c === '\n' || c === '\t' ? c : '')).trim();
  return [...kept].slice(0, max).join('').trim();
}

/** How many characters (code points: an emoji is one) the note has as it will be sent. */
export const messageLength = (text) => [...cleanMessage(text, Infinity)].length;

/** "180 left" once the note is near the limit, else empty. */
export function counterText(text) {
  const left = MAX_MESSAGE - messageLength(text);
  if (left > COUNTER_FROM) return '';
  return left >= 0 ? `${left} left` : `${-left} too many`;
}

/** What an answer means: 2xx sent; no answer (offline), 408, 429 or 5xx try again; anything else failed. */
export function classify(status) {
  if (status == null || status === 408 || status === 429 || (status >= 500 && status <= 599)) return 'retry';
  if (status >= 200 && status <= 299) return 'sent';
  return 'failed';
}

/** How the sheet was opened, from a link (?feedback=shake …); a plain link is the button. */
export const sourceOf = (value) => (SOURCES.includes(value) ? value : 'button');

const newClientId = () => crypto.randomUUID();

/** The feedback sheet, in the side panel. */
export async function openFeedback(host, source = 'button') {
  const { h } = host;
  // One id for this sheet: sending again after "try again" can't file the note twice.
  const clientID = newClientId();
  let kind = 'bug';
  let sending = false;

  const chips = h('div', { class: 'seg feedback-kinds', role: 'group', 'aria-label': 'What is it?' },
    ...KINDS.map(([id, label]) => h('button', { type: 'button', dataset: { kind: id }, 'aria-pressed': String(id === kind), onclick: () => {
      kind = id;
      chips.querySelectorAll('button').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.kind === id)));
    } }, label)));
  const message = h('textarea', { id: 'feedback-message', rows: 7, maxlength: MAX_MESSAGE * 2, placeholder: 'What happened, or what would help?', 'aria-describedby': 'feedback-counter' });
  const counter = h('span', { id: 'feedback-counter', class: 'muted small feedback-counter', 'aria-live': 'polite' });
  const contact = h('input', { id: 'feedback-contact', type: 'email', autocomplete: 'email', placeholder: TEXTS.contactHint, 'aria-describedby': 'feedback-contact-hint' });
  const status = h('p', { class: 'feedback-status small', role: 'status', 'aria-live': 'polite' });
  const send = h('button', { class: 'btn primary', type: 'submit', disabled: true }, host.icon('send', { size: 16 }), 'Send');

  const update = () => {
    counter.textContent = counterText(message.value);
    send.disabled = sending || !cleanMessage(message.value) || messageLength(message.value) > MAX_MESSAGE;
  };
  message.addEventListener('input', update);

  const form = h('form', { class: 'feedback', novalidate: true },
    h('p', { class: 'muted' }, 'Found a bug, or have an idea? Tell us: every note is read.'),
    chips,
    h('label', { class: 'field' }, h('span', null, 'Your note'), message),
    counter,
    h('label', { class: 'field' }, h('span', null, 'Email (optional)'), contact),
    h('p', { class: 'muted small', id: 'feedback-contact-hint' }, `${TEXTS.contactHint}.`),
    h('p', { class: 'muted small feedback-fine-print' }, TEXTS.finePrint),
    status,
    h('div', { class: 'row' }, send));

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (send.disabled) return;
    sending = true;
    update();
    status.textContent = 'Sending…';
    status.classList.remove('error');
    let outcome;
    try {
      await host.post('/api/plugins/feedback', {
        kind, message: message.value, contact: contact.value, clientID, source,
        language: navigator.language || null, screen: `${window.screen.width}x${window.screen.height}`,
      });
      outcome = 'sent';
    } catch (err) {
      // No status: Onething on this computer didn't answer (it's restarting or closed).
      outcome = classify(err?.status ?? null);
    }
    sending = false;
    if (outcome === 'sent') {
      host.toast(TEXTS.sent);
      host.closePanel();
      return;
    }
    status.textContent = TEXTS[outcome];
    status.classList.add('error');
    update();
  });

  const opened = await host.openPanel('Send feedback', form, { iconName: 'chat' });
  if (opened !== false) message.focus();
}

export function activate(host) {
  // A link from the sidebar, the tray or the browser extension: /?feedback=button opens the sheet.
  const params = new URLSearchParams(location.search);
  if (params.has('feedback')) {
    const source = sourceOf(params.get('feedback'));
    // Opened once: a reload doesn't open it again.
    params.delete('feedback');
    const rest = params.toString();
    history.replaceState(null, '', `${location.pathname}${rest ? `?${rest}` : ''}${location.hash}`);
    openFeedback(host, source);
  }
}
