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
    // Single-use sign-in link for opening the dashboard in a tab; the API token never goes into a URL.
    launchUrl: async (path = '/') => (await call('/api/launch', { method: 'POST', body: { return: path } })).url,
  };
}
