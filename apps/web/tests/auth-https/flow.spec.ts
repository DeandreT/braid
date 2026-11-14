import { test } from "@playwright/test";
import { HttpsFixture, until } from "./fixture";
import { requireOwned, safeFailure } from "./wire";

test("actual owned HTTPS authentication qualification", async () => {
  // Manual contexts deliberately avoid runner-managed artifacts/context snapshots.
  const fixture = new HttpsFixture();
  let primary = false,
    cleanup = false;
  try {
    await fixture.open();
    await fixture.login(false);
    if (["state", "correlation", "nonce"].includes(fixture.cfg.scenario))
      await fixture.negativeCallback();
    else {
      await until(() => fixture.callbackSuccess === 1);
      await fixture.authenticated();
      const initial = await fixture.counts();
      requireOwned(
        initial.sessionRows === 1 &&
          initial.activeRows === 1 &&
          initial.tokenRequests === 1 &&
          initial.assertionsAccepted === 1 &&
          initial.pkceAccepted === 1 &&
          initial.unexpected === 0,
      );
      if (fixture.cfg.scenario === "replay") await fixture.replay();
      else if (fixture.cfg.scenario === "tokens")
        await fixture.tokenNegatives();
      else {
        await fixture.catalog();
        await fixture.login(true);
        await until(() => fixture.callbackSuccess === 2);
        await fixture.authenticated();
        const again = await fixture.counts();
        requireOwned(
          again.sessionRows === 2 &&
            again.epochChanged &&
            fixture.dualHeaders === 1 &&
            fixture.native === 2,
        );
        await fixture.restartAndRefresh();
        await fixture.logout();
      }
    }
  } catch {
    fixture.diagnostics.bodyFailed();
    primary = true;
  }
  try {
    await fixture.close();
  } catch {
    cleanup = true;
  }
  fixture.diagnostics.write();
  if (primary || cleanup) throw safeFailure();
});
