import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "browser-auth-controls.spec.ts",
    "upload-presentation.spec.ts",
    "upload-retention.spec.ts",
    "workbench.spec.ts",
    "workbench-model.spec.ts",
    "hosted-mutation.spec.ts",
    "hosted-read.spec.ts",
    "auth-https-summary.spec.ts",
    "regression.spec.ts",
    "builds.spec.ts",
    "revision-preview.spec.ts",
    "revision-chat.spec.ts",
    "revision-comparison.spec.ts",
  ],
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5181",
    headless: true,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command:
      "npm run dev -- --port 5181 --proxy-config proxy.synthetic.conf.json --no-hmr" +
      (process.platform === "win32" ? " --poll 2000" : ""),
    url: "http://127.0.0.1:5181",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
