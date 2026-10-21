import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/build-values.spec.ts"],
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
});
