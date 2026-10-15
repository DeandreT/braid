import { defineConfig } from "@playwright/test";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";

const baseURL = process.env.WEFT_NATIVE_MAPPING_UPLOAD_BASE_URL;
if (!baseURL)
  throw new Error(
    "Native mapping upload browser checks require an explicit packaged origin and private fixture.",
  );
const origin = new URL(baseURL);
if (
  origin.protocol !== "http:" ||
  origin.hostname !== "127.0.0.1" ||
  !origin.port ||
  origin.origin !== baseURL
)
  throw new Error(
    "WEFT_NATIVE_MAPPING_UPLOAD_BASE_URL must be the exact configured packaged loopback HTTP origin.",
  );
for (const name of [
  "WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR",
  "WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR_CONFIG",
  "WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_MANIFEST",
  "WEFT_CHROMIUM_EXECUTABLE",
])
  if (!process.env[name])
    throw new Error(`Native mapping upload browser checks require ${name}.`);

export default defineConfig({
  testDir: "./tests",
  testMatch: [
    "native-mapping-upload-boundary.spec.ts",
    "native-mapping-upload-persistence.spec.ts",
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
