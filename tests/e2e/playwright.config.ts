import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.DEV_BASE_URL || 'http://127.0.0.1:5055';
const useExternalServer = Boolean(process.env.CI || process.env.DEV_BASE_URL);

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: [
    ['list'],
    ['html', { open: 'never', outputFolder: 'playwright-report' }]
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off'
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ],
  ...(useExternalServer
    ? {}
    : {
        webServer: {
          command:
            'ASPNETCORE_ENVIRONMENT=Development B2B_SEED_MOCK_DATA=true ASPNETCORE_URLS=http://127.0.0.1:5055 dotnet run --project ../../src/Gateway/B2B.GraphQL.Gateway/B2B.GraphQL.Gateway.csproj',
          url: `${baseURL}/health`,
          reuseExistingServer: true,
          timeout: 120_000
        }
      })
});
