import { test } from 'node:test';
import assert from 'node:assert/strict';
import { presets, groupAgents, ago, privacyNote } from '../../src/TodoTracker.Server/wwwroot/js/plugins/agent-chat.js';

test('the picker shows installed agents, then API models', () => {
  const groups = groupAgents([
    { id: 'copilot', name: 'GitHub Copilot', installed: true, kind: 'agent' },
    { id: 'claude', name: 'Claude Code', installed: false, kind: 'agent' },
    { id: 'api:a1', name: 'GPT', installed: true, kind: 'api' },
  ]);

  assert.deepEqual(groups.agents.map((a) => a.id), ['copilot']);
  assert.deepEqual(groups.models.map((a) => a.id), ['api:a1']);
});

test('presets: services that leave the computer use https and need a key; local ones need neither', () => {
  for (const p of presets.filter((x) => x.baseUrl)) {
    const url = new URL(p.baseUrl);
    const local = ['127.0.0.1', 'localhost'].includes(url.hostname);
    assert.equal(url.protocol, local ? 'http:' : 'https:', p.id);
    assert.equal(p.key, !local, p.id);
  }
  // Every kind the server knows has a preset.
  assert.deepEqual(presets.map((p) => p.id).sort(), ['anthropic', 'anthropic-compatible', 'azure', 'lmstudio', 'ollama', 'openai', 'openai-compatible', 'openrouter']);
});

test('chat times read naturally', () => {
  const now = new Date('2026-10-07T12:00:00Z');
  assert.equal(ago('2026-10-07T11:59:40Z', now), 'just now');
  assert.equal(ago('2026-10-07T11:55:00Z', now), '5 min ago');
  assert.equal(ago('2026-10-07T09:00:00Z', now), '3 h ago');
  assert.equal(ago('2026-10-06T10:00:00Z', now), 'yesterday');
});

test('a model that leaves the computer says where messages go; agents and local models say nothing', () => {
  assert.match(privacyNote({ kind: 'api', local: false, name: 'GPT' }, 'https://api.openai.com/v1'), /sent to api\.openai\.com/);
  assert.equal(privacyNote({ kind: 'api', local: true, name: 'Llama' }, 'http://127.0.0.1:11434/v1'), null);
  assert.equal(privacyNote({ kind: 'agent', local: true, name: 'Copilot' }), null);
  assert.match(privacyNote({ kind: 'api', local: false, name: 'X' }, undefined), /the model’s service/);
});
