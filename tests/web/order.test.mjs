import { test } from 'node:test';
import assert from 'node:assert/strict';
import { moveTo, step, drop, beforeOf, changed } from '../../src/TodoTracker.Server/wwwroot/js/order.js';

const ids = ['a', 'b', 'c', 'd'];

test('moveTo puts an item at a position, clamped to the list', () => {
  assert.deepEqual(moveTo(ids, 'c', 0), ['c', 'a', 'b', 'd']);
  assert.deepEqual(moveTo(ids, 'a', 99), ['b', 'c', 'd', 'a']);
  assert.deepEqual(moveTo(ids, 'x', 0), ids);
});

test('step moves one place and stops at the ends', () => {
  assert.deepEqual(step(ids, 'b', -1), ['b', 'a', 'c', 'd']);
  assert.deepEqual(step(ids, 'b', 1), ['a', 'c', 'b', 'd']);
  assert.deepEqual(step(ids, 'a', -1), ids);
  assert.deepEqual(step(ids, 'd', 1), ids);
});

test('drop places the dragged item before or after the target', () => {
  assert.deepEqual(drop(ids, 'd', 'b'), ['a', 'd', 'b', 'c']);
  assert.deepEqual(drop(ids, 'a', 'c', true), ['b', 'c', 'a', 'd']);
  assert.deepEqual(drop(ids, 'a', 'a'), ids);
  assert.deepEqual(drop(ids, 'a', 'zzz'), ids);
});

test('beforeOf names the next sibling (null when last) for the reorder API', () => {
  assert.equal(beforeOf(['b', 'a', 'c'], 'b'), 'a');
  assert.equal(beforeOf(['b', 'a', 'c'], 'c'), null);
});

test('changed detects a different order', () => {
  assert.equal(changed(ids, [...ids]), false);
  assert.equal(changed(ids, step(ids, 'b', 1)), true);
});
