// The break rules shared with the desktop apps: tests/fixtures/breaks.json is also checked by the C# BreakScreen tests.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { breakState, tipFor } from '../../src/TodoTracker.Server/wwwroot/js/breaks.js';

const cases = JSON.parse(readFileSync(new URL('../fixtures/breaks.json', import.meta.url), 'utf8'));

for (const c of cases) {
  test(`break: ${c.name}`, () => {
    const s = breakState(c.pomodoro, Date.parse(c.now), c.dismissed ? Date.parse(c.dismissed) : null);
    assert.equal(s.show, c.expect.show);
    if (!c.expect.show) return;
    assert.equal(s.until, Date.parse(c.expect.until));
    assert.equal(s.long, c.expect.long);
    assert.equal(tipFor(s.key), c.expect.tip);
  });
}
