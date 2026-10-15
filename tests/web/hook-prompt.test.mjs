import { test } from 'node:test';
import assert from 'node:assert/strict';
import { describeHook, nextToAsk } from '../../src/TodoTracker.Server/wwwroot/js/hook-prompt.js';

const waiting = { id: 'a1', version: 'v1', event: 'taskCompleted', state: 'needs approval', hook: { bash: 'echo hi' } };

test('the page asks the owner about a waiting hook once per version, unless a desktop app asks itself', () => {
  const asked = new Set();
  const status = { canApprove: true, appAsks: false, hooks: [{ ...waiting, id: 'on', state: 'on' }, waiting] };
  assert.equal(nextToAsk(status, asked), waiting);

  asked.add('a1\u001fv1');
  assert.equal(nextToAsk(status, asked), null);
  assert.equal(nextToAsk({ ...status, hooks: [{ ...waiting, version: 'v2' }] }, asked)?.version, 'v2');

  assert.equal(nextToAsk({ ...status, appAsks: true }, new Set()), null);
  assert.equal(nextToAsk({ ...status, canApprove: false }, new Set()), null);
});

test('describeHook shows everything a hook would do', () => {
  assert.equal(
    describeHook({ powershell: 'Write-Host hi', url: 'https://x.test', env: { A: '1' }, cwd: 'C:\\tmp', match: { tags: ['work'] } }),
    'PowerShell: Write-Host hi\nWeb address: https://x.test\nVariables: A=1\nFolder: C:\\tmp\nOnly for: tags work');
});
