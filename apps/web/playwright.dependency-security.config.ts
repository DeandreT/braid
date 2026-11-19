import { defineConfig } from "@playwright/test";

// Synthetic HTTP replies qualify the real Angular forms, not backend authority
// or persistence. The fallback proxy deliberately has no application listener.
export default defineConfig({
  testDir: "./tests",
  testMatch: ["dependency-security-forms.spec.ts", "administration.spec.ts"],
  outputDir: "./test-results/dependency-security",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  use: {
    baseURL: "http://127.0.0.1:5237",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "only-on-failure",
  },
  webServer: {
    command:
      "bun ../../node_modules/@angular/cli/bin/ng.js serve --host 127.0.0.1 --port 5237 --proxy-config proxy.synthetic.conf.json --no-hmr --live-reload=false",
    url: "http://127.0.0.1:5237",
    reuseExistingServer: false,
    timeout: 120_000,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15_000 },
  },
});
