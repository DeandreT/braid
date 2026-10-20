import { defineConfig } from "@playwright/test";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
if (
  !process.env.WEFT_BROWSER_SESSION_CONTROLLED_CONFIG ||
  !process.env.WEFT_CHROMIUM_EXECUTABLE
)
  throw new Error(
    "Browser session checks require the root-admitted private static configuration and pinned browser.",
  );
export default defineConfig({
  testDir: "./tests/browser-session",
  testMatch: ["boundary.spec.ts", "recovery.spec.ts", "navigation.spec.ts"],
  workers: 1,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  timeout: 90000,
  expect: { timeout: 10000 },
  use: {
    headless: false,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    video: "off",
    screenshot: "off",
    serviceWorkers: "block",
  },
});
