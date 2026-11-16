import { defineConfig } from "@playwright/test";

const baseURL = process.env.BRAID_CATALOG_BASE_URL;
if (!baseURL) {
  throw new Error(
    "Catalog browser qualification requires BRAID_CATALOG_BASE_URL and the private owner fixture; no development proxy is substituted.",
  );
}
const origin = new URL(baseURL);
if (
  origin.protocol !== "http:" ||
  origin.hostname !== "127.0.0.1" ||
  !origin.port ||
  origin.origin !== baseURL
) {
  throw new Error(
    "BRAID_CATALOG_BASE_URL must be the exact admitted packaged-host loopback HTTP origin.",
  );
}
for (const name of [
  "BRAID_CATALOG_BROWSER_OPERATOR",
  "BRAID_CATALOG_BROWSER_OPERATOR_CONFIG",
  "BRAID_CATALOG_BROWSER_MANIFEST",
  "BRAID_CHROMIUM_EXECUTABLE",
]) {
  if (!process.env[name])
    throw new Error(`Catalog browser qualification requires ${name}.`);
}

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "catalog-navigation.spec.ts",
    "catalog-history.spec.ts",
    "catalog-boundary.spec.ts",
  ],
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,
  timeout: 240_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    headless: false,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    // Context responses contain framework tokens. Keep raw network bodies out of attachments.
    trace: "off",
    video: "off",
    screenshot: "only-on-failure",
  },
});
