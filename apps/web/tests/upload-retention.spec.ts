import { test, expect, type Page } from "@playwright/test";
import {
  catalogApi,
  mappingPath,
  projectPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";

type Kind = "mapping" | "append";
const bytes = Buffer.from(' {"order":9007199254740993,"code":"001"}\r\n');
const submit = (page: Page, kind: Kind) =>
  page.getByRole("button", {
    name: kind === "mapping" ? "Upload mapping" : "Append revision",
    exact: true,
  });
const filename = (page: Page) =>
  page.getByLabel("Saved filename", { exact: true });

async function fixture(page: Page, kind: Kind, heldCapture = false) {
  const data = structuredClone(synthetic);
  data.context.features.createMappings = true;
  data.context.features.uploadRevisions = true;
  data.project.access.role = "publisher";
  data.mapping.access.role = "publisher";
  data.project.access.actions.createMappings = true;
  data.mapping.access.actions.uploadRevisions = true;
  let actor = data.context.actorPrincipalId;
  let identityContext = `v1.${"A".repeat(43)}`;
  const writes: {
    url: string;
    bytes: Buffer | null;
    key: string | undefined;
  }[] = [];
  await syntheticCatalog(page, {
    [catalogApi(projectPath(data.project.scope))]: data.project,
    [catalogApi(mappingPath(data.mapping.scope))]: data.mapping,
  });
  await page.route("**/api/auth/session*", (route) =>
    route.fulfill({
      json: {
        version: 1,
        mode: "session",
        state: "authenticated",
        identityContext,
        authActionRequestToken: "YXV0aC1h",
        requestToken: "ZXBvY2gtYQ",
      },
    }),
  );
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...data.context,
        mode: "session",
        identityContext,
        requestToken: "Y2F0YWxvZy10b2tlbg",
        actorPrincipalId: actor,
      },
    }),
  );
  await page.route("**/api/v1/**", (route) => {
    if (route.request().method() === "GET") return route.fallback();
    writes.push({
      url: route.request().url(),
      bytes: route.request().postDataBuffer(),
      key: route.request().headers()["idempotency-key"],
    });
    return route.fulfill({ status: 503, json: { code: "unavailable" } });
  });
  if (heldCapture) {
    await page.addInitScript(() => {
      const original = Blob.prototype.arrayBuffer;
      let once = false;
      const control = window as unknown as {
        releaseCapture?: () => void;
        captureHeld?: boolean;
      };
      Blob.prototype.arrayBuffer = function () {
        if (this instanceof File && !once) {
          once = true;
          control.captureHeld = true;
          return new Promise<ArrayBuffer>((resolve, reject) => {
            control.releaseCapture = () =>
              original.call(this).then(resolve, reject);
          });
        }
        return original.call(this);
      };
    });
  }
  await page.goto(
    kind === "mapping"
      ? projectPath(data.project.scope) + "/mappings/new"
      : mappingPath(data.mapping.scope) + "/revisions/new",
  );
  await expect(
    page.getByLabel("Native mapping file", { exact: true }),
  ).toBeEnabled();
  if (kind === "mapping") {
    await page.getByLabel("Name", { exact: true }).fill("Unsent order");
    await page
      .locator("#upload-description")
      .fill("  keep this description\nexactly  ");
  }
  await page.getByLabel("Native mapping file", { exact: true }).setInputFiles({
    name: "chosen.json",
    mimeType: "application/json",
    buffer: bytes,
  });
  if (heldCapture) {
    await expect
      .poll(() =>
        page.evaluate(
          () => (window as unknown as { captureHeld?: boolean }).captureHeld,
        ),
      )
      .toBe(true);
  } else {
    await expect(submit(page, kind)).toBeEnabled();
    await filename(page).fill("saved-exact.json");
  }
  return {
    writes,
    otherActor: () => {
      actor = "10000000-0000-4000-8000-000000000099";
    },
    otherIdentity: () => {
      identityContext = `v1.${"B".repeat(42)}A`;
    },
    changeHead: () => {
      data.mapping.head = {
        ...data.mapping.head,
        sequence: "2",
        scope: {
          ...data.mapping.head.scope,
          revisionId: "40000000-0000-4000-8000-000000000002",
        },
      };
    },
  };
}

async function failCheck(
  page: Page,
  failure: "unavailable" | "timeout",
  duringCapture = false,
) {
  await page.route(
    "**/api/auth/session*",
    (route) =>
      failure === "timeout"
        ? route.abort("timedout")
        : route.fulfill({ status: 503, json: { code: "unavailable" } }),
    { times: 1 },
  );
  if (duringCapture)
    await page
      .getByRole("button", { name: "Check session", exact: true })
      .click();
  else await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(
    page.getByRole("heading", {
      name: "Workspace access unavailable",
      exact: true,
    }),
  ).toBeVisible();
  await expect(filename(page)).toBeHidden();
}

