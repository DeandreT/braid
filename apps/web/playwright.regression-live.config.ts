import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/regression-live.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120000,
  forbidOnly: true,
  use: {
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
