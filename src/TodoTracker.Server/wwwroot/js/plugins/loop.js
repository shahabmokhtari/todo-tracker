// Copy for Loop: Microsoft Loop has no API to write to, so tasks are copied to paste there: a group as a checklist
// (markdown, which Loop, Teams, OneNote and GitHub turn into a checklist), or one task with everything in it (details,
// subtasks, notes, files and pictures: rich text with the pictures inside, plus the markdown for plain-text editors).

import { dayKey } from '../timefmt.js';
import { formatTags } from '../format.js';
import { splitEmbeds, embedUrl } from '../media.js';

/** The tasks as a nested markdown checklist: finished ones ticked (or left out), due dates in brackets. */
export function loopMarkdown(nodes, { title = null, includeDone = false } = {}) {
  const lines = title ? [`## ${title}`, ''] : [];
  const walk = (list, depth) => {
    for (const node of list ?? []) {
      if (node.done && !includeDone) continue;
      const due = node.deadline && !node.done ? ` (due ${dayKey(new Date(node.deadline))})` : '';
      const text = String(node.title).replace(/\s+/g, ' ').trim();
      lines.push(`${'  '.repeat(depth)}- [${node.done ? 'x' : ' '}] ${text}${due}`);
      walk(node.children, depth + 1);
    }
  };
  walk(nodes, 0);
  return `${lines.join('\n')}\n`;
}

const isDone = (node) => node.done ?? !!node.completedAt;
const oneLine = (text) => String(text ?? '').replace(/\s+/g, ' ').trim();
const day = (at) => dayKey(new Date(at));
const IMAGE = /\.(png|jpe?g|gif|webp|bmp|avif|svg)$/i;

/** Due date, labels and tags of a task, in one line ('' when it has none). */
function metaLine(item) {
  const parts = [];
  if (item.deadline && !isDone(item)) parts.push(`Due ${day(item.deadline)}`);
  parts.push(...(item.labels ?? []).map((l) => l.name));
  if (item.tags?.length) parts.push(formatTags(item.tags));
  return parts.join(' · ');
}

/** Text with its pictures and files named, for plain-text editors: "(picture: x.png)". */
const plainEmbeds = (text) => splitEmbeds(text).map((p) => (p.embed ? `(${p.image ? 'picture' : 'file'}: ${p.embed})` : p.text)).join('');

/** Files of a task that its text doesn't show already. */
function looseFiles(item) {
  const shown = new Set([item.details, ...(item.notes ?? []).map((n) => n.text)].flatMap((t) => splitEmbeds(t).filter((p) => p.embed).map((p) => p.embed.toLowerCase())));
  return (item.attachments ?? []).filter((a) => !shown.has(String(a.storedName ?? a.fileName).toLowerCase()));
}

/** One task, with everything in it, as markdown: details, subtasks (and theirs), notes and files. */
export function taskMarkdown(item, { includeDone = true } = {}) {
  const lines = [`## ${oneLine(item.title)}`];
  const meta = metaLine(item);
  if (meta) lines.push(meta);
  if (item.details?.trim()) lines.push('', plainEmbeds(item.details.trim()));
  const walk = (list, depth) => {
    for (const node of list ?? []) {
      if (isDone(node) && !includeDone) continue;
      const pad = '  '.repeat(depth);
      const due = node.deadline && !isDone(node) ? ` (due ${day(node.deadline)})` : '';
      lines.push(`${pad}- [${isDone(node) ? 'x' : ' '}] ${oneLine(node.title)}${due}`);
      if (node.details?.trim()) lines.push(...plainEmbeds(node.details.trim()).split('\n').map((l) => `${pad}  ${l}`));
      for (const n of node.notes ?? []) lines.push(`${pad}  - Note ${day(n.at)}: ${oneLine(plainEmbeds(n.text))}`);
      walk(node.children, depth + 1);
    }
  };
  if (item.children?.length) {
    lines.push('');
    walk(item.children, 0);
  }
  if (item.notes?.length) lines.push('', '**Notes**', ...item.notes.map((n) => `- ${day(n.at)}: ${oneLine(plainEmbeds(n.text))}`));
  const files = looseFiles(item);
  if (files.length) lines.push('', '**Files**', ...files.map((a) => `- ${a.fileName}`));
  return `${lines.join('\n')}\n`;
}

