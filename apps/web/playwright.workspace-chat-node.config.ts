import { defineConfig } from "@playwright/test";
// Pure transport/shape checks: no browser, display, network server or model.
export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "workspace-chat-types.spec.ts",
    "workspace-chat-unsent.spec.ts",
    "return-path.spec.ts",
  ],
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
});
