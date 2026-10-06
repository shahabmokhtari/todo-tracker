import { test } from 'node:test';
import assert from 'node:assert/strict';
import { embedNames, splitEmbeds, pastedName, imageFiles } from '../../src/TodoTracker.Server/wwwroot/js/media.js';

test('embeds are found in Obsidian and markdown form', () => {
  const text = 'Before ![[Pasted image 20261006132517.png]] mid ![[shot.png|300]] and ![diagram](../_attachments/ab12cd34/flow%20chart.png) end';
  assert.deepEqual(embedNames(text), ['Pasted image 20261006132517.png', 'shot.png', 'flow chart.png']);
});

test('text splits into plain parts and embeds, in order', () => {
  assert.deepEqual(splitEmbeds('a ![[x.png]] b'), [{ text: 'a ' }, { embed: 'x.png' }, { text: ' b' }]);
  assert.deepEqual(splitEmbeds('no images'), [{ text: 'no images' }]);
  assert.deepEqual(splitEmbeds('![[only.png]]'), [{ embed: 'only.png' }]);
});

test('links that are not embeds stay text', () => {
  assert.deepEqual(embedNames('[[Another task]] and [a link](https://example.com) and ![[]]'), []);
});

test('pasted files get a stable, readable name like Obsidian gives them', () => {
  const at = new Date(2026, 9, 6, 13, 25, 17);
  assert.equal(pastedName({ name: 'image.png', type: 'image/png' }, at), 'Pasted image 20261006132517.png');
  assert.equal(pastedName({ name: '', type: 'image/jpeg' }, at), 'Pasted image 20261006132517.jpg');
  assert.equal(pastedName({ name: 'Quarterly plan.pdf', type: 'application/pdf' }, at), 'Quarterly plan.pdf');
  assert.equal(pastedName({ name: '', type: 'application/octet-stream' }, at), 'Pasted file 20261006132517');
});

test('only files are taken from a paste; text-only pastes stay text', () => {
  const file = { kind: 'file', getAsFile: () => ({ name: 'image.png', type: 'image/png' }) };
  const text = { kind: 'string', type: 'text/plain' };
  assert.equal(imageFiles({ items: [file] }).length, 1);
  assert.equal(imageFiles({ items: [text] }).length, 0);
  // Copying from an office app puts text and a picture of it on the clipboard: the text wins.
  assert.equal(imageFiles({ items: [text, file] }).length, 0);
  assert.equal(imageFiles(null).length, 0);
});
