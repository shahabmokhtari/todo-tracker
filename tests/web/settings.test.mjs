import { test } from 'node:test';
import assert from 'node:assert/strict';
import { settingsSection, SETTINGS_SECTIONS } from '../../src/OneThing.Server/wwwroot/js/views/settings.js';

test('each settings address lands on its own section (the Mac app opens them in its One Thing window)', () => {
  assert.equal(settingsSection('#/settings/connected'), 'connected');
  assert.equal(settingsSection('#/settings/sync'), 'sync');
  assert.equal(settingsSection('#/settings/hooks'), 'hooks');
  assert.equal(settingsSection('#/settings/plugins'), 'plugins');
  assert.equal(settingsSection('#/settings/focus'), 'focus');
  assert.equal(settingsSection('#/settings/labels'), 'labels');
  assert.equal(settingsSection('#/settings'), null);
  assert.equal(settingsSection('#/settings/nothing'), null);
  assert.equal(settingsSection('#/board'), null);
  for (const id of ['connected', 'sync', 'hooks', 'plugins', 'feedback', 'labels', 'energy', 'focus']) assert.ok(SETTINGS_SECTIONS.includes(id), id);
});
