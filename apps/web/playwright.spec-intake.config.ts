import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  testMatch: ["spec-intake.spec.ts", "spec-evidence.spec.ts"],
  workers: 1,
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5186",
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    screenshot: "only-on-failure",
  },
  webServer: {
    command: "npm run dev -- --port 5186 --no-hmr",
    url: "http://127.0.0.1:5186",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
