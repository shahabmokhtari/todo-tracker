// Shared by the views: loads where only the newest answer counts, changes made one at a time (each built from the
// data as it is when its turn comes), and the request runner with its toast and refresh.

/** Wraps a loader so only the newest call's result is used: an older reply that arrives late is dropped. */
export function latest(load) {
  let seq = 0;
  return async (...args) => {
    const mine = ++seq;
    const value = await load(...args);
    return mine === seq ? { current: true, value } : { current: false };
  };
}

/** Runs jobs one after another (a failed job doesn't stop the next). */
export function serial() {
  let tail = Promise.resolve();
  return (job) => {
    const next = tail.then(job, job);
    tail = next.catch(() => {});
    return next;
  };
}

/** Same data as last time (a background refresh then leaves the screen, its scroll and focus alone). */
export function sameAs(previous, next) {
  const text = JSON.stringify(next);
  return { same: previous === text, text };
}

/**
 * Sends the requests in order, through ctx.act (toast, then everything on screen refreshes). `focusId` is the task
 * that keeps the keyboard focus after the redraw. Returns true when it all went through.
 */
export async function send(ctx, requests, message, focusId = null) {
  if (!requests?.length) return false;
  ctx.state.refocus = focusId;
  try {
    return await ctx.act((async () => { for (const r of requests) await ctx.api(r.url, { method: r.method, body: r.body }); })(), message);
  } finally {
    if (ctx.state.refocus === focusId) ctx.state.refocus = null;
  }
}

/** The tree of the group on screen (all groups when none is picked). */
export const treeUrl = (ctx, extra = '') => {
  const params = new URLSearchParams(extra);
  if (ctx.state.group) params.set('group', ctx.state.group);
  const query = params.toString();
  return `/api/tree${query ? `?${query}` : ''}`;
};

/** Redraws, keeping keyboard focus on the same toolbar control (the toolbar is rebuilt with the rest). */
export function keepToolbarFocus(root, draw) {
  const controls = () => [...root.querySelectorAll('.view-toolbar button, .view-toolbar input, .view-toolbar select')];
  const at = controls().indexOf(document.activeElement);
  draw();
  if (at >= 0) controls()[at]?.focus({ preventScroll: true });
}
