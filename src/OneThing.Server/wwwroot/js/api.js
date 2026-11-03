// Thin fetch wrapper. Cookie auth (set by the desktop launch link or the login form) plus the client header
// the server requires on cookie-authenticated mutations.

export class ApiError extends Error {
  constructor(status, message, { offline = false } = {}) {
    super(message);
    this.status = status;
    /** One Thing couldn't be reached at all (not running, or the connection dropped). */
    this.offline = offline;
  }
}

const OFFLINE = 'Can’t reach One Thing. Check that it’s running, then try again.';

/** fetch, with "no connection" turned into an error that says so. */
async function send(path, init) {
  try {
    return await fetch(path, init);
  } catch {
    throw new ApiError(0, OFFLINE, { offline: true });
  }
}

/** The answer as JSON (null when empty or not JSON: a page from something else answering on this address). */
async function readJson(response) {
  const body = await response.text().catch(() => '');
  if (!body) return { data: null, readable: true };
  try {
    return { data: JSON.parse(body), readable: true };
  } catch {
    return { data: null, readable: false };
  }
}

const failed = (status, data, what = 'That') => new ApiError(status, data?.detail || data?.title || `${what} didn’t work (One Thing answered ${status}). Try again in a moment.`);

let keepalive = false;

/** While the page is going away, requests must outlive it (the browser would abort a plain fetch). */
export function setKeepalive(on) {
  keepalive = on;
}

export async function api(path, { method = 'GET', body } = {}) {
  const response = await send(path, {
    method,
    keepalive,
    credentials: 'same-origin',
    headers: {
      'X-OneThing-Client': 'web',
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (response.status === 204) return null;
  const { data, readable } = await readJson(response);
  if (!response.ok) throw failed(response.status, data);
  if (!readable) throw new ApiError(response.status, 'One Thing sent an answer this page can’t read. Reload the page and try again.');
  return data;
}

export const post = (path, body = {}) => api(path, { method: 'POST', body });
export const patch = (path, body) => api(path, { method: 'PATCH', body });
export const put = (path, body) => api(path, { method: 'PUT', body });
export const del = (path) => api(path, { method: 'DELETE' });

/** Uploads one file as multipart form data (the browser sets the boundary), optionally under another name. */
export async function upload(path, file, name = file.name) {
  const form = new FormData();
  form.append('file', file, name);
  const response = await send(path, { method: 'POST', credentials: 'same-origin', headers: { 'X-OneThing-Client': 'web' }, body: form });
  const { data } = await readJson(response);
  if (!response.ok) throw failed(response.status, data, 'The upload');
  return data;
}

/** GET returning plain text (e.g. a markdown version from the history). */
export async function text(path) {
  const response = await send(path, { credentials: 'same-origin', headers: { 'X-OneThing-Client': 'web' } });
  if (!response.ok) throw failed(response.status, null);
  return response.text();
}

// Minimal DOM builder: text is always set via textContent, never innerHTML.
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'class') el.className = value;
    else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
    else if (key === 'dataset') Object.assign(el.dataset, value);
    else el.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}
