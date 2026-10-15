/**
 * Asking about hooks an AI agent added or changed (they wait for approval): "Claude wants to run a hook", with all of
 * what it would do, and Allow (it runs from now on, without asking again) or Don't allow (it's removed). Only in the
 * owner's session, and only when no desktop app asks itself (the Windows app shows a notification instead).
 */

/** Everything a hook would do, one line each: a name alone could say anything. */
export function describeHook(hook) {
  const m = hook.match ?? {};
  const pairs = (o, sep) => Object.entries(o ?? {}).map(([k, v]) => `${k}${sep}${v}`).join(', ');
  const only = [['groups', m.groups], ['tags', m.tags], ['labels', m.labels], ['done by', m.actors]].filter(([, v]) => v?.length).map(([k, v]) => `${k} ${v.join(', ')}`).join('; ');
  return [
    hook.powershell && `PowerShell: ${hook.powershell}`,
    hook.bash && `bash: ${hook.bash}`,
    hook.url && `Web address: ${hook.url}`,
    hook.headers && `Headers: ${pairs(hook.headers, ': ')}`,
    hook.env && `Variables: ${pairs(hook.env, '=')}`,
    hook.cwd && `Folder: ${hook.cwd}`,
    hook.timeoutSec && `Time limit: ${hook.timeoutSec} s`,
    only && `Only for: ${only}`,
  ].filter(Boolean).join('\n');
}

/** The next hook to ask about: waiting for approval, and not asked about (in this version) yet. */
export function nextToAsk(status, asked) {
  if (!status?.canApprove || status.appAsks) return null;
  return status.hooks.find((x) => x.state === 'needs approval' && !asked.has(`${x.id}\u001f${x.version}`)) ?? null;
}

const asked = new Set();
let busy = false;
let done = false;

/** Looks for a hook to ask about and asks (one at a time). `ctx`: { api, h, icon, toast }. */
export async function askAboutHooks(ctx) {
  if (done || busy || document.querySelector('dialog.hook-ask')) return;
  busy = true;
  try {
    const status = await ctx.api('/api/hooks');
    // Not the owner's session (it can't approve), or the desktop app asks: this page never needs to look again.
    if (!status.canApprove || status.appAsks) {
      done = true;
      return;
    }
    const next = nextToAsk(status, asked);
    if (!next) return;
    asked.add(`${next.id}\u001f${next.version}`);
    show(ctx, next, status.events?.[next.event]);
  } catch {
    // Hooks off, or not signed in: nothing to ask.
  } finally {
    busy = false;
  }
}

function show(ctx, x, eventText) {
  const { h, icon } = ctx;
  const dialog = h('dialog', { class: 'look-dialog hook-ask', 'aria-label': 'A hook asks to run' });
  const decide = async (allow) => {
    const id = encodeURIComponent(x.id);
    try {
      // Allowing or refusing names the version shown: one allowed or changed meanwhile is neither (it says so).
      if (allow) await ctx.api(`/api/hooks/${id}/approve`, { method: 'POST', body: { version: x.version } });
      else await ctx.api(`/api/hooks/${id}/reject`, { method: 'POST', body: { version: x.version } });
      ctx.toast(allow ? 'Allowed: it runs from now on' : 'Not allowed: it was removed');
    } catch (err) {
      ctx.toast(err.message, 'error');
    }
    dialog.close();
  };
  dialog.addEventListener('keydown', (e) => e.stopPropagation());
  dialog.append(
    h('h2', null, `${x.hook.addedBy ?? 'An AI agent'} wants to run a hook`),
    h('p', null, 'When ', h('code', null, x.event), eventText ? ` (${eventText.toLowerCase()})` : '', ', it would:'),
    h('pre', { class: 'hook-sample' }, describeHook(x.hook)),
    h('p', { class: 'muted small' }, 'Allow it and it runs from now on, without asking again. If it is changed later by an agent, you’re asked again.'),
    h('div', { class: 'row buttons' },
      h('button', { class: 'btn primary', type: 'button', onclick: () => decide(true) }, icon('check', { size: 14 }), 'Allow'),
      h('button', { class: 'btn', type: 'button', onclick: () => decide(false) }, 'Don’t allow'),
      h('button', { class: 'btn ghost', type: 'button', title: 'Decide later in Settings › Hooks', onclick: () => dialog.close() }, 'Later')));
  dialog.addEventListener('close', () => dialog.remove());
  document.body.append(dialog);
  dialog.showModal();
}
