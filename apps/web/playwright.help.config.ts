import { defineConfig } from "@playwright/test";

// Synthetic API fixtures exercise the Angular help page, not the server's embedded guide,
// and the appearance choice in the shell.
export default defineConfig({
  testDir: "./tests",
  testMatch: ["help.spec.ts", "theme.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  use: {
    baseURL: "http://127.0.0.1:5187",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
  },
  webServer: {
    // No API proxy: every request is answered by the synthetic routes or fails.
    command:
      "node ../../node_modules/@angular/cli/bin/ng.js serve --host 127.0.0.1 --port 5187 --no-hmr",
    url: "http://127.0.0.1:5187",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
