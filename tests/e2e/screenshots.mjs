// Screenshots and pictures for the README, the docs and the website, from a made-up demo board (never anyone's real
// tasks).
//
//   cd tests/e2e && npm install && npx playwright install chromium
//   node screenshots.mjs                      # everything below (the Windows sidebar on Windows; quit One Thing first)
//   node screenshots.mjs --web-only           # everything but the Windows sidebar
//   node screenshots.mjs --only board,og      # just these (names as in docs/images, without .png)
//   node screenshots.mjs --webp-only --only macos-ios-light,macos-ios-dark   # only the website's WebP copies
//
// It starts its own One Thing on a throwaway data folder and port, fills it with the demo tasks below through the
// API (as you and as an AI agent), and saves to docs/images:
//   today-light, today-dark, board, tasks, plan, reports, task-details, phone   the web app
//   browser-extension                                                           the extension's side panel, paired
//   logo                                                                        the app icon (from wwwroot/img/logo.svg)
//   windows-sidebar, windows-sidebar-dark                                       the Windows sidebar (Windows only)
// and to website/public/img: og.png (the link preview) and a WebP copy of each picture the site shows (at most 1600
// wide, quality 82), with the sizes in index.html kept up to date. macos-ios-light/-dark come from the iPhone
// simulator (.github/workflows/screenshots.yml); the app icons from build/icons.py. Your own app and tasks are never
// touched.
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const out = path.join(root, 'docs', 'images');
const site = path.join(root, 'website', 'public');
const port = Number(process.env.SHOT_PORT || 5391);
const token = 'screenshots-token-0123456789abcdefghijkl';
const args = process.argv.slice(2);
const webOnly = args.includes('--web-only');
const webpOnly = args.includes('--webp-only');
const onlyAt = args.indexOf('--only');
const only = onlyAt >= 0 ? (args[onlyAt + 1] ?? '').split(',').map((n) => n.trim().replace(/\.png$/, '')).filter(Boolean) : null;
const wanted = (name) => !only || only.includes(name);
const base = `http://127.0.0.1:${port}`;

const webViews = ['today-light', 'today-dark', 'board', 'tasks', 'plan', 'reports', 'task-details', 'phone'];
const sidebarShots = ['windows-sidebar', 'windows-sidebar-dark'];
// The pictures the website shows (website/public/index.html), as WebP.
const onWebsite = ['today-light', 'today-dark', 'board', 'plan', 'reports', 'tasks', ...sidebarShots, 'macos-ios-light', 'macos-ios-dark', 'browser-extension'];
const known = [...webViews, 'browser-extension', 'logo', 'og', ...sidebarShots, 'macos-ios-light', 'macos-ios-dark'];
for (const name of only ?? []) {
  if (!known.includes(name)) {
    console.error(`Unknown picture "${name}". These are: ${known.join(', ')}`);
    process.exit(2);
  }
}

const data = webpOnly ? null : fs.mkdtempSync(path.join(os.tmpdir(), 'onething-shots-'));

/** The PNGs saved in this run (their WebP copies are made at the end). */
const saved = [];
const auth = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };

