import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5174",
    headless: false,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: [
    {
      command:
        "dotnet run --no-build --configuration Release --project ../../dotnet/apps/Weft.Server",
      url: "http://127.0.0.1:4101/api/v1/health",
      env: { WEFT_DEV_LISTEN_ADDR: "127.0.0.1:4101" },
      reuseExistingServer: false,
      timeout: 120000,
    },
    {
      command: "npm run dev -- --port 5174 --proxy-config proxy.e2e.conf.json",
      url: "http://127.0.0.1:5174",
      reuseExistingServer: false,
    },
  ],
});
