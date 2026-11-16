import {
  test,
  expect,
  refused,
  browserPaths,
  editField,
  type ControlledSession,
} from "./fixture";
import { inputIds, type Feature } from "./data";

type Editor = Exclude<Feature, "creation">;
const leaveCopy =
  "Leave this workspace view? Private in-tab work and recovery information may be lost. A previously sent request may already have committed.";
const host: Record<Editor, string> = {
  metadata: "braid-edit-mapping-page",
  upload: "braid-upload-mapping-page",
  append: "braid-append-revision-page",
};
const submitName: Record<Editor, string> = {
  metadata: "Save metadata",
  upload: "Upload mapping",
  append: "Append revision",
};
const guarded = async (work: () => Promise<void>) => {
  try {
    await work();
  } catch {
    throw new Error("The controlled workspace navigation predicate failed.");
  }
};
const button = (app: ControlledSession, name: string) =>
  app.page.getByRole("button", { name, exact: true });
// Leaving an editor through the header goes to the catalog landing, outside the editor route.
const catalogLink = (app: ControlledSession) =>
  app.page
    .getByRole("navigation", { name: "Workspace", exact: true })
    .getByRole("link", { name: "Catalog", exact: true });
const refreshName = (feature: Editor) =>
  feature === "metadata" ? "Refresh admission" : "Refresh access";
const unknownName = (feature: "upload" | "append") =>
  feature === "upload"
    ? "Upload outcome is unknown"
    : "Append outcome is unknown";
const recoverName = (feature: "upload" | "append") =>
  feature === "upload" ? "Resend original upload" : "Resend original append";

async function prepare(app: ControlledSession, feature: Editor): Promise<void> {
  // The ordinary application entry and its real router, not the probe entry.
  await app.open(browserPaths[feature]);
  await expect(app.page.locator("#" + inputIds[feature])).toBeVisible();
  app.draft("Owned private draft", "Owned private description");
  if (feature === "append") {
    const review = app.hold("status", () => ({ json: app.status() }));
    await button(app, "Review current head").click();
    await review.entered;
    await expect(app.page.locator("#append-file")).toBeDisabled();
    review.release();
    await review.finished;
    app.plans.delete("status");
    await expect(app.page.locator("#append-file")).toBeEnabled();
  } else {
    await editField(app.page, "#" + inputIds[feature], "Owned private draft");
    await editField(
      app.page,
      feature === "metadata" ? "#metadata-description" : "#upload-description",
      "Owned private description",
    );
  }
  if (feature !== "metadata") {
    const fileInput = app.page.locator(
      feature === "upload" ? "#upload-file" : "#append-file",
    );
    await expect(fileInput).toBeEnabled();
    await fileInput.setInputFiles(app.file());
    await editField(
      app.page,
      feature === "upload" ? "#upload-filename" : "#append-filename",
      "owned +%.native.json",
    );
  }
  await expect(button(app, submitName[feature])).toBeEnabled();
  if (feature !== "metadata") {
    app.plans.set(feature, { abort: true });
    await button(app, submitName[feature]).click();
    await expect(
      app.page.getByRole("heading", {
        name: unknownName(feature),
        exact: true,
      }),
    ).toBeVisible();
    expect(app.wires.length).toBe(1);
    expect(
      app.wires.every(
        (wire) =>
          wire.kind === feature &&
          wire.exactBody &&
          wire.exactQuery &&
          wire.sameOriginalBody &&
          wire.sameOriginalKey &&
          wire.sameOriginalQuery &&
          wire.freshCatalogToken &&
          wire.distinctAudiences &&
          wire.correctHeaders,
      ),
    ).toBe(true);
  }
}

async function hiddenWorkspace(
  app: ControlledSession,
  feature: Editor,
): Promise<void> {
  const previous = app.count("status");
  if (feature === "append") app.session = "anonymous";
  else app.plans.set("status", refused(503));
  await button(app, refreshName(feature)).click();
  await expect.poll(() => app.count("status")).toBeGreaterThan(previous);
  await expect(
    app.page.getByRole("heading", {
      name: "Workspace access unavailable",
      exact: true,
    }),
  ).toBeVisible();
  await assertHidden(app, feature);
}

async function assertHidden(
  app: ControlledSession,
  feature: Editor,
): Promise<void> {
  expect(
    await app.page.locator(host[feature]).evaluate((node) => {
      const boundary = node.closest("[hidden]");
      return (
        node.isConnected &&
        node.getClientRects().length === 0 &&
        boundary !== null &&
        boundary.hasAttribute("inert") &&
        boundary.getAttribute("aria-hidden") === "true"
      );
    }),
  ).toBe(true);
  expect(await app.privateMarkersHidden(feature)).toBe(true);
  await expect(catalogLink(app)).toBeVisible();
}

async function retainedWork(
  app: ControlledSession,
  feature: Editor,
): Promise<void> {
  if (feature === "metadata") {
    await expect(app.page.locator("#metadata-name")).toBeVisible();
    expect(
      await app.page
        .locator("#metadata-name")
        .evaluate(
          (node) => (node as HTMLInputElement).value === "Owned private draft",
        ),
    ).toBe(true);
    expect(
      await app.page
        .locator("#metadata-description")
        .evaluate(
          (node) =>
            (node as HTMLTextAreaElement).value === "Owned private description",
        ),
    ).toBe(true);
  } else {
    await expect(
      app.page.getByRole("heading", {
        name: unknownName(feature),
        exact: true,
      }),
    ).toBeVisible();
    await expect(button(app, recoverName(feature))).toBeEnabled();
  }
}

