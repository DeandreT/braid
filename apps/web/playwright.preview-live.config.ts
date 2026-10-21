import { defineConfig } from "@playwright/test";

// Run through tools/preview-browser/run.mjs, which owns the actual API, worker and database.
for (const name of [
  "WEFT_PREVIEW_BROWSER_MANIFEST",
  "WEFT_PREVIEW_BROWSER_BASE_URL",
  "WEFT_PREVIEW_BROWSER_CONTROL",
  "WEFT_PREVIEW_BROWSER_CONTROL_TOKEN",
  "WEFT_CHROMIUM_EXECUTABLE",
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
    baseURL: process.env.WEFT_PREVIEW_BROWSER_BASE_URL,
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