/** The pictures a task shows (in its text, or attached), each once: what the rich copy fetches. */
export function taskPictures(item) {
  const seen = new Map();
  const add = (itemId, name) => {
    const url = embedUrl(itemId, name);
    if (!seen.has(url)) seen.set(url, { url, name });
  };
  const visit = (node) => {
    for (const text of [node.details, ...(node.notes ?? []).map((n) => n.text)]) {
      splitEmbeds(text).filter((p) => p.embed && p.image).forEach((p) => add(node.id, p.embed));
    }
    looseFiles(node).filter((a) => IMAGE.test(a.fileName)).forEach((a) => add(node.id, a.storedName ?? a.fileName));
    (node.children ?? []).forEach(visit);
  };
  visit(item);
  return [...seen.values()];
}

const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

/**
 * One task as rich text (HTML) with its pictures inside. image(url) gives a picture as a data: URL, or null (too big,
 * or it didn't load): then its name is shown. Checkboxes are ☐ / ☑, which every editor keeps.
 */
export function taskHtml(item, { image = () => null, includeDone = true } = {}) {
  const picture = (itemId, name) => {
    const src = image(embedUrl(itemId, name));
    return src ? `<img src="${esc(src)}" alt="${esc(name)}" style="max-width:480px">` : `(picture: ${esc(name)})`;
  };
  const rich = (itemId, text) => splitEmbeds(text).map((p) => (p.embed ? (p.image ? picture(itemId, p.embed) : `(file: ${esc(p.embed)})`) : esc(p.text).replace(/\n/g, '<br>'))).join('');
  const out = [`<h2>${esc(oneLine(item.title))}</h2>`];
  const meta = metaLine(item);
  if (meta) out.push(`<p><em>${esc(meta)}</em></p>`);
  if (item.details?.trim()) out.push(`<p>${rich(item.id, item.details.trim())}</p>`);
  const list = (nodes) => {
    const items = (nodes ?? []).filter((n) => includeDone || !isDone(n)).map((n) => {
      const due = n.deadline && !isDone(n) ? ` (due ${day(n.deadline)})` : '';
      const extra = [
        n.details?.trim() ? `<br>${rich(n.id, n.details.trim())}` : '',
        ...(n.notes ?? []).map((x) => `<br><small>Note ${day(x.at)}: ${rich(n.id, x.text)}</small>`),
      ].join('');
      return `<li>${isDone(n) ? '☑' : '☐'} ${esc(oneLine(n.title))}${due}${extra}${list(n.children)}</li>`;
    });
    return items.length ? `<ul>${items.join('')}</ul>` : '';
  };
  out.push(list(item.children));
  if (item.notes?.length) out.push('<p><strong>Notes</strong></p>', `<ul>${item.notes.map((n) => `<li>${day(n.at)}: ${rich(n.itemId ?? item.id, n.text)}</li>`).join('')}</ul>`);
  const files = looseFiles(item);
  const pictures = files.filter((a) => IMAGE.test(a.fileName));
  const others = files.filter((a) => !IMAGE.test(a.fileName));
  if (pictures.length) out.push(`<p>${pictures.map((a) => picture(item.id, a.storedName ?? a.fileName)).join(' ')}</p>`);
  if (others.length) out.push('<p><strong>Files</strong></p>', `<ul>${others.map((a) => `<li>${esc(a.fileName)}</li>`).join('')}</ul>`);
  return out.join('\n');
}

const MAX_PICTURE_BYTES = 4 * 1024 * 1024;

