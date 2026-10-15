import { defineConfig } from "@playwright/test";

const baseURL = process.env.WEFT_REPORTS_BASE_URL;
if (!baseURL)
  throw new Error(
    "Saved-report browser checks require WEFT_REPORTS_BASE_URL and the private packaged-host fixture.",
  );
const origin = new URL(baseURL);
if (
  origin.protocol !== "http:" ||
  origin.hostname !== "127.0.0.1" ||
  !origin.port ||
  origin.origin !== baseURL
)
  throw new Error(
    "WEFT_REPORTS_BASE_URL must be the exact configured packaged-host loopback HTTP origin.",
  );
for (const name of [
  "WEFT_REPORTS_BROWSER_OPERATOR",
  "WEFT_REPORTS_BROWSER_OPERATOR_CONFIG",
  "WEFT_REPORTS_BROWSER_MANIFEST",
  "WEFT_CHROMIUM_EXECUTABLE",
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
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    video: "off",
    screenshot: "only-on-failure",
  },
});
