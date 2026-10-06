// Edge and Chrome open the side panel from the toolbar button; Safari (no side panel) shows the same page as a popup.
const ext = globalThis.browser ?? globalThis.chrome;
ext.runtime.onInstalled.addListener(() => {
  ext.sidePanel?.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});
});
