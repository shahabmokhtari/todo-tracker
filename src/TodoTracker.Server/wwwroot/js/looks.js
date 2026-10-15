// A task's look: an icon (an emoji) shown before its title, and a color (its own, else its first label's) as a tint
// and a stripe. Pickers for both live in the task's details.
import { h } from './api.js';
import { icon } from './icons.js';
import { cleanIcon, hexColor } from './format.js';

/** Emojis to pick from (anything else can be typed or pasted). */
export const ICONS = [
  '🚀', '🎯', '⭐', '🔥', '💡', '✅', '📌', '📝', '📅', '⏰', '📞', '✉️',
  '💬', '👥', '🧑‍💻', '🐛', '🔧', '🛠️', '📦', '🔒', '📊', '📈', '💰', '🧾',
  '🏠', '🛒', '🍽️', '🏃', '🧘', '💊', '🩺', '🎓', '📚', '✈️', '🚗', '🎉',
  '🎁', '❤️', '🌱', '🐶', '🎵', '🎮', '🧹', '🧺', '☕', '💤', '⚠️', '❓',
];

/** Colors to pick from (the label palette and a few more). */
export const COLORS = ['#ef4444', '#f97316', '#f59e0b', '#eab308', '#10b981', '#14b8a6', '#0ea5e9', '#3b82f6', '#7c3aed', '#ec4899', '#64748b', '#78350f'];

/** The icon in front of a title (nothing when there's none). */
export function iconBadge(value) {
  return value ? h('span', { class: 'task-icon', 'aria-hidden': 'true' }, value) : null;
}

/** A small 🔁 after a title when the task repeats (its rule on hover). */
export function repeatMark(rule) {
  return rule ? h('span', { class: 'repeat-mark', title: `Repeats ${rule}`, 'aria-label': `repeats ${rule}`, role: 'img' }, '🔁') : null;
}

/** Rules offered while typing one (any other one can be typed). */
export const REPEAT_CHOICES = [
  'every day', 'every weekday', 'every week', 'every 2 weeks', 'every month', 'every year',
  'every week on Monday', 'every month on the 1st', 'every month on the last day', 'every month on the last Friday',
  'every 3 days when done',
];

/**
 * The repeat box of a task's details: a rule in words ("every weekday", "every 2 weeks on Monday, Thursday"), saved
 * when you leave the box or press Enter; empty stops it repeating. `onSave` gets the text and returns a promise that
 * rejects when the rule isn't understood (the box shows why and keeps what was typed).
 */
export function repeatField(current, onSave) {
  const list = h('datalist', { id: `repeat-choices-${Math.random().toString(36).slice(2)}` }, ...REPEAT_CHOICES.map((r) => h('option', { value: r })));
  const input = h('input', { value: current ?? '', placeholder: 'Doesn’t repeat (e.g. every weekday)', maxlength: 120, 'aria-label': 'Repeat', autocomplete: 'off' });
  input.setAttribute('list', list.id);
  let saved = current ?? '';
  const save = async () => {
    const text = input.value.trim();
    if (text === saved) return;
    input.setCustomValidity('');
    try {
      await onSave(text);
      saved = text;
    } catch (e) {
      input.setCustomValidity(e?.message ?? 'Not understood');
      input.reportValidity();
    }
  };
  input.addEventListener('input', () => input.setCustomValidity(''));
  input.addEventListener('change', save);
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); save(); } });
  return h('span', { class: 'repeat-field' }, input, list);
}

/** Tints an element with a task's color (its own or its first label's); set from script (the CSP blocks style attributes). */
export function withAccent(el, accent) {
  if (accent) {
    el.classList.add('has-accent');
    el.style.setProperty('--task-accent', accent);
  }
  return el;
}

/** A dialog to pick an icon: the grid, a box for any emoji, and "No icon". Resolves to the icon, '' for none, or null. */
export function pickIcon(current) {
  return new Promise((resolve) => {
    const dialog = h('dialog', { class: 'look-dialog', 'aria-label': 'Choose an icon' });
    let result = null;
    const done = (value) => { result = value; dialog.close(); };
    const typed = h('input', { type: 'text', maxlength: 16, value: current ?? '', placeholder: 'Or type / paste any emoji', 'aria-label': 'Icon' });
    const grid = h('div', { class: 'icon-grid', role: 'group', 'aria-label': 'Icons' },
      ...ICONS.map((value) => h('button', {
        class: `icon-choice${value === current ? ' chosen' : ''}`, type: 'button', 'aria-label': value, 'aria-pressed': String(value === current),
        onclick: () => done(value),
      }, value)));
    const useTyped = () => {
      const value = cleanIcon(typed.value);
      if (value === null) { typed.setCustomValidity('At most 16 characters'); typed.reportValidity(); return; }
      done(value);
    };
    typed.addEventListener('input', () => typed.setCustomValidity(''));
    typed.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); useTyped(); } });
    dialog.append(
      h('h2', null, 'Icon'),
      grid,
      h('div', { class: 'row' }, typed, h('button', { class: 'btn', type: 'button', onclick: useTyped }, 'Use')),
      h('div', { class: 'row buttons' },
        current ? h('button', { class: 'btn ghost', type: 'button', onclick: () => done('') }, icon('x', { size: 14 }), 'No icon') : null,
        h('button', { class: 'btn ghost', type: 'button', onclick: () => dialog.close() }, 'Cancel')));
    // Keys stay in the dialog: Esc closes it, not the task behind it.
    dialog.addEventListener('keydown', (e) => e.stopPropagation());
    dialog.addEventListener('close', () => { dialog.remove(); resolve(result); });
    document.body.append(dialog);
    dialog.showModal();
    (grid.querySelector('.chosen') ?? grid.firstElementChild)?.focus();
  });
}

/**
 * The color row of a task's details: "Auto" (its first label's color), the palette, and any color. `onPick` gets
 * '#rrggbb', or '' for Auto.
 */
export function colorPicker(current, labelColor, onPick) {
  const swatch = (value, title) => {
    const on = (current ?? '') === value;
    const b = h('button', { class: `swatch${on ? ' chosen' : ''}${value ? '' : ' auto'}`, type: 'button', title, 'aria-label': title, 'aria-pressed': String(on), onclick: () => onPick(value) },
      value ? null : icon('x', { size: 12 }));
    const fill = value || labelColor;
    if (fill) b.style.setProperty('--swatch', fill);
    return b;
  };
  const custom = h('input', { type: 'color', value: current ?? '#3b82f6', 'aria-label': 'Any color', title: 'Any color' });
  custom.addEventListener('change', () => { const value = hexColor(custom.value); if (value) onPick(value); });
  return h('div', { class: 'swatches', role: 'group', 'aria-label': 'Color' },
    swatch('', labelColor ? 'Auto (its label’s color)' : 'No color'),
    ...COLORS.map((c) => swatch(c, c)),
    custom);
}