async function recover(page: Page, unlocked = true) {
  await page
    .getByRole("heading", { name: "Workspace access unavailable", exact: true })
    .locator("..")
    .getByRole("button", { name: "Retry", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  if (unlocked)
    await expect(
      page.getByLabel("Native mapping file", { exact: true }),
    ).toBeEnabled();
}

async function expectDraft(
  page: Page,
  kind: Kind,
  savedFilename = "saved-exact.json",
) {
  await expect(filename(page)).toHaveValue(savedFilename);
  await expect(page.getByText("chosen.json", { exact: true })).toBeVisible();
  if (kind === "mapping") {
    await expect(page.getByLabel("Name", { exact: true })).toHaveValue(
      "Unsent order",
    );
    await expect(page.locator("#upload-description")).toHaveValue(
      "  keep this description\nexactly  ",
    );
  }
  const persisted = await page.evaluate(() =>
    [...Object.values(localStorage), ...Object.values(sessionStorage)].join(
      "\n",
    ),
  );
  expect(persisted).not.toContain("Unsent order");
  expect(persisted).not.toContain("chosen.json");
  expect(persisted).not.toContain("9007199254740993");
}

for (const kind of ["mapping", "append"] as const) {
  for (const failure of ["unavailable", "timeout"] as const) {
    test(`${kind}: an unsent exact file survives a ${failure} session check`, async ({
      page,
    }) => {
      const state = await fixture(page, kind);
      await failCheck(page, failure);
      await recover(page);
      await expectDraft(page, kind);
      expect(state.writes).toEqual([]);
      await submit(page, kind).click();
      await expect.poll(() => state.writes.length).toBe(1);
      expect(state.writes[0].bytes).toEqual(bytes);
      expect(
        new URL(state.writes[0].url).searchParams.get("originalFilename"),
      ).toBe("saved-exact.json");
    });
  }

  test(`${kind}: another confirmed owner cannot recover an unsent file`, async ({
    page,
  }) => {
    const state = await fixture(page, kind);
    await failCheck(page, "unavailable");
    state.otherActor();
    await recover(page);
    await expect(filename(page)).toHaveValue("");
    await expect(page.getByText("chosen.json", { exact: true })).toHaveCount(0);
    await expect(submit(page, kind)).toBeDisabled();
    expect(state.writes).toEqual([]);
  });

  test(`${kind}: another confirmed session identity cannot recover an unsent file`, async ({
    page,
  }) => {
    const state = await fixture(page, kind);
    await failCheck(page, "unavailable");
    state.otherIdentity();
    await recover(page);
    await expect(filename(page)).toHaveValue("");
    await expect(page.getByText("chosen.json", { exact: true })).toHaveCount(0);
    expect(state.writes).toEqual([]);
  });

  test(`${kind}: a capture interrupted by a failed check resumes only after admission`, async ({
    page,
  }) => {
    const state = await fixture(page, kind, true);
    await failCheck(page, "unavailable", true);
    await recover(page);
    await page.evaluate(() =>
      (window as unknown as { releaseCapture?: () => void }).releaseCapture?.(),
    );
    await expect(submit(page, kind)).toBeEnabled();
    await expectDraft(page, kind, "chosen.json");
    expect(state.writes).toEqual([]);
    await submit(page, kind).click();
    await expect.poll(() => state.writes.length).toBe(1);
    expect(state.writes[0].bytes).toEqual(bytes);
  });

  test(`${kind}: repeated unavailable checks do not replace the retained draft with an empty form`, async ({
    page,
  }) => {
    const state = await fixture(page, kind);
    await failCheck(page, "unavailable");
    await page.route(
      "**/api/auth/session*",
      (route) => route.fulfill({ status: 503, json: { code: "unavailable" } }),
      { times: 1 },
    );
    await page
      .getByRole("button", { name: "Check session", exact: true })
      .click();
    await expect(
      page.getByRole("heading", {
        name: "Workspace access unavailable",
        exact: true,
      }),
    ).toBeVisible();
    await recover(page);
    await expectDraft(page, kind);
    expect(state.writes).toEqual([]);
  });

  test(`${kind}: leaving the page discards its unsent file even when the route is revisited`, async ({
    page,
  }) => {
    const state = await fixture(page, kind);
    const path = new URL(page.url()).pathname;
    await page
      .getByRole("link", {
        name: kind === "mapping" ? "Back to project" : "Back to mapping",
        exact: true,
      })
      .click();
    await page
      .getByRole("button", { name: "Discard draft and leave", exact: true })
      .click();
    await expect(page).not.toHaveURL(new RegExp(path + "$"));
    await page.goBack();
    await expect(
      page.getByLabel("Native mapping file", { exact: true }),
    ).toBeEnabled();
    await expect(filename(page)).toHaveValue("");
    await expect(page.getByText("chosen.json", { exact: true })).toHaveCount(0);
    expect(state.writes).toEqual([]);
  });

  test(`${kind}: a sent request still recovers its original bytes and identity`, async ({
    page,
  }) => {
    const state = await fixture(page, kind);
    await submit(page, kind).click();
    await expect.poll(() => state.writes.length).toBe(1);
    const resend = page.getByRole("button", {
      name:
        kind === "mapping"
          ? "Resend original upload"
          : "Resend original append",
      exact: true,
    });
    await expect(resend).toBeEnabled();
    await failCheck(page, "unavailable");
    await recover(page, false);
    await resend.click();
    await expect.poll(() => state.writes.length).toBe(2);
    expect(state.writes[1]).toEqual(state.writes[0]);
    expect(state.writes[0].key).toMatch(/^[0-9a-f-]{36}$/);
  });
}

test("append: a changed head after recovery still requires explicit review", async ({
  page,
}) => {
  const state = await fixture(page, "append");
  await failCheck(page, "unavailable");
  state.changeHead();
  await recover(page);
  await expectDraft(page, "append");
  await expect(submit(page, "append")).toBeDisabled();
  expect(state.writes).toEqual([]);
  await page
    .getByRole("button", { name: "Review current head", exact: true })
    .click();
  await expect(submit(page, "append")).toBeEnabled();
  await submit(page, "append").click();
  await expect.poll(() => state.writes.length).toBe(1);
  expect(state.writes[0].bytes).toEqual(bytes);
  expect(
    new URL(state.writes[0].url).searchParams.get("expectedHeadRevisionId"),
  ).toBe("40000000-0000-4000-8000-000000000002");
});
