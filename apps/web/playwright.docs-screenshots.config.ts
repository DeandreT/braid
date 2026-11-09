import { defineConfig } from "@playwright/test";

// Captures the documentation screenshots in docs/images from demo API fixtures.
// These images illustrate the UI; they are not qualification evidence.
export default defineConfig({
  testDir: "./tests",
  testMatch: "docs-screenshots.spec.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  timeout: 60_000,
  // The build/server is registered with the pinned runner's process cleanup
  // before setup awaits readiness, including cancellation during the build.
  globalSetup: "../../tools/docs-images/session.mjs",
  globalTeardown: "../../tools/docs-images/teardown.mjs",
  use: {
    baseURL: "http://127.0.0.1:5188",
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    colorScheme: "light",
    reducedMotion: "reduce",
    locale: "en-US",
    timezoneId: "UTC",
    trace: "off",
    screenshot: "off",
  },
});
