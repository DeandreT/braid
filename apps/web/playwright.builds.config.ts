import { defineConfig } from "@playwright/test";
import base from "./playwright.config";

export default defineConfig({
  ...base,
  testMatch: ["**/builds.spec.ts"],
  use: { ...base.use, headless: true },
  webServer: [
    {
      command:
        process.env.BRAID_BUILD_SERVER_COMMAND ||
        "dotnet ../../artifacts/windows/bin/Braid.Server/release/Braid.Server.dll",
      url: "http://127.0.0.1:4101/api/v1/health",
      env: {
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
        BRAID_DEV_LISTEN_ADDR: "127.0.0.1:4101",
        DOTNET_ENVIRONMENT: "Development",
      },
      reuseExistingServer: false,
      gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
      timeout: 120000,
    },
    {
      command:
        "bun ../../node_modules/@angular/cli/bin/ng.js serve --host 127.0.0.1 --port 5174 --proxy-config proxy.e2e.conf.json --no-hmr",
      url: "http://127.0.0.1:5174",
      reuseExistingServer: false,
      gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
      timeout: 120000,
    },
  ],
});
