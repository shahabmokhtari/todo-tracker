// Media in details and notes: pasted images become the task's attachments and are embedded the way Obsidian does
// (![[Pasted image 20261006132517.png]]), so the markdown files show them in Obsidian too. The app shows embeds by
// name through /api/items/{id}/embed/{name}.

const EMBED = /!\[\[([^\]|]+)(?:\|[^\]]*)?\]\]|!\[[^\]]*\]\(([^)\s]+)\)/g;
const IMAGE = /\.(png|jpe?g|gif|webp|bmp|avif)$/i;

/** The attachment an embed points to (its file name), or null when it isn't one (a web address, another note). */
function targetOf(wiki, path) {
  const raw = (wiki ?? path ?? '').trim();
  // Remote images and other notes' sections are not this task's attachments.
  if (!raw || /^[a-z][a-z0-9+.-]*:/i.test(raw) || raw.startsWith('//')) return null;
  const last = raw.split('#')[0].split('/').pop();
  if (!last) return null;
  if (wiki) return last;
  try {
    return decodeURIComponent(last);
  } catch {
    return last;
  }
}

/**
 * Text as plain parts and embeds, in order: [{ text } | { embed: name, image }]. Embeds that aren't attachments of
 * the task (web images, ![[Another note]]) stay text.
 */
export function splitEmbeds(text) {
  const value = String(text ?? '');
  const parts = [];
  let at = 0;
  const push = (t) => {
    if (!t) return;
    if (parts.length && parts[parts.length - 1].text !== undefined) parts[parts.length - 1].text += t;
    else parts.push({ text: t });
  };
  for (const match of value.matchAll(EMBED)) {
    const name = targetOf(match[1], match[2]);
    // ![[Another note]] (no extension) is Obsidian embedding a note: not a file of this task.
    if (!name || (match[1] && !/\.[a-z0-9]{1,8}$/i.test(name))) continue;
    push(value.slice(at, match.index));
    parts.push({ embed: name, image: IMAGE.test(name) });
    at = match.index + match[0].length;
  }
  push(value.slice(at));
  return parts.length ? parts : [{ text: '' }];
}

export function embedNames(text) {
  return splitEmbeds(text).filter((p) => p.embed).map((p) => p.embed);
}

const pad = (n) => String(n).padStart(2, '0');
const stamp = (d) => `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
const extensions = { 'image/png': 'png', 'image/jpeg': 'jpg', 'image/gif': 'gif', 'image/webp': 'webp', 'image/bmp': 'bmp', 'image/avif': 'avif' };

/** Screenshots arrive as "image.png" (or nameless): they get Obsidian's "Pasted image <time>" name; real files keep theirs. */
export function pastedName(file, now = new Date()) {
  const generic = !file.name || /^image\.(png|jpe?g|gif|webp|bmp|avif)$/i.test(file.name);
  if (!generic) return file.name;
  const ext = extensions[file.type];
  return ext ? `Pasted image ${stamp(now)}.${ext}` : `Pasted file ${stamp(now)}.bin`;
}

/** The files in a paste, unless it also holds text (copying from an office app adds a picture of the text). */
export function imageFiles(clipboard) {
  const items = [...(clipboard?.items ?? [])];
  if (items.some((i) => i.kind === 'string' && i.type === 'text/plain')) return [];
  return items.filter((i) => i.kind === 'file').map((i) => i.getAsFile()).filter(Boolean);
}

/** The URL showing an embed of the task's (or its parents') attachment. */
export const embedUrl = (itemId, name) => `/api/items/${encodeURIComponent(itemId)}/embed/${encodeURIComponent(name)}`;

/** The same limit the app has for attachments. */
export const MAX_PASTE_BYTES = 25 * 1024 * 1024;

const pending = new Set();

/** Resolves when every paste still uploading is done (finishing a note waits, so its picture is in it). */
export function pastesDone() {
  return Promise.allSettled([...pending]);
}

/**
 * Pasting a file (a screenshot) into a text box attaches it to the task and puts its embed where the caret is.
 * `upload(file, name)` stores it and resolves to the attachment (its stored file name may differ); `onUploaded`
 * runs after each one; `onError` reports a file that couldn't be stored (the others still go in).
 */
export function acceptPastedMedia(box, { upload, onError, onUploaded }) {
  box.addEventListener('paste', (e) => {
    const files = imageFiles(e.clipboardData);
    if (!files.length) return;
    e.preventDefault();
    let caret = box.selectionEnd ?? box.value.length;
    const work = (async () => {
      for (const file of files) {
        if (file.size > MAX_PASTE_BYTES) {
          onError?.(new Error(`${file.name || 'That file'} is too big to attach (at most 25 MB).`));
          continue;
        }

        try {
          const attachment = await upload(file, pastedName(file));
          const embed = `![[${attachment.storedName ?? attachment.fileName}]]`;
          // Where the caret is now if the box is still being typed in; else where the paste was. Never in place of
          // text selected meanwhile (the upload took a moment): the embed goes after it and the selection stays.
          const focused = document.activeElement === box;
          const at = focused ? box.selectionEnd : Math.min(caret, box.value.length);
          const selecting = focused && box.selectionStart !== box.selectionEnd;
          const before = at > 0 && !/\s$/.test(box.value.slice(0, at)) ? ' ' : '';
          box.setRangeText(before + embed, at, at, selecting ? 'preserve' : 'end');
          caret = at + before.length + embed.length;
          box.dispatchEvent(new Event('input', { bubbles: true }));
          onUploaded?.(attachment);
        } catch (err) {
          onError?.(err);
        }
      }
    })();
    pending.add(work);
    work.finally(() => pending.delete(work));
  });
}
