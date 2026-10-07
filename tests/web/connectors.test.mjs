import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loopMarkdown } from '../../src/TodoTracker.Server/wwwroot/js/plugins/loop.js';
import { previewText } from '../../src/TodoTracker.Server/wwwroot/js/plugins/connectors.js';

const n = (title, extra = {}) => ({ title, done: false, children: [], ...extra });

test('Loop gets a nested checklist with due dates; finished tasks only when asked', () => {
  const nodes = [
    n('Ship release', { children: [n('Write notes', { deadline: '2026-01-08T17:00:00' }), n('Old step', { done: true })] }),
    n('Done thing', { done: true }),
    n('Multi\nline  title'),
  ];

  assert.equal(loopMarkdown(nodes, { title: 'Work' }), '## Work\n\n- [ ] Ship release\n  - [ ] Write notes (due 2026-01-08)\n- [ ] Multi line title\n');
  assert.equal(loopMarkdown(nodes, { includeDone: true }), '- [ ] Ship release\n  - [ ] Write notes (due 2026-01-08)\n  - [x] Old step\n- [x] Done thing\n- [ ] Multi line title\n');
  assert.equal(loopMarkdown([]), '\n');
});

test('the connector preview says what the first sync will do', () => {
  assert.equal(previewText({ addHere: 3, sendThere: 1, updateHere: 1, updateThere: 1, archive: 0 }), 'It will add 3 tasks here, send 1 task there, update 2.');
  assert.equal(previewText({ addHere: 0, sendThere: 0, updateHere: 0, updateThere: 0, archive: 2 }), 'It will archive 2 deleted here.');
  assert.equal(previewText({ addHere: 0, sendThere: 0 }), 'Nothing to change: they already match.');
});