function noAutomaticWrites(app: ControlledSession, count: number): void {
  expect(app.wires.length).toBe(count);
  expect(app.count("creation")).toBe(0);
  expect(app.count("patch")).toBe(0);
  expect(app.count("resume")).toBe(0);
  expect(app.count("receipt")).toBe(0);
  expect(app.authPosts).toBe(0);
  expect(app.unexpected).toBe(0);
  expect(app.routeFailures).toBe(0);
}

for (const feature of ["metadata", "upload", "append"] as const)
  test(`hidden ${feature} navigation uses a private-safe browser decision and preserves ordinary cleanup`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      const originalUrl = page.url();
      const mounted = await page.locator(host[feature]).elementHandle();
      expect(mounted !== null).toBe(true);
      const writes = app.wires.length;
      const decisions: boolean[] = [];
      let accept = false;
      let dialogFailure = false;
      page.on("dialog", (dialog) => {
        decisions.push(
          dialog.type() === "confirm" && dialog.message() === leaveCopy,
        );
        void (accept ? dialog.accept() : dialog.dismiss()).catch(() => {
          dialogFailure = true;
        });
      });

      await hiddenWorkspace(app, feature);
      await catalogLink(app).click();
      expect(decisions.length === 1 && decisions[0]).toBe(true);
      expect(page.url() === originalUrl).toBe(true);
      expect(await mounted!.evaluate((node) => node.isConnected)).toBe(true);
      await assertHidden(app, feature);
      noAutomaticWrites(app, writes);

      // A dismissed navigation did not destroy the original private work. Re-admit
      // the same owner through the visible shell and then the child resource UI.
      app.plans.delete("status");
      app.session = "authenticated";
      app.epoch();
      const contexts = app.count("context");
      await button(app, "Retry").click();
      await expect.poll(() => app.count("context")).toBeGreaterThan(contexts);
      await expect(button(app, refreshName(feature))).toBeEnabled();
      await button(app, refreshName(feature)).click();
      await retainedWork(app, feature);
      expect(await mounted!.evaluate((node) => node.isConnected)).toBe(true);
      noAutomaticWrites(app, writes);

      await hiddenWorkspace(app, feature);
      accept = true;
      await catalogLink(app).click();
      await expect.poll(() => page.title()).toBe("Catalog · braid");
      expect(new URL(page.url()).pathname === "/catalog").toBe(true);
      expect(decisions.length === 2 && decisions.every(Boolean)).toBe(true);
      expect(await mounted!.evaluate((node) => node.isConnected)).toBe(false);
      expect(await app.privateMarkersHidden(feature)).toBe(true);
      // Include the existing coalesced focus window in the negative-write oracle.
      await page.waitForTimeout(300);
      noAutomaticWrites(app, writes);

      // Browser history remounts the real route without reloading the tab. Only
      // the existing upload/append recovery slot may survive route destruction.
      app.plans.delete("status");
      app.session = "authenticated";
      await page.goBack();
      await expect.poll(() => page.url() === originalUrl).toBe(true);
      if (feature === "metadata") {
        await expect(page.locator("#metadata-name")).toBeVisible();
        expect(
          await page
            .locator("#metadata-name")
            .evaluate(
              (node, baseline) => (node as HTMLInputElement).value === baseline,
              app.mapping.name,
            ),
        ).toBe(true);
        expect(await page.locator("#metadata-unknown").count()).toBe(0);
      } else {
        if (feature === "append") {
          // Anonymous -> authenticated invalidates the remount's first admission.
          // Recover only after a deliberate fresh child-resource admission.
          await expect(button(app, refreshName(feature))).toBeEnabled();
          await button(app, refreshName(feature)).click();
        }
        await retainedWork(app, feature);
      }
      await page.waitForTimeout(300);
      noAutomaticWrites(app, writes);
      expect(decisions.length === 2 && !dialogFailure).toBe(true);
      await mounted!.dispose();
    }));

test("visible metadata navigation keeps the ordinary in-page leave decision", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "metadata");
    const originalUrl = page.url();
    let dialogs = 0;
    let dialogFailure = false;
    page.on("dialog", (dialog) => {
      ++dialogs;
      void dialog.dismiss().catch(() => {
        dialogFailure = true;
      });
    });
    await catalogLink(app).click();
    await expect(
      page.getByRole("heading", {
        name: "Leave this metadata edit?",
        exact: true,
      }),
    ).toBeVisible();
    expect(page.url() === originalUrl).toBe(true);
    expect(dialogs).toBe(0);
    await button(app, "Keep editing").click();
    await retainedWork(app, "metadata");
    await expect(page.locator("#metadata-leave")).toHaveCount(0);
    await catalogLink(app).click();
    await expect(button(app, "Discard changes and leave")).toBeVisible();
    await button(app, "Discard changes and leave").click();
    await expect.poll(() => page.title()).toBe("Catalog · braid");
    expect(new URL(page.url()).pathname === "/catalog").toBe(true);
    noAutomaticWrites(app, 0);
    expect(dialogs === 0 && !dialogFailure).toBe(true);
  }));
