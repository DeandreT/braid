import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  testMatch: "creation-library.spec.ts",
  workers: 1,
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5194",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
  },
  webServer: {
    command: "bun run dev --port 5194 --no-hmr",
    url: "http://127.0.0.1:5194",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
