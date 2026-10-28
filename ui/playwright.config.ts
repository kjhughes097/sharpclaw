import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end suite (3.3 spec section 11). By default it starts an isolated stack (gateway with the
 * fake provider and the built UI, plus one agent) via e2e/stack.sh; set E2E_BASE_URL (and
 * E2E_ADMIN_KEY, E2E_USER, E2E_PASSWORD) to run against a Compose stack instead.
 */
const baseURL = process.env.E2E_BASE_URL ?? `http://127.0.0.1:${process.env.E2E_CLIENT_PORT ?? 18480}`;

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, grep: /@core/ },
  ],
});