// Everything in the throwaway folder: ONETHING_VAULT (or _DATA) set in your shell would otherwise win over --data and
// put the demo tasks in your own tasks folder. No cloud sync either.
// ONETHING_FEEDBACK_SEND=0: a demo never sends feedback anywhere.
const isolated = data ? { ...process.env, ONETHING_DATA: data, ONETHING_VAULT: path.join(data, 'vault'), ONETHING_CLOUD: 'off', ONETHING_FEEDBACK_SEND: '0' } : null;

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(method, url, body, actor) {
  // As the person at this computer (the owner key the app keeps in its data folder): the demo board is "yours".
  const keyFile = path.join(data, 'owner-key');
  const owner = fs.existsSync(keyFile) ? { 'X-OneThing-Owner-Key': fs.readFileSync(keyFile, 'utf8').trim() } : {};
  const headers = { ...auth, ...owner, ...(actor ? { 'X-OneThing-Actor': actor } : {}) };
  const res = await fetch(base + url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

/** Stops a process and what it started (dotnet run starts the app as a child). */
function stop(child) {
  if (process.platform === 'win32') {
    try {
      execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    } catch { /* gone already */ }
  } else {
    child.kill();
  }
}

async function startServer() {
  const server = spawn('dotnet', ['run', '--project', path.join(root, 'src', 'OneThing.Web'), '--no-launch-profile', '--', '--port', String(port), '--data', data, '--no-history'], {
    env: { ...isolated, ONETHING_TOKEN: token, ONETHING_AGENT_PATH: path.join(data, 'no-agents') },
    stdio: 'ignore',
  });
  for (let i = 0; i < 240; i++) {
    try {
      if ((await fetch(`${base}/health`)).ok) return server;
    } catch { /* starting */ }
    await wait(500);
  }
  stop(server);
  throw new Error('The demo server did not start.');
}

/** Today at hh:mm local time (or another day). */
function at(hour, minute = 0, days = 0) {
  const d = new Date();
  d.setDate(d.getDate() + days);
  d.setHours(hour, minute, 0, 0);
  return d.toISOString();
}

const daysFromNow = (n) => new Date(Date.now() + n * 86400000).toISOString();

/** The demo board: someone shipping a release at work, with a life and a side project. */
async function seed() {
  const groups = await api('GET', '/api/groups');
  const work = groups.find((g) => g.name === 'Work').id;
  const personal = groups.find((g) => g.name === 'Personal').id;
  const garden = (await api('POST', '/api/groups', { name: 'Garden', color: '#22c55e' })).id;
  await api('POST', '/api/labels', { name: 'Deep work', color: '#8b5cf6' });
  await api('POST', '/api/labels', { name: 'Quick win', color: '#10b981' });

  const add = (body, actor) => api('POST', '/api/items', body, actor);
  const agent = 'agent:claude';

  // The release: rollout steps, an agent logging progress, time tracked, on the board as Doing.
  const release = await add({ title: 'Ship v2.4 release', groupId: work, priority: 'high', icon: '🚀', deadline: daysFromNow(2), tags: ['release'], labels: ['Deep work'], details: 'Release notes are in the wiki. Watch the error dashboard after each ring.' });
  const steps = await api('POST', `/api/items/${release.id}/steps`, { titles: ['Ring 0: internal', 'Ring 1: 5% of users', 'Ring 2: everyone'], stepDelayMinutes: 1440 });
  await api('POST', `/api/items/${steps[0].id}/notes`, { text: 'Ring 0 healthy: error rate 0.02%, p95 latency flat. Starting ring 1 tomorrow.' }, agent);
  await api('POST', `/api/items/${steps[0].id}/complete`, {}, agent);
  await api('POST', `/api/items/${release.id}/stage`, { stage: 'doing' });

  const review = await add({ title: 'Reply to the security review', groupId: work, priority: 'critical', icon: '🛡️', tags: ['security'] });
  await api('POST', `/api/items/${review.id}/reminders`, { inMinutes: 0, message: 'Review closes at 5 pm' });
  await api('POST', `/api/items/${review.id}/notes`, { text: 'Pulled the report: 5 findings, 3 already fixed in v2.3.' }, agent);
  await api('POST', `/api/items/${review.id}/notes`, { text: 'Two findings left: token lifetime and the CSP header. Draft replies in the doc.' });

  const design = await add({ title: "Review Maya's API design doc", groupId: work, labels: ['Deep work'], tags: ['api'] });
  await api('POST', `/api/items/${design.id}/notes`, { text: 'Section 3 (pagination) needs a second look.' });
  await api('POST', `/api/items/${design.id}/notes`, { text: 'Maya asked for comments by Thursday.' });
  const flaky = await add({ title: 'Fix flaky checkout test', groupId: work, icon: '🐛', color: '#ef4444', tags: ['bug', 'ci'], labels: ['Quick win'] });
  await api('POST', `/api/items/${flaky.id}/stage`, { stage: 'next' });
  const ci = await add({ title: 'Migrate CI to the new runners', groupId: work, tags: ['ci'], details: 'Linux first, then macOS. Keep the old pool for a week.' });
  await api('POST', `/api/items/${ci.id}/stage`, { stage: 'doing' });
  const oneOnOne = await add({ title: 'Prep 1:1 agenda', groupId: work, repeat: 'every week on Thursday', icon: '📝' });
  const planning = await add({ title: 'Write Q3 planning doc', groupId: work, labels: ['Deep work'] });
  await api('POST', `/api/items/${planning.id}/schedule`, { at: at(9, 0, 1), notify: true });
  const triage = await add({ title: 'Triage new bug reports', groupId: work, tags: ['bug'], stage: 'inbox' });
  const runbook = await add({ title: 'Update the on-call runbook', groupId: work, tags: ['ops'] });
  await api('POST', `/api/items/${runbook.id}/complete`, {});
  const feedback = await add({ title: 'Send interview feedback', groupId: work, labels: ['Quick win'] }, agent);
  await api('POST', `/api/items/${feedback.id}/complete`, {});

  // Life.
  await add({ title: 'Renew passport', groupId: personal, icon: '✈️', deadline: daysFromNow(9), tags: ['admin'], details: 'Bring two photos and the old passport.' });
  await add({ title: 'Book dentist appointment', groupId: personal, labels: ['Quick win'], tags: ['health'] });
  const hike = await add({ title: 'Plan the weekend hike', groupId: personal, icon: '🥾', tags: ['outdoors'] });
  await api('POST', `/api/items/${hike.id}/schedule`, { at: at(10, 0, 2), notify: true });
  const bills = await add({ title: 'Pay the electricity bill', groupId: personal, repeat: 'every month on the 15th', icon: '💡' });
  await api('POST', `/api/items/${bills.id}/complete`, {});

  // A side project.
  const onboarding = await add({ title: 'Sketch onboarding screens', groupId: garden, icon: '🎨', color: '#22c55e', labels: ['Deep work'] });
  const gardenCi = await add({ title: 'Set up CI for the garden app', groupId: garden, tags: ['ci'] });
  await add({ title: 'Pick a plant-care API', groupId: garden, tags: ['research'] }, agent);

  // Four weeks of tracked time, for the reports: weekday mornings and afternoons, some evenings on the side project.
  const log = async (id, day, hour, minutes) => api('POST', `/api/items/${id}/time`, { start: at(hour, 0, -day), end: new Date(new Date(at(hour, 0, -day)).getTime() + minutes * 60000).toISOString() });
  const workTasks = [release.id, ci.id, design.id, review.id, flaky.id, triage.id];
  for (let day = 1; day <= 27; day++) {
    const weekday = new Date(at(12, 0, -day)).getDay();
    if (weekday === 0 || weekday === 6) {
      if (day % 3 === 0) await log(onboarding.id, day, 10, 60 + (day % 4) * 15);
      continue;
    }

    await log(workTasks[day % workTasks.length], day, 9, 60 + (day * 17) % 70);
    await log(workTasks[(day + 2) % workTasks.length], day, 14, 45 + (day * 23) % 80);
    if (day % 4 === 1) await log(gardenCi.id, day, 20, 40);
  }

  // This week's plan (Monday to Friday), around today.
  const monday = -((new Date().getDay() + 6) % 7);
  const plan = (id, day, hour, minute, minutes) => api('POST', `/api/items/${id}/plan`, { start: at(hour, minute, monday + day), minutes });
  await plan(triage.id, 0, 9, 30, 60);
  await plan(review.id, 1, 11, 0, 45);
  await plan(onboarding.id, 2, 16, 0, 60);
  await plan(ci.id, 3, 10, 0, 90);
  await plan(oneOnOne.id, 3, 15, 0, 30);
  await plan(release.id, 4, 9, 30, 90);
  await plan(design.id, 4, 13, 0, 60);
  await plan(flaky.id, 4, 15, 0, 45);
  await plan(planning.id, 4, 11, 0, 90);

  // Energy: how draining each task is (an hour), and what there is in a day. Friday's plan is too much on purpose.
  for (const [task, energy] of [[review, 3], [release, 3], [planning, 2], [design, 2], [ci, 2], [onboarding, 2], [flaky, 1], [triage, 1]]) {
    await api('PATCH', `/api/items/${task.id}`, { energy });
  }
  await api('PUT', '/api/energy', { perDay: 8 });

  return { release, review, design };
}

/** Saves a picture to docs/images (or another folder) and notes it for its WebP copy. */
async function save(page, name, options = {}, folder = out) {
  const file = path.join(folder, `${name}.png`);
  await page.screenshot({ path: file, ...options });
  saved.push(name);
  console.log(`saved ${path.relative(root, file).replaceAll('\\', '/')}`);
}

async function captureWeb(browser, ids) {
  const shoot = async (name, { hash = '#/today', dark = false, width = 1440, height = 900, full = false, prepare } = {}) => {
    if (!wanted(name)) return;
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1.5, colorScheme: dark ? 'dark' : 'light' });
    const page = await context.newPage();
    const launch = await api('POST', '/api/launch', { return: '/' });
    await page.goto(launch.url);
    await page.waitForSelector('#board');
    await page.evaluate((h) => { location.hash = h; }, hash);
    await wait(1200);
    if (prepare) await prepare(page);
    await save(page, name, { fullPage: full });
    await context.close();
  };

  await shoot('today-light');
  await shoot('today-dark', { dark: true });
  await shoot('board', { hash: '#/board' });
  await shoot('tasks', { hash: '#/tasks', prepare: async (page) => { await page.getByRole('button', { name: 'Expand all' }).click(); await wait(500); } });
  await shoot('plan', { hash: '#/plan' });
  await shoot('reports', { hash: '#/reports' });
  await shoot('task-details', { hash: `#/task/${ids.release.id}` });
  await shoot('phone', { width: 390, height: 844 });
}

