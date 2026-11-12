import { defineConfig } from "@playwright/test";

// Native Windows qualification uses an offscreen browser, never an active monitor.
// Synthetic API fixtures exercise the Angular boundary, not the provider/backend.
export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "revision-chat.spec.ts",
    "workspace-chat.spec.ts",
    "workspace-chat-resources.spec.ts",
    "chat-presentation.spec.ts",
    "chat-mapping-writes.spec.ts",
  ],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  use: {
    baseURL: "http://127.0.0.1:5178",
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
  },
  webServer: {
    cwd: process.env.WEFT_CHAT_UI_SOURCE
      ? `${process.env.WEFT_CHAT_UI_SOURCE}/apps/web`
      : undefined,
    command:
      "npm run dev -- --port 5178 --proxy-config proxy.synthetic.conf.json --no-hmr",
    url: "http://127.0.0.1:5178",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
