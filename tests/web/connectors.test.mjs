import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loopMarkdown, taskMarkdown, taskHtml, taskPictures } from '../../src/TodoTracker.Server/wwwroot/js/plugins/loop.js';
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

const task = {
  id: 't1', title: 'Launch <site>', details: 'Hero copy\n![[Pasted image 1.png]]', deadline: '2026-01-08T17:00:00', done: false,
  tags: ['deep work'], labels: [{ name: 'Urgent', color: '#e11' }],
  notes: [{ itemId: 't1', at: '2026-01-05T09:00:00', text: 'Agreed with Sam\n![[Pasted image 1.png]]' }],
  attachments: [
    { fileName: 'Pasted image 1.png', storedName: 'Pasted image 1.png', url: '/a/t1/1' },
    { fileName: 'brief.pdf', storedName: 'brief.pdf', url: '/a/t1/2' },
    { fileName: 'mock.jpg', storedName: 'mock.jpg', url: '/a/t1/3' },
  ],
  children: [
    { id: 's1', title: 'Write copy', details: 'Short', completedAt: null, children: [], notes: [{ itemId: 's1', at: '2026-01-06T10:00:00', text: 'Draft 1 sent' }], attachments: [] },
    { id: 's2', title: 'Old step', completedAt: '2026-01-04T10:00:00', children: [], notes: [], attachments: [] },
  ],
};

test('Copy for Loop on one task: details, subtasks, notes and files, as a checklist', () => {
  assert.equal(taskMarkdown(task), [
    '## Launch <site>',
    'Due 2026-01-08 · Urgent · Tags: deep work',
    '',
    'Hero copy',
    '(picture: Pasted image 1.png)',
    '',
    '- [ ] Write copy',
    '  Short',
    '  - Note 2026-01-06: Draft 1 sent',
    '- [x] Old step',
    '',
    '**Notes**',
    '- 2026-01-05: Agreed with Sam (picture: Pasted image 1.png)',
    '',
    '**Files**',
    '- brief.pdf',
    '- mock.jpg',
    '',
  ].join('\n'));
  assert.ok(!taskMarkdown(task, { includeDone: false }).includes('Old step'));
});

test('the rich copy shows the pictures themselves (and escapes text)', () => {
  // Embedded pictures are fetched once; unembedded picture attachments are shown too; other files are listed.
  assert.deepEqual(taskPictures(task).map((p) => p.name), ['Pasted image 1.png', 'mock.jpg']);
  const html = taskHtml(task, { image: (url) => (url.endsWith('mock.jpg') ? 'data:image/jpeg;base64,BBBB' : url.includes('Pasted') ? 'data:image/png;base64,AAAA' : null) });
  assert.match(html, /<h2>Launch &lt;site&gt;<\/h2>/);
  assert.match(html, /<img src="data:image\/png;base64,AAAA" alt="Pasted image 1.png"/);
  assert.match(html, /<img src="data:image\/jpeg;base64,BBBB" alt="mock.jpg"/);
  assert.match(html, /<li>☐ Write copy/);
  assert.match(html, /<li>☑ Old step/);
  assert.match(html, /brief\.pdf/);
  // No picture (too big, or it failed to load): its name instead.
  assert.match(taskHtml(task, { image: () => null }), /\(picture: Pasted image 1\.png\)/);
});