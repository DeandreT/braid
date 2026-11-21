import assert from "node:assert/strict";
import { launchFixture, openFixture, violate, origin } from "./fixture.mjs";
const browser = await launchFixture();
try {
  const page = await openFixture(browser);
  assert.equal(await violate(page), true);
  assert.equal(
    await page.evaluate(
      () =>
        new SecurityPolicyViolationEvent("securitypolicyviolation").isTrusted,
    ),
    false,
  );
  await page.goto(origin + "/replacement");
  assert.equal(await page.title(), "local CSP control");
  console.log(
    "Local enforced worker-src control passed (not a native receipt).",
  );
} finally {
  await browser.close();
}
