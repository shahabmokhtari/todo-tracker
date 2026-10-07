// Applies the chosen theme (light, dark, or the system's) before the page is drawn, so it never flashes the other one.
// The app keeps it in step with the setting (Settings › Appearance), which every window shares.
(function applySavedTheme() {
  try {
    const theme = localStorage.getItem('tt.theme');
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
  } catch {
    // Storage off: the system's theme.
  }
}());
