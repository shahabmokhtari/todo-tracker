// "Later" beyond the quick choices: a time in words (or a date picker), or "after another task is done".
import { whenText, rulePreview } from './format.js';

const EXAMPLES = ['tonight', 'tomorrow 14:00', 'fri', 'weekend', 'next week', '2 weeks', 'next month'];

export function createLater(ctx) {
  const { h, icon, api, post, act } = ctx;

  /** A modal sheet (styled like the command palette); returns a close function. */
  function sheet(label, build) {
    const lastFocus = document.activeElement;
    const overlay = h('div', { class: 'palette later-sheet', role: 'dialog', 'aria-modal': 'true', 'aria-label': label });
    const close = () => {
      overlay.remove();
      lastFocus?.focus?.();
    };
    overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) close(); });
    overlay.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); return; }
      if (e.key === 'Tab') {
        const items = [...overlay.querySelectorAll('input, button, [tabindex="0"]')].filter((el) => !el.disabled && el.offsetParent !== null);
        const i = items.indexOf(document.activeElement);
        e.preventDefault();
        items[(i + (e.shiftKey ? -1 : 1) + items.length) % items.length]?.focus();
      }
    });
    overlay.addEventListener('click', (e) => e.stopPropagation());
    overlay.append(h('div', { class: 'palette-box later-box' }, ...build(close)));
    document.body.append(overlay);
    overlay.querySelector('input')?.focus();
    return close;
  }

  /** "Pick a time…": words with a live preview ("next week" → Mon 12 Jan, 9:00), or a date and time. */
  function openWhen(card) {
    let answer = null;
    let timer = null;
    let asked = '';
    sheet(`Snooze ${card.title} until`, (close) => {
      const preview = h('p', { class: 'later-preview muted', 'aria-live': 'polite' });
      const show = () => {
        const p = rulePreview(answer);
        preview.textContent = p.text || 'Type when, or pick a date below.';
        preview.classList.toggle('ok', p.ok);
        preview.classList.toggle('bad', !p.ok && !!p.text);
      };
      const ask = async (rule) => {
        asked = rule;
        try {
          const found = await api(`/api/snooze?rule=${encodeURIComponent(rule)}`);
          if (asked === rule) { answer = found; show(); }
        } catch {
          // The preview is a nicety; Snooze still asks the server.
        }
      };
      const input = h('input', {
        type: 'text', placeholder: 'next week, fri 14:00, 3d, 2026-03-01…', 'aria-label': 'When', autocomplete: 'off', maxlength: 60,
        oninput: () => {
          picker.value = '';
          answer = null;
          show();
          clearTimeout(timer);
          const rule = input.value.trim();
          asked = rule;
          if (rule) timer = setTimeout(() => ask(rule), 150);
        },
        onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } },
      });
      const picker = h('input', {
        type: 'datetime-local', 'aria-label': 'Date and time',
        oninput: () => {
          input.value = '';
          answer = picker.value ? { ruleAt: new Date(picker.value).toISOString(), ruleIn: ctx.relativeTime(new Date(picker.value)) } : null;
          show();
        },
      });
      const submit = async () => {
        const rule = input.value.trim();
        let body;
        if (rule) body = { rule };
        else if (picker.value) body = { at: new Date(picker.value).toISOString() };
        else { input.focus(); return; }
        clearTimeout(timer);
        if (rule && asked !== rule) await ask(rule);
        if (rule && answer?.ruleProblem) { show(); input.focus(); return; }
        const until = answer?.ruleAt ? whenText(answer.ruleAt) : '';
        if (await act(post(`/api/items/${card.id}/schedule`, { notify: true, ...body }), until ? `Snoozed until ${until}` : 'Snoozed')) close();
      };
      show();
      return [
        h('div', { class: 'later-head' }, icon('clock', { size: 18 }), h('div', null, h('h2', null, 'Snooze until…'), h('p', { class: 'muted small' }, card.title))),
        h('div', { class: 'later-body' },
          input,
          preview,
          h('div', { class: 'later-examples', 'aria-label': 'Examples' }, ...EXAMPLES.map((x) => h('button', {
            type: 'button', class: 'chip-btn', onclick: () => { input.value = x; input.dispatchEvent(new Event('input')); input.focus(); },
          }, x))),
          h('label', { class: 'later-picker muted small' }, 'or pick', picker)),
        h('div', { class: 'later-foot' },
          h('button', { class: 'btn', type: 'button', onclick: close }, 'Cancel'),
          h('button', { class: 'btn primary', type: 'button', onclick: submit }, icon('clock', { size: 16 }), 'Snooze')),
      ];
    });
  }

  /** "After another task…": find the task it waits for; it comes back (with a reminder) when that one is done. */
  function openAfter(card) {
    let results = [];
    let active = 0;
    let timer = null;
    const dashboard = ctx.dashboard();
    const seen = new Set([card.id]);
    const nearby = [dashboard?.focus, ...(dashboard?.now ?? []), ...(dashboard?.waiting ?? [])]
      .filter((t) => t && !seen.has(t.id) && seen.add(t.id))
      .map((t) => ({ id: t.id, title: t.title, breadcrumb: t.breadcrumb ?? [] }));
    sheet(`${card.title} waits for`, (close) => {
      const list = h('ul', { class: 'palette-list', role: 'listbox', id: 'after-list', 'aria-label': 'Tasks' });
      const choose = async (t) => {
        if (t && (await act(post(`/api/items/${card.id}/after`, { afterId: t.id }), `Waiting for “${t.title}”`))) close();
      };
      const render = () => {
        active = Math.min(active, Math.max(0, results.length - 1));
        list.replaceChildren(...(results.length
          ? results.map((t, i) => h('li', {
            role: 'option', id: `after-${i}`, class: 'palette-item', 'aria-selected': String(i === active),
            onmousemove: () => { if (active !== i) { active = i; render(); } },
            onclick: () => choose(t),
          }, h('span', { class: 'palette-icon' }, icon('link', { size: 16 })), h('span', { class: 'palette-text' }, h('span', null, t.title), t.breadcrumb.length ? h('span', { class: 'muted small' }, t.breadcrumb.join(' › ')) : null)))
          : [h('li', { class: 'palette-empty muted' }, input.value.trim() ? 'No open task matches.' : 'Type to find a task.')]));
        input.setAttribute('aria-activedescendant', results.length ? `after-${active}` : '');
      };
      const search = async (q) => {
        try {
          const found = await api(`/api/search?q=${encodeURIComponent(q)}`);
          if (input.value.trim() !== q) return;
          results = found.filter((t) => !t.isDone && t.id !== card.id).slice(0, 8).map((t) => ({ id: t.id, title: t.title, breadcrumb: t.breadcrumb ?? [] }));
          render();
        } catch {
          // Keep what's shown.
        }
      };
      const input = h('input', {
        type: 'text', placeholder: 'Find the task it waits for…', 'aria-label': 'Find the task it waits for', role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'after-list', autocomplete: 'off',
        oninput: () => {
          active = 0;
          clearTimeout(timer);
          const q = input.value.trim();
          if (!q) { results = nearby; render(); return; }
          timer = setTimeout(() => search(q), 120);
        },
        onkeydown: (e) => {
          if (e.key === 'ArrowDown') { e.preventDefault(); active = Math.min(results.length - 1, active + 1); render(); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); active = Math.max(0, active - 1); render(); }
          else if (e.key === 'Enter') { e.preventDefault(); choose(results[active]); }
        },
      });
      results = nearby;
      render();
      return [
        h('div', { class: 'later-head' }, icon('link', { size: 18 }), h('div', null, h('h2', null, 'After another task'), h('p', { class: 'muted small' }, `“${card.title}” comes back when that one is done.`))),
        h('div', { class: 'palette-input' }, icon('search', { size: 18 }), input, h('kbd', null, 'Esc')),
        list,
      ];
    });
  }

  return { openWhen, openAfter };
}
