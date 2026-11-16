import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/builds-live.spec.ts"],
  workers: 1,
  retries: 0,
  timeout: 900000,
  expect: { timeout: 15000 },
  forbidOnly: Boolean(process.env.CI),
  use: {
    baseURL: process.env.BRAID_BUILD_BASE_URL || "http://127.0.0.1:4114",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
