import { defineConfig } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

/**
 * End-to-end tests against a running stack (dev: Vite on 5173 proxying the API; compose: the web gateway).
 * E2E_CHANNEL=msedge|chrome uses an installed browser instead of downloading one.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: 0,
  reporter: [['list']],
  outputDir: './e2e-results',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5173',
    channel: process.env.E2E_CHANNEL,
    locale: 'es-ES',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
});
