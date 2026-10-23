import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["hosted-mutation.spec.ts", "hosted-read.spec.ts"],
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
});
