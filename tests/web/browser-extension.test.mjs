import { test } from 'node:test';
import assert from 'node:assert/strict';
import { detectBrowser } from '../../src/TodoTracker.Server/wwwroot/js/plugins/browser-extension.js';

test('the setup guide starts with the browser it is open in', () => {
  assert.equal(detectBrowser('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0'), 'edge');
  assert.equal(detectBrowser('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'), 'chrome');
  assert.equal(detectBrowser('Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15'), 'safari');
});
