import { test } from "@playwright/test";
import { HttpsFixture } from "./fixture";
import { NetworkCaseOwner } from "./network-cases";
import { CleanupPair, CleanupFailure } from "./cleanup-pair";

test("owned HTTPS finite transport adversary", async () => {
  const fixture = new HttpsFixture(),
    owner = new NetworkCaseOwner(fixture),
    cleanup = new CleanupPair();
  let primaryFailed = false;
  try {
    await fixture.open();
    await owner.run();
  } catch {
    primaryFailed = true;
  }
  const result = await cleanup.run(
    primaryFailed,
    () => fixture.close(),
    () => owner.drain(),
  );
  if (result.primaryFailed || result.closeFailed || result.drainFailed)
    throw new CleanupFailure(result);
});
