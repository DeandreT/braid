import { defineConfig } from "@playwright/test";

for (const name of [
  "BRAID_PREVIEW_BROWSER_MANIFEST",
  "BRAID_PREVIEW_BROWSER_BASE_URL",
  "BRAID_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Actual editor browser checks require ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/mapping-editor-live.spec.ts"],
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 1_500_000,
  expect: { timeout: 180_000 },
  forbidOnly: true,
  use: {
    baseURL: process.env.BRAID_PREVIEW_BROWSER_BASE_URL,
    headless: false,
    launchOptions: {
      executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE,
      args: ["--ozone-platform=x11"],
    },
    trace: "off",
    screenshot: "only-on-failure",
    video: "off",
  },
});
