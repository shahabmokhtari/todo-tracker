import { test } from 'node:test';
import assert from 'node:assert/strict';
import { embedNames, splitEmbeds, pastedName, imageFiles } from '../../src/TodoTracker.Server/wwwroot/js/media.js';

test('embeds are found in Obsidian and markdown form', () => {
  const text = 'Before ![[Pasted image 20261006132517.png]] mid ![[shot.png|300]] and ![diagram](../_attachments/ab12cd34/flow%20chart.png) end';
  assert.deepEqual(embedNames(text), ['Pasted image 20261006132517.png', 'shot.png', 'flow chart.png']);
});

test('text splits into plain parts and embeds, in order', () => {
  assert.deepEqual(splitEmbeds('a ![[x.png]] b'), [{ text: 'a ' }, { embed: 'x.png', image: true }, { text: ' b' }]);
  assert.deepEqual(splitEmbeds('no images'), [{ text: 'no images' }]);
  assert.deepEqual(splitEmbeds('![[only.png]]'), [{ embed: 'only.png', image: true }]);
});

test('only this task\'s files are embeds; web images and other notes stay text', () => {
  // Review finding: these all showed as broken images.
  assert.deepEqual(splitEmbeds('see ![logo](https://example.com/logo.png) ok'), [{ text: 'see ![logo](https://example.com/logo.png) ok' }]);
  assert.deepEqual(embedNames('![[Another task]] and ![x](//cdn.example.com/a.png) and [[Link]] and ![[]]'), []);
  assert.deepEqual(splitEmbeds('![[Plan.pdf]]'), [{ embed: 'Plan.pdf', image: false }]);
});

test('Obsidian links with a folder or a section point to the file itself', () => {
  assert.deepEqual(embedNames('![[_attachments/ab12/Pasted image 1.png]] ![[shot.png#^top|200]]'), ['Pasted image 1.png', 'shot.png']);
});

test('pasted files get a stable, readable name like Obsidian gives them', () => {
  const at = new Date(2026, 9, 6, 13, 25, 17);
  assert.equal(pastedName({ name: 'image.png', type: 'image/png' }, at), 'Pasted image 20261006132517.png');
  assert.equal(pastedName({ name: '', type: 'image/jpeg' }, at), 'Pasted image 20261006132517.jpg');
  assert.equal(pastedName({ name: 'Quarterly plan.pdf', type: 'application/pdf' }, at), 'Quarterly plan.pdf');
  assert.equal(pastedName({ name: '', type: 'application/octet-stream' }, at), 'Pasted file 20261006132517.bin');
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