/** The task's pictures as data: URLs (each at most 4 MB; ones that fail are left out). */
async function loadPictures(item) {
  const found = new Map();
  await Promise.all(taskPictures(item).map(async (p) => {
    try {
      const res = await fetch(p.url, { credentials: 'same-origin' });
      if (!res.ok) return;
      const blob = await res.blob();
      if (blob.size > MAX_PICTURE_BYTES) return;
      found.set(p.url, await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(blob);
      }));
    } catch { /* named instead */ }
  }));
  return found;
}

/**
 * Copies one task: rich text with its pictures (Loop, Teams, OneNote, Word, mail) and markdown as plain text. The
 * clipboard gets promises, so the click still counts as the user's even though the pictures load first (Safari).
 */
export async function copyTask(host, id, { includeDone = true } = {}) {
  const item = await host.api(`/api/items/${id}`);
  const markdown = taskMarkdown(item, { includeDone });
  let pictures = 0;
  const html = loadPictures(item).then((found) => {
    pictures = found.size;
    return new Blob([taskHtml(item, { image: (url) => found.get(url) ?? null, includeDone })], { type: 'text/html' });
  });
  try {
    await navigator.clipboard.write([new ClipboardItem({ 'text/html': html, 'text/plain': new Blob([markdown], { type: 'text/plain' }) })]);
  } catch {
    await navigator.clipboard.writeText(markdown);
    pictures = 0;
  }
  return { item, markdown, pictures };
}

export function activate(host) {
  host.addTaskAction?.({
    iconName: 'list',
    label: 'Copy for Loop',
    title: 'Copy this task for Loop: details, subtasks, notes and pictures',
    onClick: async (id) => {
      try {
        const { item, pictures } = await copyTask(host, id);
        host.toast(`Copied “${item.title}”${pictures ? ` with ${pictures} picture${pictures === 1 ? '' : 's'}` : ''}. Paste it into a Loop page.`);
      } catch (err) {
        host.toast(err.message, 'error');
      }
    },
  });

  const { h } = host;
  host.addHeaderButton({ iconName: 'list', label: 'Copy for Loop', onClick: () => show() });

  async function show() {
    const groups = host.groups();
    let group = host.group();
    let includeDone = false;
    const output = h('textarea', { class: 'loop-output', readonly: true, rows: 14, 'aria-label': 'Checklist to paste' });
    const copied = h('span', { class: 'muted small', role: 'status' });

    async function fill() {
      try {
        const nodes = await host.api(`/api/tree${group ? `?group=${group}` : ''}`);
        const name = groups.find((g) => g.id === group)?.name ?? null;
        output.value = loopMarkdown(nodes, { title: name, includeDone });
        copied.textContent = '';
      } catch (err) {
        host.toast(err.message, 'error');
      }
    }

    async function copy() {
      try {
        await navigator.clipboard.writeText(output.value);
      } catch {
        output.select();
        document.execCommand('copy');
      }

      copied.textContent = 'Copied. Paste it into a Loop page.';
    }

    const panel = h('div', { class: 'loop' },
      h('p', { class: 'muted' }, 'Loop can’t be written to by other apps, so copy your tasks and paste them into a Loop page: they become a checklist there.'),
      h('p', { class: 'muted' }, 'One task with its details, notes and pictures: open it and use its Copy for Loop button.'),
      h('div', { class: 'row' },
        h('label', { class: 'field' }, h('span', null, 'Tasks from'),
          h('select', { 'aria-label': 'Group', onchange: (e) => { group = e.target.value || null; fill(); } },
            h('option', { value: '', selected: !group }, 'All groups'),
            ...groups.map((g) => h('option', { value: g.id, selected: g.id === group }, g.name)))),
        h('label', { class: 'check' }, h('input', { type: 'checkbox', onchange: (e) => { includeDone = e.target.checked; fill(); } }), 'Include finished')),
      output,
      h('div', { class: 'row buttons' }, h('button', { class: 'btn primary', type: 'button', onclick: copy }, 'Copy checklist'), copied));
    await host.openPanel('Copy for Loop', panel, { iconName: 'list' });
    await fill();
  }
}