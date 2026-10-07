// Copy for Loop: Microsoft Loop has no API to write to, so a group's tasks are copied as a checklist (markdown), which
// Loop turns into a checklist when it's pasted (so do Teams, OneNote, GitHub and most editors).

import { dayKey } from '../timefmt.js';

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

export function activate(host) {
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
