// Light, dark, or the system's. The choice is kept by the app (every window shares it) and remembered in the browser,
// so js/theme.js can apply it before the page is drawn.

export const THEMES = [['system', 'System', 'settings'], ['light', 'Light', 'sun'], ['dark', 'Dark', 'moon']];

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
