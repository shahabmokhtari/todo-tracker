// Renders mockups.html to the PNGs next to it (360 × 760, at 2× for detail).
// Run from tests/e2e (it has Playwright):  cd tests/e2e && npm install && node ../../docs/design/windows-sidebar/render.mjs
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

// Playwright comes from the folder you run this in (tests/e2e), not from next to this file.
const { chromium } = createRequire(path.join(process.cwd(), 'package.json'))('@playwright/test');

const here = path.dirname(fileURLToPath(import.meta.url));
const page = pathToFileURL(path.join(here, 'mockups.html')).href;
const shots = [
  ['today-light', 'today', 'light'],
  ['today-dark', 'today', 'dark'],
  ['row-hover', 'hover', 'light'],
  ['context-menu', 'menu', 'light'],
  ['focus-timer', 'timer', 'dark'],
  ['empty', 'empty', 'light'],
];

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 360, height: 760 }, deviceScaleFactor: 2 });
const tab = await context.newPage();
for (const [name, state, theme] of shots) {
  await tab.goto(`${page}?state=${state}&theme=${theme}`);
  await tab.screenshot({ path: path.join(here, `${name}.png`) });
  console.log(`${name}.png`);
}
await browser.close();
