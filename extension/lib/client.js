// Talks to the local Todo Tracker server (the Windows sidebar or `TodoTracker.Web`).
// Extension pages bypass CORS through host_permissions, and every call is attributed to the browser.

export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
    this.unauthorized = status === 401;
  }
}

export function normalizeServerUrl(value) {
  let url;
  try {
    url = new URL(String(value).trim());
  } catch {
    throw new Error('Enter a valid URL, e.g. http://127.0.0.1:5317');
  }
  if (url.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(url.hostname)) {
    throw new Error('The server must be on this computer (http://127.0.0.1 or localhost).');
  }
  return url.origin;
}

export function pageSource(tab) {
  if (!tab?.url || !/^https?:\/\//i.test(tab.url)) return null;
  return { url: tab.url, title: tab.title || tab.url };
}

/** What Todo Tracker lists this browser as, e.g. "Edge on Windows". */
export function browserName(userAgent = globalThis.navigator?.userAgent ?? '') {
  const ua = String(userAgent);
  if (!ua) return 'Browser';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const system = /Windows/.test(ua) ? 'Windows' : /Mac OS X|Macintosh/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return system ? `${browser} on ${system}` : browser;
}

/**
 * Pairs with Todo Tracker: the 6-digit code shown there (Browser extension › Get a code) is traded for this browser's
 * own token, so nothing secret has to be copied by hand. The custom header means web pages can't do this.
 */
export async function pair({ serverUrl, code, name = browserName(), fetch = globalThis.fetch }) {
  const digits = String(code ?? '').replace(/\D/g, '');
  if (digits.length !== 6) throw new Error('The pairing code has 6 digits.');
  const response = await fetch(`${normalizeServerUrl(serverUrl)}/api/plugins/browser-extension/claim`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-TodoTracker-Client': 'browser-extension' },
    body: JSON.stringify({ code: digits, name }),
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok || !data?.token) {
    throw new ApiError(response.status, data?.detail || data?.title || `Pairing failed (${response.status})`);
  }
  return data.token;
}

export function createClient({ serverUrl, token, fetch = globalThis.fetch }) {
  const base = normalizeServerUrl(serverUrl);

  async function call(path, { method = 'GET', body } = {}) {
    const response = await fetch(`${base}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        'X-TodoTracker-Actor': 'browser-extension',
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const text = await response.text();
    const data = text ? JSON.parse(text) : null;
    if (!response.ok) {
      throw new ApiError(response.status, data?.detail || data?.title || (response.status === 401 ? 'Token rejected' : `Request failed (${response.status})`));
    }
    return data;
  }

  return {
    dashboard: (groupId) => call(`/api/dashboard${groupId ? `?group=${encodeURIComponent(groupId)}` : ''}`),
    capture: (text, groupId) => call('/api/capture', { method: 'POST', body: { text, groupId: groupId || null } }),
    complete: (id) => call(`/api/items/${id}/complete`, { method: 'POST', body: {} }),
    addNote: (id, text, source) => call(`/api/items/${id}/notes`, {
      method: 'POST',
      body: { text, sourceUrl: source?.url ?? null, sourceTitle: source?.title ?? null },
    }),
    snooze: (id, minutes) => call(`/api/items/${id}/schedule`, { method: 'POST', body: { inMinutes: minutes, notify: true } }),
    // Single-use sign-in link for opening the dashboard in a tab; the API token never goes into a URL. A paired
    // browser's own token can't make those (it only reaches tasks), so it opens the dashboard as it is.
    launchUrl: async (path = '/') => {
      try {
        return (await call('/api/launch', { method: 'POST', body: { return: path } })).url;
      } catch (error) {
        if (error instanceof ApiError && error.status === 403) return `${base}${path}`;
        throw error;
      }
    },
  };
}
