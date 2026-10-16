import { defineConfig } from "@playwright/test";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";

const baseURL = process.env.WEFT_NATIVE_REVISION_APPEND_BASE_URL;
if (!baseURL)
  throw new Error(
    "Native revision append browser checks require an explicit packaged origin and private fixture.",
  );
const origin = new URL(baseURL);
if (
  origin.protocol !== "http:" ||
  origin.hostname !== "127.0.0.1" ||
  !origin.port ||
  origin.origin !== baseURL
)
  throw new Error(
    "WEFT_NATIVE_REVISION_APPEND_BASE_URL must be the exact configured packaged loopback HTTP origin.",
  );
for (const name of [
  "WEFT_NATIVE_REVISION_APPEND_BROWSER_OPERATOR",
  "WEFT_NATIVE_REVISION_APPEND_BROWSER_OPERATOR_CONFIG",
  "WEFT_NATIVE_REVISION_APPEND_BROWSER_MANIFEST",
  "WEFT_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Native revision append browser checks require ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "native-revision-append-boundary.spec.ts",
    "native-revision-append-persistence.spec.ts",
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
    screenshot: "off",
  },
});
