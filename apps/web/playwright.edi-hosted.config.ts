import { defineConfig } from "@playwright/test";
import hosted from "./playwright.hosted-acceptance.config";

export default defineConfig({
  ...hosted,
  testMatch: "**/edi-hosted-acceptance.spec.ts",
  use: { ...hosted.use, actionTimeout: 30_000, navigationTimeout: 90_000 },
});
