import { defineConfig } from "@playwright/test";
import reports from "../playwright.reports.config";

// Reuse the packaged owner/origin requirements without starting development servers.
export default defineConfig({
  ...reports,
  testDir: ".",
  testMatch: "mapping-viewer*.spec.ts",
  outputDir: "../../../artifacts/mapping-viewer/test-results",
});
