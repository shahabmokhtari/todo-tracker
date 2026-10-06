import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = (path) => JSON.parse(readFileSync(new URL(path, import.meta.url), 'utf8'));
const chromium = read('../manifest.json');
const safari = read('../safari/manifest.json');

test('the Safari build is the same extension (name, version, access) without the side panel', () => {
  for (const key of ['name', 'short_name', 'version', 'description', 'host_permissions', 'icons', 'content_security_policy', 'background']) {
    assert.deepEqual(safari[key], chromium[key], key);
  }
  assert.deepEqual(safari.permissions, chromium.permissions.filter((p) => p !== 'sidePanel'));
  assert.equal(safari.side_panel, undefined);
  // Safari has no side panel: the same page opens from the toolbar button.
  assert.equal(safari.action.default_popup, chromium.side_panel.default_path);
});
