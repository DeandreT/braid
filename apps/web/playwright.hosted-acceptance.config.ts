import { defineConfig } from "@playwright/test";

// Run through deploy/compose/acceptance-test.sh, which owns the packaged stack, the issuer,
// the ingress and every credential. Nothing here scripts an API reply.
for (const name of [
  "WEFT_ACCEPTANCE_BASE_URL",
  "WEFT_ACCEPTANCE_HOST_RULES",
  "WEFT_ACCEPTANCE_SPKI",
  "WEFT_ACCEPTANCE_WORK",
  "WEFT_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Hosted acceptance requires ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/hosted-acceptance.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 600_000,
  expect: { timeout: 30_000 },
  forbidOnly: true,
  outputDir: process.env.WEFT_ACCEPTANCE_WORK + "/playwright",
  reporter: [["list"]],
  use: {
    baseURL: process.env.WEFT_ACCEPTANCE_BASE_URL,
    headless: true,
    launchOptions: {
      executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE,
      // Only the public host and the fixed Microsoft name resolve, each to this run's
      // listener; the browser admits only this run's two TLS keys beyond its own trust.
      args: [
        "--host-resolver-rules=" + process.env.WEFT_ACCEPTANCE_HOST_RULES,
        "--ignore-certificate-errors-spki-list=" +
          process.env.WEFT_ACCEPTANCE_SPKI,
        "--dns-prefetch-disable",
      ],
    },
    ignoreHTTPSErrors: false,
    serviceWorkers: "block",
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
