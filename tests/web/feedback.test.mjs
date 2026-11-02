import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { cleanMessage, messageLength, counterText, classify, sourceOf, KINDS, TEXTS, MAX_MESSAGE } from '../../src/TodoTracker.Server/wwwroot/js/plugins/feedback.js';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/feedback.json', import.meta.url), 'utf8'));

test('the sheet cleans the note the way every app does (tests/fixtures/feedback.json)', () => {
  for (const c of fixture.bodies) {
    const max = c.maxMessage ?? MAX_MESSAGE;
    assert.equal(cleanMessage(c.draft.message, max), c.body?.message ?? '', c.name);
  }
});

test('each answer means what the shared fixture says: sent, try again, or failed', () => {
  for (const o of fixture.outcomes) assert.equal(classify(o.status), o.outcome, String(o.status));
  // Onething on this computer couldn't be reached at all (fetch threw): try again too.
  assert.equal(classify(undefined), 'retry');
});

test('the words, kinds and limit are the shared ones', () => {
  assert.deepEqual(TEXTS, fixture.texts);
  assert.deepEqual(KINDS.map(([id, label]) => ({ id, label })), fixture.kinds);
  assert.equal(MAX_MESSAGE, fixture.relay.maxMessage);
});

test('the counter shows only near the limit, and counts an emoji as one', () => {
  assert.equal(counterText('Hello'), '');
  assert.equal(counterText('a'.repeat(1800)), '200 left');
  assert.equal(counterText('😀'.repeat(1999)), '1 left');
  assert.equal(counterText('a'.repeat(2003)), '3 too many');
  assert.equal(messageLength('😀😀 '), 2);
});

test('a link says how the sheet was opened; anything else is the button', () => {
  assert.equal(sourceOf('shake'), 'shake');
  assert.equal(sourceOf('1'), 'button');
  assert.equal(sourceOf(''), 'button');
  assert.deepEqual(fixture.sources.map(sourceOf), fixture.sources);
});