/**
 * The browser extension's side panel, paired with the demo board the way a person does it (a code from the app).
 * Extensions only load in a persistent profile, in Playwright's own Chromium (as in extension.spec.js).
 */
async function captureExtension() {
  const extensionDir = path.join(root, 'extension');
  const profile = path.join(data, 'browser-profile');
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium',
    viewport: { width: 380, height: 929 },
    deviceScaleFactor: 2,
    colorScheme: 'light',
    args: [`--disable-extensions-except=${extensionDir}`, `--load-extension=${extensionDir}`],
  });
  try {
    const isOurs = (w) => w.url().endsWith('/background.js');
    const worker = context.serviceWorkers().find(isOurs) ?? await context.waitForEvent('serviceworker', { predicate: isOurs, timeout: 30000 });
    const id = new URL(worker.url()).host;
    const panel = context.pages()[0] ?? await context.newPage();
    await panel.goto(`chrome-extension://${id}/sidepanel.html`);
    await panel.locator('#setup').waitFor();
    await panel.locator('#advanced summary').click();
    await panel.locator('#server').fill(base);
    const { code } = await api('POST', '/api/plugins/browser-extension/pair');
    await panel.locator('#code').fill(`${code.slice(0, 3)} ${code.slice(3)}`);
    await panel.getByRole('button', { name: 'Connect' }).click();
    await panel.locator('#main').waitFor({ timeout: 15000 });
    await panel.getByText('Reply to the security review').first().waitFor({ timeout: 15000 });
    await panel.locator('#status').evaluate((el) => { el.hidden = true; }); // "Paired": only once, right after pairing
    // A page to take a note on, in the tab beside the panel (the panel shows its title under "Attach this page").
    await context.route('https://wiki.example.com/**', (route) => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Security review: findings and replies</title><h1>Security review</h1>' }));
    const article = await context.newPage();
    await article.goto('https://wiki.example.com/security-review');
    await article.bringToFront();
    await panel.locator('#page').filter({ hasText: 'Security review' }).waitFor({ timeout: 10000 });
    await panel.mouse.move(0, 0);
    await wait(1200);
    await save(panel, 'browser-extension');
  } finally {
    await context.close();
  }
}

