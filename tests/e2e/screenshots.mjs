// Screenshots for the README and docs, from a made-up demo board (never anyone's real tasks).
//
//   cd tests/e2e && npm install && npx playwright install chromium
//   node screenshots.mjs                 # web views (any OS) + the Windows sidebar (Windows; quit Todo Tracker first)
//   node screenshots.mjs --web-only
//
// It starts its own Todo Tracker on a throwaway data folder and port, fills it with the demo tasks below through the
// API (as you and as an AI agent), and saves the pictures to docs/images. Your own app and tasks are never touched.
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const out = path.join(root, 'docs', 'images');
const port = Number(process.env.SHOT_PORT || 5391);
const token = 'screenshots-token-0123456789abcdefghijkl';
const data = fs.mkdtempSync(path.join(os.tmpdir(), 'tt-shots-'));
const base = `http://127.0.0.1:${port}`;
const webOnly = process.argv.includes('--web-only');
const auth = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };

// Everything in the throwaway folder: TODOTRACKER_VAULT (or _DATA) set in your shell would otherwise win over --data and
// put the demo tasks in your own tasks folder. No cloud sync either.
const isolated = { ...process.env, TODOTRACKER_DATA: data, TODOTRACKER_VAULT: path.join(data, 'vault'), TODOTRACKER_CLOUD: 'off' };

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(method, url, body, actor) {
  const headers = { ...auth, ...(actor ? { 'X-TodoTracker-Actor': actor } : {}) };
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
  const server = spawn('dotnet', ['run', '--project', path.join(root, 'src', 'TodoTracker.Web'), '--no-launch-profile', '--', '--port', String(port), '--data', data, '--no-history'], {
    env: { ...isolated, TODOTRACKER_TOKEN: token, TODOTRACKER_AGENT_PATH: path.join(data, 'no-agents') },
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
  await api('POST', `/api/items/${review.id}/notes`, { text: 'Two findings left: token lifetime and the CSP header. Draft replies in the doc.' });

  const design = await add({ title: "Review Maya's API design doc", groupId: work, labels: ['Deep work'], tags: ['api'] });
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

async function captureWeb(ids) {
  const browser = await chromium.launch();
  const shoot = async (name, { hash = '#/today', dark = false, width = 1440, height = 900, full = false, prepare } = {}) => {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1.5, colorScheme: dark ? 'dark' : 'light' });
    const page = await context.newPage();
    const launch = await api('POST', '/api/launch', { return: '/' });
    await page.goto(launch.url);
    await page.waitForSelector('#board');
    await page.evaluate((h) => { location.hash = h; }, hash);
    await wait(1200);
    if (prepare) await prepare(page);
    await page.screenshot({ path: path.join(out, `${name}.png`), fullPage: full });
    await context.close();
    console.log(`saved docs/images/${name}.png`);
  };

  await shoot('today-light');
  await shoot('today-dark', { dark: true });
  await shoot('board', { hash: '#/board' });
  await shoot('tasks', { hash: '#/tasks', prepare: async (page) => { await page.getByRole('button', { name: 'Expand all' }).click(); await wait(500); } });
  await shoot('plan', { hash: '#/plan' });
  await shoot('reports', { hash: '#/reports' });
  await shoot('task-details', { hash: `#/task/${ids.release.id}` });
  await shoot('phone', { width: 390, height: 844 });
  await browser.close();
}

/** Whether a Todo Tracker sidebar is running (only one runs at a time: a second one would just bring it forward). */
function sidebarRunning() {
  try {
    return execFileSync('tasklist', ['/FI', 'IMAGENAME eq TodoTracker.exe', '/NH'], { encoding: 'utf8' }).includes('TodoTracker.exe');
  } catch {
    return false;
  }
}

/** The Windows sidebar, light and dark (the app renders itself to a PNG: --screenshot). */
async function captureSidebar() {
  if (sidebarRunning()) {
    console.log('Skipping the sidebar: quit Todo Tracker first (only one sidebar runs at a time), then run this again.');
    return;
  }

  const exe = path.join(root, 'src', 'TodoTracker.Windows', 'bin', 'Debug', 'net10.0-windows10.0.19041.0', 'TodoTracker.exe');
  execFileSync('dotnet', ['build', path.join(root, 'src', 'TodoTracker.Windows'), '-v', 'q', '--nologo'], { stdio: 'inherit' });
  for (const theme of ['light', 'dark']) {
    const file = path.join(out, theme === 'light' ? 'windows-sidebar.png' : 'windows-sidebar-dark.png');
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
    console.log(fs.existsSync(file) ? `saved docs/images/${path.basename(file)}` : `no ${path.basename(file)} (the sidebar didn't start)`);
  }
}

fs.mkdirSync(out, { recursive: true });
let server = await startServer();
try {
  const ids = await seed();
  await captureWeb(ids);
} finally {
  stop(server);
}

if (!webOnly && process.platform === 'win32') {
  await wait(2000); // the demo server lets go of the tasks folder
  await captureSidebar();
}

// Files can stay locked a moment after the apps stop.
fs.rmSync(data, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
