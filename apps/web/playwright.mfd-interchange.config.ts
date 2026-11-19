import { defineConfig } from "@playwright/test";
import base from "./playwright.config";

export default defineConfig({
  ...base,
  testMatch: ["**/mfd-interchange.spec.ts"],
  use: { ...base.use, viewport: { width: 1280, height: 900 } },
  webServer: [
    {
      command:
        "dotnet run --no-build --configuration Release --project ../../dotnet/apps/Braid.Server",
      url: "http://127.0.0.1:4101/api/v1/health",
      env: {
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
        BRAID_DEV_LISTEN_ADDR: "127.0.0.1:4101",
        DOTNET_ENVIRONMENT: "Development",
      },
      reuseExistingServer: false,
      timeout: 120000,
    },
    {
      command:
        "bun run dev --port 5174 --proxy-config proxy.e2e.conf.json --no-hmr",
      url: "http://127.0.0.1:5174",
      reuseExistingServer: false,
      timeout: 120000,
    },
  ],
});
