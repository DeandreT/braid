import { defineConfig } from "@playwright/test";

const server = process.env.WEFT_EDIT_SHELL_DLL;
if (!server)
  throw new Error(
    "Publish the exact server with Angular assets and set WEFT_EDIT_SHELL_DLL.",
  );
export default defineConfig({
  testDir: "./tests",
  testMatch:
    process.env.WEFT_EDIT_SHELL_ALL === "1"
      ? [
          "**/shell.spec.ts",
          // The mapping viewer needs the packaged reports owner; playwright.reports.config.ts runs it against the packaged host.
          "**/browser-auth-controls.spec.ts",
          "**/revision-chat.spec.ts",
          "**/revision-preview.spec.ts",
          "**/revision-comparison.spec.ts",
          "**/mapping-editor.spec.ts",
        ]
      : ["**/mapping-editor.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  use: {
    baseURL: "http://127.0.0.1:4173",
    headless: false,
    launchOptions: {
      executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE,
      args: ["--ozone-platform=x11"],
    },
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  webServer: {
    command: `dotnet "${server}"`,
    url: "http://127.0.0.1:4173/api/v1/health",
    env: {
      WEFT_DEV_LISTEN_ADDR: "127.0.0.1:4173",
      DOTNET_ENVIRONMENT: "Development",
      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
    },
    reuseExistingServer: false,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15_000 },
    timeout: 120_000,
  },
});
