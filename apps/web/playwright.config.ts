import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "**/shell.spec.ts",
    "**/mapping-viewer.spec.ts",
    "**/browser-auth-controls.spec.ts",
    "**/revision-preview.spec.ts",
  ],
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
      env: {
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
        WEFT_DEV_LISTEN_ADDR: "127.0.0.1:4101",
        DOTNET_ENVIRONMENT: "Development",
      },
      reuseExistingServer: false,
      gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
      timeout: 120000,
    },
    {
      command:
        "npm run dev -- --port 5174 --proxy-config proxy.e2e.conf.json --no-hmr",
      url: "http://127.0.0.1:5174",
      reuseExistingServer: false,
      gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
    },
  ],
});
