// Light, dark, or the system's. The choice is kept by the app (every window shares it) and remembered in the browser,
// so js/theme.js can apply it before the page is drawn.

export const THEMES = [['system', 'System', 'settings'], ['light', 'Light', 'sun'], ['dark', 'Dark', 'moon']];

let choices = 0;
let saving = 0;

/**
 * The user picked a theme here: applied now, then saved with `save()`. Reads of the shared theme started before the
 * save is done are ignored (they'd bring the old one back for a moment; see followTheme).
 */
export async function chooseTheme(theme, save = async () => {}) {
  choices++;
  saving++;
  applyTheme(theme);
  try {
    await save();
  } finally {
    saving--;
    choices++;
  }
}

/** Applies the shared theme read with `read()`, unless the user picks one here meanwhile (that answer would be stale). */
export async function followTheme(read) {
  const before = choices;
  const theme = await read();
  if (before === choices && saving === 0) applyTheme(theme);
}

export function applyTheme(theme) {
  const value = theme === 'light' || theme === 'dark' ? theme : 'system';
  if (value === 'system') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = value;
  try {
    if (value === 'system') localStorage.removeItem('tt.theme');
    else localStorage.setItem('tt.theme', value);
  } catch {
    // Storage off: the choice still applies to this page.
  }

  return value;
}
