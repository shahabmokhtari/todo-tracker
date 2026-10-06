// Media in details and notes: pasted images become the task's attachments and are embedded the way Obsidian does
// (![[Pasted image 20261006132517.png]]), so the markdown files show them in Obsidian too. The app shows embeds by
// name through /api/items/{id}/embed/{name}.

const EMBED = /!\[\[([^\]|]+)(?:\|[^\]]*)?\]\]|!\[[^\]]*\]\(([^)\s]+)\)/g;

/** The file name an embed points to (the last part of a path, decoded). */
function nameOf(wiki, path) {
  if (wiki) return wiki.trim();
  const last = path.split('/').pop();
  try {
    return decodeURIComponent(last);
  } catch {
    return last;
  }
}

/** Text as plain parts and embeds, in order: [{ text } | { embed: name }]. */
export function splitEmbeds(text) {
  const parts = [];
  let at = 0;
  for (const match of String(text ?? '').matchAll(EMBED)) {
    const name = nameOf(match[1], match[2] ?? '');
    if (!name) continue;
    if (match.index > at) parts.push({ text: text.slice(at, match.index) });
    parts.push({ embed: name });
    at = match.index + match[0].length;
  }
  if (at < String(text ?? '').length || parts.length === 0) parts.push({ text: String(text ?? '').slice(at) });
  return parts;
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
  return ext ? `Pasted image ${stamp(now)}.${ext}` : `Pasted file ${stamp(now)}`;
}

/** The files in a paste, unless it also holds text (copying from an office app adds a picture of the text). */
export function imageFiles(clipboard) {
  const items = [...(clipboard?.items ?? [])];
  if (items.some((i) => i.kind === 'string' && i.type === 'text/plain')) return [];
  return items.filter((i) => i.kind === 'file').map((i) => i.getAsFile()).filter(Boolean);
}

/** The URL showing an embed of the task's (or its parents') attachment. */
export const embedUrl = (itemId, name) => `/api/items/${encodeURIComponent(itemId)}/embed/${encodeURIComponent(name)}`;

/**
 * Pasting a file (a screenshot) into a text box attaches it to the task and puts its embed where the caret is.
 * `upload(file, name)` stores it and resolves to the attachment (its final file name may differ).
 */
export function acceptPastedMedia(box, { upload, onError }) {
  box.addEventListener('paste', async (e) => {
    const files = imageFiles(e.clipboardData);
    if (!files.length) return;
    e.preventDefault();
    const start = box.selectionStart ?? box.value.length;
    const end = box.selectionEnd ?? start;
    try {
      const embeds = [];
      for (const file of files) {
        const attachment = await upload(file, pastedName(file));
        embeds.push(`![[${attachment.storedName ?? attachment.fileName}]]`);
      }
      const text = embeds.join(' ');
      box.focus();
      box.setRangeText(text, start, end, 'end');
      box.dispatchEvent(new Event('input', { bubbles: true }));
    } catch (err) {
      onError?.(err);
    }
  });
}
