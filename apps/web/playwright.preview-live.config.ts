import { defineConfig } from "@playwright/test";

// Run through tools/preview-browser/run.mjs, which owns the actual API, worker and database.
for (const name of [
  "BRAID_PREVIEW_BROWSER_MANIFEST",
  "BRAID_PREVIEW_BROWSER_BASE_URL",
  "BRAID_PREVIEW_BROWSER_CONTROL",
  "BRAID_PREVIEW_BROWSER_CONTROL_TOKEN",
  "BRAID_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Actual preview browser checks require ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/preview-live.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 240_000,
  expect: { timeout: 15_000 },
  forbidOnly: true,
  use: {
    baseURL: process.env.BRAID_PREVIEW_BROWSER_BASE_URL,
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