/** The app icon at 256×256 for the README, from the logo the web app shows (build/icons.py draws it). */
async function captureLogo(browser) {
  const svg = fs.readFileSync(path.join(root, 'src', 'OneThing.Server', 'wwwroot', 'img', 'logo.svg'), 'utf8');
  const page = await browser.newPage({ viewport: { width: 256, height: 256 }, deviceScaleFactor: 1 });
  await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:256px;height:256px}</style>${svg}`);
  await save(page, 'logo', { omitBackground: true });
  await page.close();
}

/** The link preview (Open Graph, 1200×630): the name, the icon, what it does, and the Today view beside them. */
async function captureOg(browser) {
  const today = path.join(out, 'today-light.png');
  if (!fs.existsSync(today)) throw new Error('og needs docs/images/today-light.png: run with --only today-light,og');
  const logo = fs.readFileSync(path.join(root, 'website', 'public', 'img', 'favicon.svg'), 'utf8');
  const shot = `data:image/png;base64,${fs.readFileSync(today).toString('base64')}`;
  const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1, colorScheme: 'light' });
  await page.setContent(`<!doctype html><meta charset="utf-8"><style>
    * { box-sizing: border-box; }
    html, body { margin: 0; width: 1200px; height: 630px; overflow: hidden; }
    body { font-family: "Segoe UI Variable Display", "Segoe UI", -apple-system, BlinkMacSystemFont, system-ui, sans-serif; color: #11142b;
      background: radial-gradient(circle at 12% 18%, #e0e7ff 0, transparent 42%), radial-gradient(circle at 88% 100%, #fce7f3 0, transparent 45%), #f6f7fb; position: relative; }
    .copy { position: absolute; left: 64px; top: 0; bottom: 0; width: 420px; display: flex; flex-direction: column; justify-content: center; }
    .brand { display: flex; align-items: center; gap: 18px; margin-bottom: 34px; }
    .brand svg { width: 84px; height: 84px; display: block; }
    .brand span { font-size: 46px; font-weight: 700; letter-spacing: -0.02em; }
    h1 { margin: 0 0 22px; font-size: 44px; line-height: 1.1; font-weight: 700; letter-spacing: -0.02em; }
    h1 em { font-style: normal; background: linear-gradient(90deg, #4f46e5, #7c3aed, #db2777); -webkit-background-clip: text; color: transparent; }
    p { margin: 0; font-size: 22px; line-height: 1.4; color: #4b5068; }
    .shot { position: absolute; left: 520px; top: 64px; width: 1040px; border-radius: 18px; border: 1px solid #e3e5ee;
      box-shadow: 0 24px 60px rgba(49, 46, 129, .22), 0 4px 14px rgba(17, 20, 43, .08); }
  </style>
  <div class="copy">
    <div class="brand">${logo}<span>One Thing</span></div>
    <h1>The to-do list that tells you what to do <em>now</em>.</h1>
    <p>Later work stays out of sight until it's due. Your AI agents can help.</p>
  </div>
  <img class="shot" src="${shot}" alt="">`);
  await page.locator('.shot').evaluate((img) => img.decode());
  await save(page, 'og', {}, path.join(site, 'img'));
  await page.close();
}

/**
 * The website's WebP copy of a picture (the site's img folder, at most 1600 wide, quality 82), encoded by the browser
 * so no other tool is needed. The width and height in index.html follow the new size.
 */
async function writeWebp(browser, name) {
  const png = path.join(out, `${name}.png`);
  if (!fs.existsSync(png)) {
    console.log(`no docs/images/${name}.png, so no ${name}.webp`);
    return;
  }

  const page = await browser.newPage();
  const result = await page.evaluate(async ({ source, maxWidth, quality }) => {
    const img = new Image();
    img.src = source;
    await img.decode();
    const scale = Math.min(1, maxWidth / img.naturalWidth);
    const width = Math.round(img.naturalWidth * scale);
    const height = Math.round(img.naturalHeight * scale);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, width, height);
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/webp', quality));
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    return { type: blob.type, base64: btoa(binary), width, height };
  }, { source: `data:image/png;base64,${fs.readFileSync(png).toString('base64')}`, maxWidth: 1600, quality: 0.82 });
  await page.close();
  if (result.type !== 'image/webp') throw new Error(`This browser can't write WebP (it gave ${result.type}).`);

  const file = path.join(site, 'img', `${name}.webp`);
  fs.writeFileSync(file, Buffer.from(result.base64, 'base64'));
  console.log(`saved website/public/img/${name}.webp (${result.width}×${result.height})`);

  const index = path.join(site, 'index.html');
  const html = fs.readFileSync(index, 'utf8');
  const sized = html.replace(new RegExp(`(<img src="/img/${name}\\.webp" width=")\\d+(" height=")\\d+(")`, 'g'), `$1${result.width}$2${result.height}$3`);
  if (sized !== html) fs.writeFileSync(index, sized);
}

/** Whether a One Thing sidebar is running (only one runs at a time: a second one would just bring it forward). */
function sidebarRunning() {
  try {
    return execFileSync('tasklist', ['/FI', 'IMAGENAME eq OneThing.exe', '/NH'], { encoding: 'utf8' }).includes('OneThing.exe');
  } catch {
    return false;
  }
}

/** The Windows sidebar, light and dark (the app renders itself to a PNG: --screenshot). */
async function captureSidebar() {
  if (sidebarRunning()) {
    console.log('Skipping the sidebar: quit One Thing first (only one sidebar runs at a time), then run this again.');
    return;
  }

  const exe = path.join(root, 'src', 'OneThing.Windows', 'bin', 'Debug', 'net10.0-windows10.0.19041.0', 'OneThing.exe');
  execFileSync('dotnet', ['build', path.join(root, 'src', 'OneThing.Windows'), '-v', 'q', '--nologo'], { stdio: 'inherit' });
  for (const theme of ['light', 'dark']) {
    const name = theme === 'light' ? 'windows-sidebar' : 'windows-sidebar-dark';
    if (!wanted(name)) continue;
    const file = path.join(out, `${name}.png`);
    fs.rmSync(file, { force: true });
    // The demo data folder: your own tasks are never opened.
    // The AI apps installed here count (Ask AI shows as it would for you); the tasks are only the demo ones.
    const app = spawn(exe, ['--data', data, '--port', String(port + 1), '--no-dock', '--theme', theme, '--screenshot', file], {
      env: isolated,
      stdio: 'ignore',
    });
    for (let i = 0; i < 60 && !fs.existsSync(file); i++) await wait(500);
    await wait(500);
    stop(app);
    await wait(1500);
    if (fs.existsSync(file)) {
      saved.push(name);
      console.log(`saved docs/images/${name}.png`);
    } else {
      console.log(`no ${name}.png (the sidebar didn't start)`);
    }
  }
}

fs.mkdirSync(out, { recursive: true });
if (webpOnly) {
  const browser = await chromium.launch();
  try {
    for (const name of onWebsite.filter(wanted)) await writeWebp(browser, name);
  } finally {
    await browser.close();
  }
  process.exit(0);
}

const sidebar = !webOnly && process.platform === 'win32' && sidebarShots.some(wanted);
if (webViews.some(wanted) || wanted('browser-extension') || sidebar) {
  const server = await startServer();
  const browser = await chromium.launch();
  try {
    const ids = await seed();
    await captureWeb(browser, ids);
    if (wanted('browser-extension')) await captureExtension();
  } finally {
    await browser.close();
    stop(server);
  }
}

if (sidebar) {
  await wait(2000); // the demo server lets go of the tasks folder
  await captureSidebar();
}

{
  const browser = await chromium.launch();
  try {
    if (wanted('logo')) await captureLogo(browser);
    if (wanted('og')) await captureOg(browser);
    for (const name of saved.filter((n) => onWebsite.includes(n))) await writeWebp(browser, name);
  } finally {
    await browser.close();
  }
}

// Files can stay locked a moment after the apps stop.
fs.rmSync(data, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
