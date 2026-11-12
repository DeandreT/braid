import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "workbench-model.spec.ts",
    "workbench-documents.spec.ts",
    "csv-document-view.spec.ts",
  ],
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
});
