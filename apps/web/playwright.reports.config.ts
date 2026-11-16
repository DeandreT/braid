import { defineConfig } from "@playwright/test";

const baseURL = process.env.BRAID_REPORTS_BASE_URL;
if (!baseURL)
  throw new Error(
    "Saved-report browser checks require BRAID_REPORTS_BASE_URL and the private packaged-host fixture.",
  );
const origin = new URL(baseURL);
if (
  origin.protocol !== "http:" ||
  origin.hostname !== "127.0.0.1" ||
  !origin.port ||
  origin.origin !== baseURL
)
  throw new Error(
    "BRAID_REPORTS_BASE_URL must be the exact configured packaged-host loopback HTTP origin.",
  );
for (const name of [
  "BRAID_REPORTS_BROWSER_OPERATOR",
  "BRAID_REPORTS_BROWSER_OPERATOR_CONFIG",
  "BRAID_REPORTS_BROWSER_MANIFEST",
  "BRAID_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Saved-report browser checks require ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "revision-reports-boundary.spec.ts",
    "revision-reports-persistence.spec.ts",
  ],
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,
  timeout: 300_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    headless: false,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    video: "off",
    screenshot: "only-on-failure",
  },
});
