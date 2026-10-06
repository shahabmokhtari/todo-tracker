import { defineConfig } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';

const port = Number(process.env.E2E_PORT || 5399);
const token = 'e2e-token-0123456789abcdefghijklmnop';
const dataDir = path.join(os.tmpdir(), `tt-e2e-${Date.now()}`);
const configuration = process.env.E2E_CONFIGURATION || 'Debug';
// Ask AI talks to a scripted agent, never to a real Copilot/Claude that happens to be installed.
const fakeAgent = path.resolve(`../FakeAcpAgent/bin/${configuration}/net10.0/FakeAcpAgent.dll`);
const noAgents = path.join(os.tmpdir(), 'tt-e2e-no-agents');

export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    channel: process.env.E2E_CHANNEL || undefined,
    trace: 'retain-on-failure',
  },
  metadata: { token },
  webServer: {
    command: `dotnet run --project ../../src/TodoTracker.Web/TodoTracker.Web.csproj -c ${configuration} --no-launch-profile -- --port ${port} --data "${dataDir}"`,
    url: `http://127.0.0.1:${port}/health`,
    timeout: 180_000,
    reuseExistingServer: false,
    env: { TODOTRACKER_TOKEN: token, TODOTRACKER_TEST_AGENT: fakeAgent, TODOTRACKER_AGENT_PATH: noAgents },
    stdout: 'pipe',
  },
});

