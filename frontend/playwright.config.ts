import { defineConfig } from '@playwright/test'
import { resolve } from 'node:path'

export default defineConfig({
  testDir: './tests', workers: 1, fullyParallel: false, timeout: 30_000,
  use: { baseURL: 'https://127.0.0.1:5009', browserName: 'chromium', ignoreHTTPSErrors: true, trace: 'off', screenshot: 'off', video: 'off' },
  reporter: [['list']],
  globalTeardown: './tests/teardown.ts',
  webServer: [
    {
      command: 'dotnet test ../src/Tests/Quaestura.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~BrowserHostFixture',
      env: { QUAESTURA_BROWSER_WEBROOT: resolve('dist') },
      url: 'https://127.0.0.1:5009/health/live', ignoreHTTPSErrors: true, timeout: 60_000, reuseExistingServer: false,
    },
    { command: 'npm run dev -- --host localhost', url: 'http://localhost:8091', reuseExistingServer: false },
  ],
})
