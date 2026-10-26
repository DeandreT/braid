import { defineConfig } from "@playwright/test";
const origin = process.env.WEFT_WORKSPACE_CHAT_ORIGIN;
if (!origin || !/^http:\/\/127\.0\.0\.1:[0-9]+$/.test(origin))
  throw new Error("An owned chat test origin is required.");
export default defineConfig({
  testDir: "./tests",
  testMatch: "workspace-real-chat.spec.ts",
  workers: 1,
  retries: 0,
  timeout: 180000,
  outputDir: process.env.WEFT_WORKSPACE_CHAT_OUTPUT,
  use: {
    baseURL: origin,
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "off",
  },
});
