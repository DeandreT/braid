import {
  test,
  expect,
  refused,
  catalogRefused,
  ids,
  browserPaths,
  projectPath,
  revisionPath,
  type ControlledSession,
  type Kind,
  editField,
  privateMarkersVisuallyHidden,
} from "./fixture";
import { inputIds, savedReports, type Feature } from "./data";
import type { Page } from "@playwright/test";

const guarded = async (work: () => Promise<void>) => {
  try {
    await work();
  } catch (error) {
    const stack =
      error instanceof Error && typeof error.stack === "string"
        ? error.stack.slice(0, 16384)
        : "";
    const frame = stack
      .split("\n")
      .find(
        (line) =>
          line.startsWith("    at ") &&
          /\/recovery\.spec\.ts:\d{1,5}:\d{1,4}\)?$/.test(line),
      );
    const location = frame?.match(/recovery\.spec\.ts:(\d{1,5}):(\d{1,4})\)?$/);
    throw new Error(
      `The controlled private-recovery predicate failed at ${location ? `recovery.spec.ts:${location[1]}:${location[2]}` : "unavailable"}.`,
    );
  }
};
const sample = (app: ControlledSession, key: string) => async () =>
  (await app.snapshot())[key];
const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const refresh = async (app: ControlledSession, feature: Feature) => {
  const before = app.count("context");
  await button(
    app.page,
    feature === "creation" || feature === "metadata"
      ? "Refresh admission"
      : "Refresh access",
  ).click();
  await expect.poll(() => app.count("context")).toBeGreaterThan(before);
  await expect.poll(sample(app, "busy")).toBe(false);
};
const reviewHead = async (app: ControlledSession) => {
  const before = app.count("context");
  await button(app.page, "Review current head").click();
  await expect.poll(() => app.count("context")).toBeGreaterThan(before);
  await expect
    .poll(async () => {
      const facts = await app.snapshot();
      return facts["busy"] === false && facts["admitted"] === true;
    })
    .toBe(true);
};
async function prepare(
  app: ControlledSession,
  feature: Feature,
): Promise<void> {
  await app.open(browserPaths[feature], true);
  const page = app.page;
  await expect(page.locator("#" + inputIds[feature])).toBeVisible();
  app.draft(
    "Owned private draft",
    "Owned private description",
    "owned +%.native.json",
  );
  if (feature === "append") {
    await reviewHead(app);
    await expect(page.locator("#append-file")).toBeEnabled();
    await page.locator("#append-file").setInputFiles(app.file());
    await editField(page, "#append-filename", "owned +%.native.json");
    await expect(button(page, "Append revision")).toBeEnabled();
  } else {
    await editField(page, "#" + inputIds[feature], "Owned private draft");
    await editField(
      page,
      feature === "creation"
        ? "#project-description"
        : feature === "metadata"
          ? "#metadata-description"
          : "#upload-description",
      "Owned private description",
    );
    if (feature === "upload") {
      await expect(page.locator("#upload-file")).toBeEnabled();
      await page.locator("#upload-file").setInputFiles(app.file());
      await editField(page, "#upload-filename", "owned +%.native.json");
      await expect(button(page, "Upload mapping")).toBeEnabled();
    }
  }
}
const submitName: Record<Feature, string> = {
  creation: "Create project",
  metadata: "Save metadata",
  upload: "Upload mapping",
  append: "Append revision",
};
const recoverName: Record<Exclude<Feature, "metadata">, string> = {
  creation: "Recover creation",
  upload: "Resend original upload",
  append: "Resend original append",
};
const kind: Record<Feature, Kind> = {
  creation: "creation",
  metadata: "patch",
  upload: "upload",
  append: "append",
};
async function lose(app: ControlledSession, feature: Feature): Promise<void> {
  app.plans.set(kind[feature], { abort: true });
  await button(app.page, submitName[feature]).click();
  await expect.poll(() => app.wires.length).toBe(1);
  await expect.poll(sample(app, "unknown")).toBe(true);
}
async function reportVisible(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    return [
      ...document.querySelectorAll(
        ".saved-mapping-view .boundary-button strong",
      ),
    ].some((node) => {
      if (node.textContent?.trim() !== "Owned private saved explanation")
        return false;
      const visibility = getComputedStyle(node).visibility;
      if (visibility === "hidden" || visibility === "collapse") return false;
      const range = document.createRange();
      range.selectNodeContents(node);
      const rects = Array.from(range.getClientRects(), (rect) => ({
        left: rect.left,
        right: rect.right,
        top: rect.top,
        bottom: rect.bottom,
      }));
      for (
        let ancestor: Element | null = node;
        ancestor !== null;
        ancestor = ancestor.parentElement
      ) {
        const style = getComputedStyle(ancestor);
        if (
          style.display === "none" ||
          style.contentVisibility === "hidden" ||
          style.opacity === "0"
        )
          return false;
        // Ordinary page scrolling does not make an otherwise painted graph hidden.
        if (ancestor === document.scrollingElement) continue;
        const clipsX = style.overflowX !== "visible",
          clipsY = style.overflowY !== "visible";
        if (!clipsX && !clipsY) continue;
        const bounds = ancestor.getBoundingClientRect();
        const left = bounds.left + ancestor.clientLeft,
          top = bounds.top + ancestor.clientTop;
        for (const rect of rects) {
          if (clipsX) {
            rect.left = Math.max(rect.left, left);
            rect.right = Math.min(rect.right, left + ancestor.clientWidth);
          }
          if (clipsY) {
            rect.top = Math.max(rect.top, top);
            rect.bottom = Math.min(rect.bottom, top + ancestor.clientHeight);
          }
        }
      }
      return rects.some(
        (rect) => rect.right > rect.left && rect.bottom > rect.top,
      );
    });
  });
}
async function healthyProjectRow(page: Page): Promise<boolean> {
  return page
    .locator(".catalog-list")
    .evaluateAll((nodes) =>
      nodes.some(
        (node) =>
          node.getClientRects().length > 0 &&
          node.closest('[inert], [aria-hidden="true"], [hidden]') === null &&
          node.textContent?.includes("Owned synthetic project"),
      ),
    );
}
async function privateInvisible(page: Page): Promise<boolean> {
  return privateMarkersVisuallyHidden(page, [
    "Owned private draft",
    "Owned private description",
    "owned +%.native.json",
  ]);
}
async function foreignControlsRefused(
  app: ControlledSession,
  feature: Feature,
): Promise<void> {
  const names =
    feature === "creation"
      ? ["Create project", "Recover creation"]
      : feature === "metadata"
        ? ["Save metadata", "Review latest", "Keep mine"]
        : feature === "upload"
          ? ["Upload mapping", "Resend original upload", "Resume upload"]
          : ["Append revision", "Resend original append", "Resume append"];
  expect(
    await app.page.getByRole("button").evaluateAll(
      (buttons, names) =>
        buttons.every((node) => {
          const button = node as HTMLButtonElement,
            name = button.textContent?.replace(/\s+/g, " ").trim();
          return (
            !names.includes(name ?? "") ||
            button.disabled ||
            button.getClientRects().length === 0 ||
            button.closest('[inert], [aria-hidden="true"], [hidden]') !== null
          );
        }),
      names,
    ),
  ).toBe(true);
  const before = await app.snapshot();
  expect(before["actualOwner"]).toBe(true);
  expect(before["recoveryDenied"]).toBe(true);
  expect(before["privateProjectionHidden"]).toBe(true);
  expect(await app.privateMarkersHidden(feature)).toBe(true);
  const calls = Number(before["foreignActions"]),
    writes = app.wires.length;
  await app.page.locator("#probe-foreign-actions").click();
  await expect.poll(sample(app, "foreignActions")).toBeGreaterThan(calls);
  await expect.poll(sample(app, "busy")).toBe(false);
  if (feature === "upload" || feature === "append")
    await expect.poll(sample(app, "preparingFile")).toBe(false);
  const after = await app.snapshot();
  expect(after["actualOwner"]).toBe(true);
  expect(after["recoveryDenied"]).toBe(true);
  expect(after["privateProjectionHidden"]).toBe(true);
  expect(await app.privateMarkersHidden(feature)).toBe(true);
  expect(app.wires.length).toBe(writes);
}
function originalWires(
  app: ControlledSession,
  feature: Feature,
  count: number,
): void {
  const wires = app.wires.filter((wire) => wire.kind === feature);
  expect(wires.length).toBe(count);
  expect(
    wires.every(
      (wire) =>
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

test("private visual oracles detect inert aria-hidden text and fields but accept actual hidden ancestors", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open("/__state", true);
    for (const kind of ["text", "input"] as const) {
      await page.evaluate((kind) => {
        const container = document.createElement("div");
        container.id = "owned-visual-sensitivity";
        container.setAttribute("inert", "");
        container.setAttribute("aria-hidden", "true");
        const marker = document.createElement(
          kind === "text" ? "span" : "input",
        );
        if (marker instanceof HTMLInputElement)
          marker.value = "Owned private draft";
        else marker.textContent = "Owned private draft";
        container.append(marker);
        document.body.append(container);
      }, kind);
      try {
        expect(
          await page
            .locator("#owned-visual-sensitivity > *")
            .evaluate((node) => {
              const style = getComputedStyle(node);
              return (
                Array.from(node.getClientRects()).some(
                  (rect) => rect.width > 0 && rect.height > 0,
                ) &&
                style.display !== "none" &&
                style.visibility === "visible" &&
                node.closest("[inert]") !== null &&
                node.closest('[aria-hidden="true"]') !== null
              );
            }),
        ).toBe(true);
        expect(await app.privateMarkersHidden("creation")).toBe(false);
        expect(await privateInvisible(page)).toBe(false);
        for (const hiding of ["hidden", "display", "visibility"] as const) {
          await page
            .locator("#owned-visual-sensitivity")
            .evaluate((node, hiding) => {
              const container = node as HTMLElement;
              container.hidden = hiding === "hidden";
              container.style.display = hiding === "display" ? "none" : "";
              container.style.visibility =
                hiding === "visibility" ? "hidden" : "";
            }, hiding);
          await expect(
            page.locator("#owned-visual-sensitivity > *"),
          ).toBeHidden();
          expect(await app.privateMarkersHidden("creation")).toBe(true);
          expect(await privateInvisible(page)).toBe(true);
        }
      } finally {
        await page
          .locator("#owned-visual-sensitivity")
          .evaluate((node) => node.remove());
      }
    }
    expect(app.wires.length).toBe(0);
  }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`same-owner new epoch and foreign-owner quarantine retain the original ${feature}`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      await lose(app, feature);
      await app.arm();
      const before = app.wires.length;
      for (const transition of [
        "same-owner-epoch",
        "foreign-actor",
        "foreign-organization",
        "development-mode",
      ] as const) {
        app.mode =
          transition === "development-mode" ? "development" : "session";
        if (transition === "foreign-actor") app.identity(ids.foreignActor);
        else if (transition === "foreign-organization")
          app.identity(ids.actor, ids.foreignOrganization);
        else {
          app.identity();
        }
        await app.outsideRetry();
        await expect
          .poll(sample(app, "kind"))
          .toBe(app.mode === "development" ? "development" : "authenticated");
        expect(app.wires.length).toBe(before);
        if (transition !== "same-owner-epoch") {
          // Complete the exact current route admission too: status/context alone is insufficient.
          await refresh(app, feature);
          expect(await privateInvisible(page)).toBe(true);
          await foreignControlsRefused(app, feature);
          expect(app.wires.length).toBe(before);
        }
      }
      app.mode = "session";
      app.identity();
      const resourceKind: Kind = feature === "upload" ? "project" : "mapping";
      const resourcesBefore = app.count(resourceKind);
      await app.outsideRetry();
      expect(app.count(resourceKind)).toBe(resourcesBefore);
      expect(app.wires.length).toBe(before);
      if (feature === "append")
        app.mapping.head = {
          ...app.mapping.head,
          scope: {
            ...app.mapping.head.scope,
            revisionId: "40000000-0000-4000-8000-000000000088",
          },
          sequence: "9007199254740995",
        };
      if (feature !== "creation") {
        const held = app.hold(resourceKind, {
          json: feature === "upload" ? app.project : app.mapping,
        });
        const pending = refresh(app, feature);
        await held.entered;
        expect(app.wires.length).toBe(before);
        expect((await app.snapshot())["canRecover"]).toBe(false);
        held.release();
        await held.finished;
        await pending;
      } else await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      expect(app.wires.length).toBe(before);
      if (feature === "append")
        expect((await app.snapshot())["advancedHeadSeen"]).toBe(true);
      if (feature === "metadata") {
        // Reading matching current values is not an acknowledgement of the earlier PATCH.
        app.mapping.name = "Owned private draft";
        app.mapping.description = "Owned private description";
        await button(page, "Review latest").click();
        await expect(page.locator("#metadata-review")).toBeVisible();
        expect((await app.snapshot())["unknown"]).toBe(true);
        expect(app.wires.length).toBe(before);
        await button(page, "Keep mine").click();
        app.plans.set("patch", {
          json: app.metadata(),
          headers: { ETag: app.tag() },
        });
        await button(page, "Save metadata").click();
        await expect.poll(() => app.wires.length).toBe(2);
        expect(
          app.wires.every(
            (wire) =>
              wire.exactBody &&
              wire.freshCatalogToken &&
              wire.distinctAudiences &&
              wire.correctHeaders,
          ),
        ).toBe(true);
        // This separate matching-values write is a confirmed no-op, never acknowledgement of the earlier unknown PATCH.
        await expect.poll(sample(app, "acknowledged")).toBe(true);
        expect((await app.snapshot())["latestSaveChanged"]).toBe(false);
        await expect.poll(sample(app, "unknown")).toBe(true);
      } else {
        app.plans.set(
          feature,
          feature === "creation" ? app.creation(200) : app.committed(feature),
        );
        await button(page, recoverName[feature]).click();
        await expect.poll(sample(app, "acknowledged")).toBe(true);
        originalWires(app, feature, 2);
      }
    }));

for (const mode of ["development", "session"] as const)
  test(`confirmed foreign actor clears unsent metadata before fresh ${mode} admission`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      app.mode = mode;
      await prepare(app, "metadata");
      await expect.poll(sample(app, "retained")).toBe(true);
      app.identity(ids.foreignActor);
      await app.outsideRetry();
      if (mode === "session") {
        // The fingerprint boundary rejects its own context response as stale.
        expect((await app.snapshot())["retained"]).toBe(true);
        await app.outsideRetry();
      }
      await expect.poll(sample(app, "retained")).toBe(false);
      expect(await privateInvisible(page)).toBe(true);
      expect(app.wires.length).toBe(0);
      await refresh(app, "metadata");
      await expect(button(page, "Save metadata")).toBeEnabled();
      expect(
        await page
          .locator("#metadata-name")
          .evaluate(
            (node, baseline) => (node as HTMLInputElement).value === baseline,
            app.mapping.name,
          ),
      ).toBe(true);
      expect((await app.snapshot())["retained"]).toBe(false);
      expect(app.wires.length).toBe(0);
    }));

test("Development actor change preserves an in-flight metadata attempt and fences its late acknowledgement", async ({
  app,
  page,
}) =>
  guarded(async () => {
    app.mode = "development";
    await prepare(app, "metadata");
    const write = app.hold("patch", {
      json: {
        ...app.metadata(),
        name: "Owned private draft",
        description: "Owned private description",
        version: "2",
      },
      headers: {
        ETag: `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:2"`,
      },
    });
    await button(page, "Save metadata").click();
    await write.entered;
    await app.arm();
    app.identity(ids.foreignActor);
    await app.outsideRetry();
    await refresh(app, "metadata");
    await expect.poll(() => write.canceled).toBe(true);
    expect((await app.snapshot())["retained"]).toBe(true);
    await foreignControlsRefused(app, "metadata");
    write.release();
    await write.finished;
    expect((await app.snapshot())["acknowledged"]).toBe(false);
    expect(app.wires.length).toBe(1);
    app.identity();
    await app.outsideRetry();
    await refresh(app, "metadata");
    await expect.poll(sample(app, "unknown")).toBe(true);
    await expect(button(page, "Save metadata")).toBeDisabled();
    await button(page, "Review latest").click();
    await expect(page.locator("#metadata-review")).toBeVisible();
    expect((await app.snapshot())["unknown"]).toBe(true);
    expect((await app.snapshot())["acknowledged"]).toBe(false);
    expect(app.wires.length).toBe(1);
  }));

for (const observationEnd of ["unchanged", "last-waiter-canceled"] as const)
  for (const feature of ["creation", "metadata", "upload", "append"] as const)
    test(`${observationEnd} checking keeps the already sent ${feature} alive without false uncertainty`, async ({
      app,
      page,
    }) =>
      guarded(async () => {
        await prepare(app, feature);
        const success =
          feature === "creation"
            ? app.creation()
            : feature === "metadata"
              ? {
                  json: {
                    ...app.metadata(),
                    name: "Owned private draft",
                    description: "Owned private description",
                    version: "2",
                  },
                  headers: {
                    ETag: `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:2"`,
                  },
                }
              : app.committed(feature);
        const write = app.hold(kind[feature], success);
        await button(page, submitName[feature]).click();
        await write.entered;
        await app.arm();
        const status = app.hold("status", { json: app.status() });
        await app.observe();
        await status.entered;
        expect(write.canceled).toBe(false);
        expect(app.wires.length).toBe(1);
        if (observationEnd === "last-waiter-canceled") {
          await page.locator("#probe-cancel-all").click();
          await expect.poll(() => status.canceled).toBe(true);
          const canceled = await app.snapshot();
          expect(canceled["visible"]).toBe(false);
          expect(canceled["accepts"]).toBe(true);
          expect(canceled["canDispatch"]).toBe(false);
        }
        write.release();
        await write.finished;
        expect(write.canceled).toBe(false);
        status.release();
        await status.finished;
        if (observationEnd === "last-waiter-canceled") await app.observe();
        await expect.poll(sample(app, "kind")).toBe("authenticated");
        await expect.poll(sample(app, "acknowledged")).toBe(true);
        expect((await app.snapshot())["unknown"]).toBe(false);
        expect(app.wires.length).toBe(1);
        expect((await app.snapshot())["notices"]).toBe(0);
        expect(
          app.wires[0]?.distinctAudiences && app.wires[0]?.freshCatalogToken,
        ).toBe(true);
      }));

for (const feature of ["creation", "upload", "append"] as const)
  for (const observationEnd of ["unchanged", "foreign-owner"] as const)
    test(`${feature} saved focus waits for ${observationEnd} access observation without replay`, async ({
      app,
      page,
    }) =>
      guarded(async () => {
        await prepare(app, feature);
        await app.arm();
        const target =
          feature === "creation"
            ? "creation-result"
            : feature === "upload"
              ? "upload-saved"
              : "append-status";
        await page.evaluate((id) => {
          const counts = { saved: 0 };
          Object.defineProperty(window, "__weft119SavedFocus", {
            value: counts,
          });
          document.addEventListener("focusin", (event) => {
            if (event.target instanceof HTMLElement && event.target.id === id)
              ++counts.saved;
          });
        }, target);
        const focusedCount = () =>
          page.evaluate(
            () =>
              (window as unknown as { __weft119SavedFocus: { saved: number } })
                .__weft119SavedFocus.saved,
          );
        const write = app.hold(
          feature,
          feature === "creation" ? app.creation() : app.committed(feature),
        );
        await button(page, submitName[feature]).click();
        await write.entered;
        // Hold the real access admission started by the acknowledged write.
        const status = app.hold("status", () => ({ json: app.status() }));
        write.release();
        await write.finished;
        await status.entered;
        await expect(
          page.getByText("Checking workspace access…", { exact: true }),
        ).toBeVisible();
        expect(await focusedCount()).toBe(0);
        await expect(page.locator(`#${target}`)).not.toBeFocused();
        if (observationEnd === "foreign-owner") app.identity(ids.foreignActor);
        status.release();
        await status.finished;
        await expect(
          page.getByText("Checking workspace access…", { exact: true }),
        ).toHaveCount(0);
        if (observationEnd === "unchanged") {
          await expect(page.locator(`#${target}`)).toBeFocused();
          expect(await focusedCount()).toBe(1);
          // A consumed focus request must not steal focus on the next safe check.
          const later = app.hold("status", () => ({ json: app.status() }));
          await app.observe();
          await later.entered;
          later.release();
          await later.finished;
          await expect(
            page.getByText("Checking workspace access…", { exact: true }),
          ).toHaveCount(0);
          await expect(page.locator("#probe-observe")).toBeFocused();
          expect(await focusedCount()).toBe(1);
        } else {
          await expect.poll(sample(app, "notices")).toBe(1);
          // Creation/upload remove their private saved heading on an owner boundary;
          // append retains a generic status container without receipt details.
          if (feature !== "append")
            await expect(page.locator(`#${target}`)).toHaveCount(0);
          else await expect(page.locator(`#${target}`)).not.toBeFocused();
          expect(await focusedCount()).toBe(0);
        }
        expect(app.wires.length).toBe(1);
        expect(app.count("resume")).toBe(0);
      }));

test("historical creation receipt survives denial and current access retry stays GET-only", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "creation");
    app.plans.set("creation", app.creation());
    app.plans.set("project", catalogRefused(404, "notFound"));
    await button(page, "Create project").click();
    await expect.poll(sample(app, "acknowledged")).toBe(true);
    expect(app.wires.length).toBe(1);
    app.context.features.createProjects = false;
    app.context.organizationActions.createProjects = false;
    app.epoch();
    await app.outsideRetry();
    await refresh(app, "creation");
    await expect(button(page, "Retry project access")).toBeEnabled();
    const previousReads = app.count("project");
    await button(page, "Retry project access").click();
    await expect
      .poll(() => app.count("project"))
      .toBeGreaterThan(previousReads);
    expect(app.wires.length).toBe(1);
    expect((await app.snapshot())["acknowledged"]).toBe(true);
  }));

for (const feature of ["upload", "append"] as const)
  test(`known ${feature} operation uses GET-only status then one deliberate original resume`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      app.plans.set(feature, app.pending(feature));
      await button(page, submitName[feature]).click();
      await expect.poll(() => app.wires.length).toBe(1);
      app.epoch();
      await app.outsideRetry();
      await refresh(app, feature);
      expect(app.wires.length).toBe(1);
      expect(app.count("resume")).toBe(0);
      await button(
        page,
        feature === "upload" ? "Check upload status" : "Check append status",
      ).click();
      await expect.poll(() => app.count("receipt")).toBe(1);
      expect(app.wires.length).toBe(1);
      app.plans.set("resume", app.committed(feature));
      await button(
        page,
        feature === "upload" ? "Resume upload" : "Resume append",
      ).click();
      await expect.poll(sample(app, "acknowledged")).toBe(true);
      expect(app.count("resume")).toBe(1);
      const resume = app.wires.at(-1);
      expect(
        resume?.kind === "resume" &&
          resume.correctHeaders &&
          resume.freshCatalogToken &&
          resume.distinctAudiences,
      ).toBe(true);
    }));

for (const stage of ["file", "hash"] as const)
  for (const feature of ["upload", "append"] as const)
    test(`late ${feature} ${stage} preparation cannot attach after original settlement or replace a new file`, async ({
      app,
      page,
    }) =>
      guarded(async () => {
        await app.open(browserPaths[feature], true);
        await expect(page.locator("#" + inputIds[feature])).toBeVisible();
        if (feature === "append") await reviewHead(app);
        await app.arm();
        expect((await app.snapshot())["actualOwner"]).toBe(true);
        await page.evaluate((stage) => {
          const originalRead = File.prototype.arrayBuffer,
            originalDigest = crypto.subtle.digest;
          let open!: () => void, timer: ReturnType<typeof setTimeout>;
          const gate = new Promise<void>((resolve) => {
            open = resolve;
          });
          const owned: Array<{
            original: Promise<ArrayBuffer>;
            settled: boolean;
          }> = [];
          let entered = false;
          const own = (
            kind: "file" | "hash",
            work: () => Promise<ArrayBuffer>,
          ) => {
            if (owned.length >= 4)
              throw new Error(
                "The original browser preparation task cap was exceeded.",
              );
            const record = {
              original: null as unknown as Promise<ArrayBuffer>,
              settled: false,
            };
            record.original = (async () => {
              try {
                if (kind === stage) {
                  entered = true;
                  await gate;
                }
                return await work();
              } finally {
                record.settled = true;
              }
            })();
            owned.push(record);
            return record.original;
          };
          // Retain BOTH standard-operation tasks; a file read's finally alone does not join its digest continuation.
          File.prototype.arrayBuffer = function () {
            return own("file", () => originalRead.call(this));
          };
          crypto.subtle.digest = function (algorithm, data) {
            return own("hash", () =>
              originalDigest.call(this, algorithm, data),
            );
          };
          const restore = () => {
            clearTimeout(timer);
            File.prototype.arrayBuffer = originalRead;
            crypto.subtle.digest = originalDigest;
            open();
          };
          Object.defineProperty(window, "__weft119FileGate", {
            configurable: true,
            value: {
              entered: () => entered,
              settled: () =>
                owned.length > 0 && owned.every((record) => record.settled),
              open,
              drain: async () => {
                const original = [...owned];
                await Promise.allSettled(
                  original.map((record) => record.original),
                );
                return (
                  original.length === owned.length &&
                  original.length > 0 &&
                  original.every((record) => record.settled)
                );
              },
              release: open,
              restore,
            },
          });
          // Automatic release opens the stage without unwrapping a later digest before it can be owned.
          timer = setTimeout(open, 10000);
        }, stage);
        await page
          .locator(feature === "upload" ? "#upload-file" : "#append-file")
          .setInputFiles(app.file());
        await expect
          .poll(() =>
            page.evaluate(() =>
              (
                window as unknown as {
                  __weft119FileGate: { entered(): boolean };
                }
              ).__weft119FileGate.entered(),
            ),
          )
          .toBe(true);
        app.identity(ids.foreignActor);
        await app.outsideRetry();
        await page.evaluate(() =>
          (
            window as unknown as { __weft119FileGate: { open(): void } }
          ).__weft119FileGate.open(),
        );
        await expect.poll(sample(app, "preparingFile")).toBe(false);
        await expect
          .poll(() =>
            page.evaluate(() =>
              (
                window as unknown as {
                  __weft119FileGate: { settled(): boolean };
                }
              ).__weft119FileGate.settled(),
            ),
          )
          .toBe(true);
        expect(
          await page.evaluate(() =>
            (
              window as unknown as {
                __weft119FileGate: { drain(): Promise<boolean> };
              }
            ).__weft119FileGate.drain(),
          ),
        ).toBe(true);
        await page.evaluate(() =>
          (
            window as unknown as { __weft119FileGate: { restore(): void } }
          ).__weft119FileGate.restore(),
        );
        const settled = await app.snapshot();
        expect(settled["actualOwner"]).toBe(true);
        expect(settled["fileAbsent"]).toBe(true);
        expect(settled["filenameAbsent"]).toBe(true);
        expect(settled["preparingFile"]).toBe(false);
        expect(await privateInvisible(page)).toBe(true);
        expect(app.wires.length).toBe(0);
        // Inspect after exact original settlement BEFORE another admission could hide a stale attachment.
        await refresh(app, feature);
        const admitted = await app.snapshot();
        expect(admitted["fileAbsent"]).toBe(true);
        expect(admitted["filenameAbsent"]).toBe(true);
        // Re-admit the original owner before this positive new-file control; foreign private-slot overwrite is not authority.
        app.identity();
        await app.outsideRetry();
        await refresh(app, feature);
        // A genuinely new current-owner file must complete after the old original has settled and wrappers were restored.
        if (feature === "append") await reviewHead(app);
        else {
          await editField(page, "#upload-name", "Owned current healthy upload");
          await editField(
            page,
            "#upload-description",
            "Owned current healthy description",
          );
        }
        const input = page.locator(
          feature === "upload" ? "#upload-file" : "#append-file",
        );
        await expect(input).toBeEnabled();
        await input.setInputFiles({
          name: "owned-current-file.native.json",
          mimeType: "application/json",
          buffer: Buffer.from('{"current":true}'),
        });
        await expect.poll(sample(app, "preparingFile")).toBe(false);
        await expect.poll(sample(app, "currentFile")).toBe(true);
        await expect.poll(sample(app, "currentFilename")).toBe(true);
        await expect(button(page, submitName[feature])).toBeEnabled();
        expect((await app.snapshot())["newDispatchAllowed"]).toBe(true);
        expect(
          await page.evaluate(() =>
            (
              window as unknown as {
                __weft119FileGate: { drain(): Promise<boolean> };
              }
            ).__weft119FileGate.drain(),
          ),
        ).toBe(true);
        expect((await app.snapshot())["currentFile"]).toBe(true);
        expect((await app.snapshot())["currentFilename"]).toBe(true);
        expect(app.wires.length).toBe(0);
      }));

test("changed admission does not cancel its own current mapping read twice", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "append");
    await lose(app, "append");
    app.epoch();
    await app.outsideRetry();
    const held = app.hold("mapping", { json: app.mapping });
    const current = refresh(app, "append");
    await held.entered;
    expect(held.canceled).toBe(false);
    held.release();
    await held.finished;
    await current;
    await expect.poll(sample(app, "unknown")).toBe(true);
    expect(held.canceled).toBe(false);
    expect(app.wires.length).toBe(1);
  }));

for (const status of [401, 403] as const)
  for (const classification of [
    "authenticated",
    "anonymous",
    "authenticationRequired",
    "unavailable",
  ] as const)
    test(`protected ${status} clears first, classifies ${classification} once and never resends`, async ({
      app,
      page,
    }) =>
      guarded(async () => {
        await prepare(app, "creation");
        const rejected = app.hold(
          "creation",
          catalogRefused(status, "identityRejected"),
        );
        await app.arm();
        const before = app.count("status");
        await button(page, "Create project").click();
        await rejected.entered;
        await expect.poll(() => app.wires.length).toBe(1);
        const contexts = app.count("context");
        if (classification === "unavailable")
          app.plans.set("status", refused(503));
        else app.session = classification;
        rejected.release();
        await rejected.finished;
        await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
        expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
        await expect.poll(sample(app, "kind")).toBe(classification);
        // Exactly one action admission status and one independent status-only classification.
        expect(app.count("status") - before).toBe(2);
        expect(app.count("context")).toBe(contexts);
        const observed = await app.snapshot();
        expect(observed["context"]).toBe(false);
        expect(observed["newDispatchAllowed"]).toBe(false);
        expect(app.wires.length).toBe(1);
      }));

test("protected rejection during checking fences the old status without resending", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "creation");
    const rejected = app.hold(
      "creation",
      catalogRefused(403, "identityRejected"),
    );
    await app.arm();
    const before = app.count("status");
    await button(page, "Create project").click();
    await rejected.entered;
    const oldPositive = app.status();
    const oldStatus = app.hold("status", { json: oldPositive });
    await app.observe();
    await oldStatus.entered;
    await expect.poll(sample(app, "kind")).toBe("checking");
    expect((await app.snapshot())["context"]).toBe(true);
    const contexts = app.count("context");
    app.plans.delete("status");
    app.session = "anonymous";
    rejected.release();
    await rejected.finished;
    await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
    expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
    await expect.poll(sample(app, "kind")).toBe("anonymous");
    expect(app.count("status") - before).toBe(3);
    expect(app.count("context")).toBe(contexts);
    oldStatus.release();
    await oldStatus.finished;
    expect((await app.snapshot())["kind"]).toBe("anonymous");
    expect((await app.snapshot())["context"]).toBe(false);
    expect(app.wires.length).toBe(1);
  }));

test("protected classification survives canceling all public probe observers", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "creation");
    const rejected = app.hold(
      "creation",
      catalogRefused(403, "identityRejected"),
    );
    await app.arm();
    const before = await app.snapshot();
    const statusReads = app.count("status");
    await button(page, "Create project").click();
    await rejected.entered;
    expect(app.wires.length).toBe(1);
    const contexts = app.count("context"),
      requests = app.order.length;
    // Only install the classification hold after real action admission and dispatch.
    const classification = app.hold("status", { json: app.status() });
    rejected.release();
    await rejected.finished;
    await classification.entered;
    await expect.poll(sample(app, "notices")).toBe(1);
    // This public observer joins the service-owned request; it must not start a third GET.
    await app.observe();
    const joined = await app.snapshot();
    expect(joined["kind"]).toBe("checking");
    expect(joined["context"]).toBe(false);
    expect(joined["clearedSynchronously"]).toBe(true);
    expect(joined["actualOwner"]).toBe(true);
    expect(joined["newDispatchAllowed"]).toBe(false);
    expect(joined["completed"]).toBe(before["completed"]);
    expect(joined["failed"]).toBe(before["failed"]);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.order.slice(requests)).toEqual(["status"]);
    expect(classification.canceled).toBe(false);
    expect(classification.settled).toBe(false);
    // cancelAll only owns subscriptions created by probe.observe/admit, not the feature or service.
    await page.locator("#probe-cancel-all").click();
    const canceled = await app.snapshot();
    expect(canceled["kind"]).toBe("checking");
    expect(canceled["context"]).toBe(false);
    expect(canceled["notices"]).toBe(1);
    expect(canceled["generation"]).toBe(Number(before["generation"]) + 1);
    expect(canceled["newDispatchAllowed"]).toBe(false);
    expect(canceled["completed"]).toBe(before["completed"]);
    expect(canceled["failed"]).toBe(before["failed"]);
    expect(classification.canceled).toBe(false);
    expect(classification.settled).toBe(false);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.count("context")).toBe(contexts);
    expect(app.order.slice(requests)).toEqual(["status"]);
    app.plans.delete("status");
    classification.release();
    await classification.finished;
    await expect.poll(sample(app, "kind")).toBe("authenticated");
    const terminal = await app.snapshot();
    expect(terminal["context"]).toBe(false);
    expect(terminal["admitted"]).toBe(false);
    expect(terminal["newDispatchAllowed"]).toBe(false);
    expect(terminal["busy"]).toBe(false);
    expect(terminal["notices"]).toBe(1);
    expect(terminal["generation"]).toBe(Number(before["generation"]) + 1);
    expect(terminal["clearedSynchronously"]).toBe(true);
    // Unsubscribe is not completion/error; these original counters are never reset.
    expect(terminal["completed"]).toBe(before["completed"]);
    expect(terminal["failed"]).toBe(before["failed"]);
    expect(classification.canceled).toBe(false);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.count("context")).toBe(contexts);
    expect(app.order.slice(requests)).toEqual(["status"]);
    originalWires(app, "creation", 1);
    expect(app.wires.length).toBe(1);
    expect(app.authPosts).toBe(0);
    expect(app.unexpected).toBe(0);
    expect(app.routeFailures).toBe(0);
  }));

test("explicit invalidation preserves the public classifier observer but fences its old positive result", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "creation");
    const rejected = app.hold(
      "creation",
      catalogRefused(403, "identityRejected"),
    );
    await app.arm();
    const before = await app.snapshot();
    const statusReads = app.count("status");
    await button(page, "Create project").click();
    await rejected.entered;
    expect(app.wires.length).toBe(1);
    const contexts = app.count("context"),
      requests = app.order.length;
    const classification = app.hold("status", { json: app.status() });
    rejected.release();
    await rejected.finished;
    await classification.entered;
    await expect.poll(sample(app, "notices")).toBe(1);
    await app.observe();
    const joined = await app.snapshot();
    expect(joined["kind"]).toBe("checking");
    expect(joined["context"]).toBe(false);
    expect(joined["clearedSynchronously"]).toBe(true);
    expect(joined["actualOwner"]).toBe(true);
    expect(joined["completed"]).toBe(before["completed"]);
    expect(joined["failed"]).toBe(before["failed"]);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.order.slice(requests)).toEqual(["status"]);
    // Do not cancel the manual observer: it must retain the original underlying HTTP request.
    await app.invalidate();
    const invalidated = await app.snapshot();
    expect(invalidated["kind"]).toBe("unavailable");
    expect(invalidated["context"]).toBe(false);
    expect(invalidated["admitted"]).toBe(false);
    expect(invalidated["newDispatchAllowed"]).toBe(false);
    expect(invalidated["notices"]).toBe(2);
    expect(invalidated["generation"]).toBe(Number(before["generation"]) + 2);
    expect(invalidated["clearedSynchronously"]).toBe(true);
    expect(invalidated["completed"]).toBe(before["completed"]);
    expect(invalidated["failed"]).toBe(before["failed"]);
    expect(classification.canceled).toBe(false);
    expect(classification.settled).toBe(false);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.count("context")).toBe(contexts);
    expect(app.order.slice(requests)).toEqual(["status"]);
    // A hold plan must not be reused by the later independent healthy control.
    app.plans.delete("status");
    classification.release();
    await classification.finished;
    // Route fulfillment is not browser settlement: join the old observer's terminal error first.
    await expect.poll(sample(app, "failed")).toBe(Number(before["failed"]) + 1);
    const fenced = await app.snapshot();
    expect(fenced["kind"]).toBe("unavailable");
    expect(fenced["context"]).toBe(false);
    expect(fenced["admitted"]).toBe(false);
    expect(fenced["newDispatchAllowed"]).toBe(false);
    expect(fenced["busy"]).toBe(false);
    expect(fenced["notices"]).toBe(2);
    expect(fenced["generation"]).toBe(Number(before["generation"]) + 2);
    expect(fenced["completed"]).toBe(before["completed"]);
    expect(fenced["failed"]).toBe(Number(before["failed"]) + 1);
    expect(classification.canceled).toBe(false);
    expect(app.count("status")).toBe(statusReads + 2);
    expect(app.count("context")).toBe(contexts);
    expect(app.order.slice(requests)).toEqual(["status"]);
    expect(app.wires.length).toBe(1);
    expect(app.authPosts).toBe(0);
    // Only after the old browser observer and original held route have settled, start one fresh GET.
    await app.observe();
    await expect
      .poll(sample(app, "completed"))
      .toBe(Number(before["completed"]) + 1);
    const healthy = await app.snapshot();
    expect(healthy["kind"]).toBe("authenticated");
    expect(healthy["context"]).toBe(false);
    expect(healthy["admitted"]).toBe(false);
    expect(healthy["newDispatchAllowed"]).toBe(false);
    expect(healthy["notices"]).toBe(2);
    expect(healthy["generation"]).toBe(Number(before["generation"]) + 2);
    expect(healthy["clearedSynchronously"]).toBe(true);
    expect(healthy["completed"]).toBe(Number(before["completed"]) + 1);
    expect(healthy["failed"]).toBe(Number(before["failed"]) + 1);
    expect(classification.canceled).toBe(false);
    expect(app.count("status")).toBe(statusReads + 3);
    expect(app.count("context")).toBe(contexts);
    expect(app.order.slice(requests)).toEqual(["status", "status"]);
    originalWires(app, "creation", 1);
    expect(app.wires.length).toBe(1);
    expect(app.authPosts).toBe(0);
    expect(app.unexpected).toBe(0);
    expect(app.routeFailures).toBe(0);
  }));

test("an authenticated observer without current workspace capability is not signed out", async ({
  app,
}) =>
  guarded(async () => {
    app.plans.set("context", catalogRefused(404, "notFound"));
    await app.open("/__state", true);
    await app.outsideRetry();
    await expect.poll(sample(app, "kind")).toBe("authenticated");
    expect((await app.snapshot())["context"]).toBe(false);
    expect(app.wires.length).toBe(0);
  }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`operational uncertainty suspends and retains ${feature} without resending`, async ({
    app,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      await lose(app, feature);
      await app.arm();
      const before = await app.snapshot();
      expect(before["kind"]).toBe("authenticated");
      expect(before["actualOwner"]).toBe(true);
      expect(before["context"]).toBe(true);
      expect(before["admitted"]).toBe(true);
      expect(before["retained"]).toBe(true);
      expect(before["unknown"]).toBe(true);
      expect(before["privateProjectionHidden"]).toBe(false);
      const draftRetained = () =>
        app.page
          .locator("#" + inputIds[feature])
          .evaluate(
            (node, feature) =>
              (node as HTMLInputElement).value ===
              (feature === "append"
                ? "owned +%.native.json"
                : "Owned private draft"),
            feature,
          );
      expect(await draftRetained()).toBe(true);
      const statusReads = app.count("status");
      const failedStatus = app.hold("status", refused(503));
      await app.observe();
      await failedStatus.entered;
      const checking = await app.snapshot();
      expect(checking["kind"]).toBe("checking");
      expect(checking["notices"]).toBe(before["notices"]);
      expect(checking["generation"]).toBe(before["generation"]);
      expect(checking["actualOwner"]).toBe(true);
      expect(checking["context"]).toBe(true);
      expect(checking["admitted"]).toBe(true);
      expect(checking["retained"]).toBe(true);
      expect(checking["unknown"]).toBe(true);
      expect(checking["privateProjectionHidden"]).toBe(false);
      expect(checking["accepts"]).toBe(true);
      expect(checking["canDispatch"]).toBe(false);
      expect(await draftRetained()).toBe(true);
      failedStatus.release();
      await failedStatus.finished;
      await expect
        .poll(sample(app, "notices"))
        .toBeGreaterThan(Number(before["notices"]));
      await expect.poll(sample(app, "kind")).toBe("unavailable");
      const firstFailure = await app.snapshot();
      expect(firstFailure["notices"]).toBe(Number(before["notices"]) + 1);
      expect(firstFailure["generation"]).toBe(Number(before["generation"]) + 1);
      expect(firstFailure["clearedSynchronously"]).toBe(true);
      expect(firstFailure["actualOwner"]).toBe(true);
      expect(firstFailure["context"]).toBe(false);
      expect(firstFailure["admitted"]).toBe(false);
      expect(firstFailure["accepts"]).toBe(false);
      expect(firstFailure["canDispatch"]).toBe(false);
      expect(firstFailure["retained"]).toBe(true);
      expect(firstFailure["privateProjectionHidden"]).toBe(true);
      expect(await app.privateMarkersHidden(feature)).toBe(true);
      expect(await privateInvisible(app.page)).toBe(true);
      expect(app.count("status")).toBe(statusReads + 1);
      expect(app.wires.length).toBe(1);
      // All first-failure assertions precede any distinct successful epoch.
      app.plans.delete("status");
      app.epoch();
      await app.outsideRetry();
      await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      expect(app.wires.length).toBe(1);
    }));

for (const feature of ["metadata", "upload", "append"] as const)
  test(`foreign full resource reply cannot retarget the private ${feature} original`, async ({
    app,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      await lose(app, feature);
      const resource: Kind = feature === "upload" ? "project" : "mapping";
      app.plans.set(resource, {
        json:
          feature === "upload"
            ? {
                ...app.project,
                scope: { ...app.project.scope, projectId: ids.foreignProject },
              }
            : {
                ...app.mapping,
                scope: { ...app.mapping.scope, mappingId: ids.foreignMapping },
                head: {
                  ...app.mapping.head,
                  scope: {
                    ...app.mapping.head.scope,
                    mappingId: ids.foreignMapping,
                  },
                },
              },
      });
      app.epoch();
      await app.outsideRetry();
      await refresh(app, feature);
      expect((await app.snapshot())["canRecover"]).toBe(false);
      expect(app.wires.length).toBe(1);
      app.plans.delete(resource);
      await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      expect(app.wires.length).toBe(1);
    }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`coherent feature denial and scoped capability gate ${feature} recovery without acknowledging the original`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      await lose(app, feature);
      const featureKey =
        feature === "creation"
          ? "createProjects"
          : feature === "metadata"
            ? "editMetadata"
            : feature === "upload"
              ? "createMappings"
              : "uploadRevisions";
      for (const level of ["feature", "scoped"] as const) {
        app.context.features[featureKey] = level !== "feature";
        // The public context schema requires organization permission to be false
        // when project creation is disabled; this is not a feature-only isolation.
        if (feature === "creation")
          app.context.organizationActions.createProjects = false;
        else if (feature === "upload")
          app.project.access.actions.createMappings = level !== "scoped";
        else
          app.mapping.access.actions[
            feature === "metadata" ? "editMetadata" : "uploadRevisions"
          ] = level !== "scoped";
        app.epoch();
        await app.outsideRetry();
        await refresh(app, feature);
        const denied = await app.snapshot();
        expect(denied["kind"]).toBe("authenticated");
        expect(denied["context"]).toBe(true);
        expect(denied["admitted"]).toBe(true);
        expect(denied["retained"]).toBe(true);
        expect(denied["acknowledged"]).toBe(false);
        expect(denied["canRecover"]).toBe(false);
        if (feature === "metadata") {
          // canSave alone is already false because the original PATCH needs
          // review. This existing boolean also requires the actual canEdit denial.
          expect(denied["recoveryDenied"]).toBe(true);
          expect(denied["newDispatchAllowed"]).toBe(false);
          expect(await page.locator("#metadata-name").count()).toBe(0);
        }
        expect(app.wires.length).toBe(1);
        app.context.features[featureKey] = true;
        app.context.organizationActions.createProjects = true;
        app.project.access.actions.createMappings = true;
        app.mapping.access.actions.editMetadata = true;
        app.mapping.access.actions.uploadRevisions = true;
        await refresh(app, feature);
        const healthy = await app.snapshot();
        expect(healthy["unknown"]).toBe(true);
        expect(healthy["retained"]).toBe(true);
        expect(healthy["acknowledged"]).toBe(false);
        if (feature === "metadata") {
          expect(healthy["recoveryDenied"]).toBe(false);
          expect(healthy["needsReview"]).toBe(true);
          expect(healthy["canRecover"]).toBe(false);
          await expect(page.locator("#metadata-name")).toBeVisible();
          expect(
            await page
              .locator("#metadata-name")
              .evaluate(
                (node) =>
                  !(node as HTMLInputElement).readOnly &&
                  (node as HTMLInputElement).value === "Owned private draft",
              ),
          ).toBe(true);
        } else expect(healthy["canRecover"]).toBe(true);
        expect(app.wires.length).toBe(1);
      }
      app.context.features[featureKey] = true;
      app.context.organizationActions.createProjects = true;
      app.project.access.actions.createMappings = true;
      app.mapping.access.actions.editMetadata = true;
      app.mapping.access.actions.uploadRevisions = true;
      await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      if (feature === "metadata") {
        const reads = app.count("metadata");
        await button(page, "Review latest").click();
        await expect.poll(() => app.count("metadata")).toBeGreaterThan(reads);
        await expect(page.locator("#metadata-review")).toBeVisible();
        expect((await app.snapshot())["unknown"]).toBe(true);
        expect((await app.snapshot())["acknowledged"]).toBe(false);
        expect(app.wires.length).toBe(1);
        await button(page, "Keep mine").click();
        await expect(button(page, "Save metadata")).toBeEnabled();
        const reviewed = await app.snapshot();
        expect(reviewed["canRecover"]).toBe(true);
        expect(reviewed["newDispatchAllowed"]).toBe(true);
        expect(reviewed["recoveryDenied"]).toBe(false);
        expect(reviewed["needsReview"]).toBe(false);
        expect(reviewed["unknown"]).toBe(true);
        expect(reviewed["retained"]).toBe(true);
        expect(reviewed["acknowledged"]).toBe(false);
      }
      expect(app.wires.length).toBe(1);
    }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`late ${feature} acknowledgement cannot publish under a foreign owner`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      const success =
        feature === "creation"
          ? app.creation()
          : feature === "metadata"
            ? {
                json: {
                  ...app.metadata(),
                  name: "Owned private draft",
                  description: "Owned private description",
                  version: "2",
                },
                headers: {
                  ETag: `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:2"`,
                },
              }
            : app.committed(feature);
      const write = app.hold(kind[feature], success);
      await button(page, submitName[feature]).click();
      await write.entered;
      await app.arm();
      app.identity(ids.foreignActor);
      await app.outsideRetry();
      await expect.poll(() => write.canceled).toBe(true);
      write.release();
      await write.finished;
      expect(await privateInvisible(page)).toBe(true);
      expect((await app.snapshot())["acknowledged"]).toBe(false);
      expect(app.wires.length).toBe(1);
      app.identity();
      await app.outsideRetry();
      await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      expect(app.wires.length).toBe(1);
    }));

test("a late old context cannot replace the explicit current parent admission", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "append");
    const old = app.hold("context", app.contextReply());
    const obsolete = refresh(app, "append");
    await old.entered;
    await app.invalidate();
    app.epoch();
    await app.outsideRetry();
    const before = app.count("mapping");
    old.release();
    await old.finished;
    await obsolete;
    expect((await app.snapshot())["admitted"]).toBe(false);
    expect(app.count("mapping")).toBe(before);
    await refresh(app, "append");
    await expect.poll(sample(app, "admitted")).toBe(true);
    expect(app.wires.length).toBe(0);
  }));

for (const operation of ["projection", "validation"] as const)
  test(`old ${operation} and parent DTO cannot repopulate replacement provenance`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await app.open(revisionPath, true);
      await expect.poll(() => reportVisible(page)).toBe(true);
      await expect.poll(sample(app, "reportsReady")).toBe(true);
      const old = app.hold(operation, { json: savedReports()[operation] });
      await page
        .locator(
          operation === "projection"
            ? '[aria-labelledby="saved-explanation-title"]'
            : '[aria-labelledby="saved-validation-title"]',
        )
        .getByRole("button", { name: "Read again", exact: true })
        .click();
      await old.entered;
      await app.arm();
      app.identity(ids.foreignActor);
      await app.observe();
      await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
      expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
      await expect.poll(() => old.canceled).toBe(true);
      old.release();
      await old.finished;
      expect(await reportVisible(page)).toBe(false);
      expect(app.wires.length).toBe(0);
      app.identity();
      await app.outsideRetry();
      // Generic retry only re-admits status/context. It cannot restore the old parent/report.
      expect(await reportVisible(page)).toBe(false);
      const parentsBefore = app.count("revision"),
        reportsBefore = app.count(operation);
      const parent = app.hold("revision", { json: app.revision });
      const report = app.hold(operation, { json: savedReports()[operation] });
      await button(page, "Refresh catalog").click();
      await parent.entered;
      expect(app.count("revision")).toBeGreaterThan(parentsBefore);
      expect((await app.snapshot())["reportsReady"]).toBe(false);
      parent.release();
      await parent.finished;
      await report.entered;
      expect(app.count(operation)).toBeGreaterThan(reportsBefore);
      expect((await app.snapshot())["reportsReady"]).toBe(false);
      if (operation === "projection")
        expect(await reportVisible(page)).toBe(false);
      report.release();
      await report.finished;
      await expect.poll(sample(app, "reportsReady")).toBe(true);
      await expect.poll(() => reportVisible(page)).toBe(true);
      expect(parent.canceled || report.canceled).toBe(false);
      expect(app.wires.length).toBe(0);
    }));

for (const state of ["anonymous", "authenticationRequired"] as const)
  for (const feature of ["creation", "metadata", "upload", "append"] as const)
    test(`negative ${state} observation suspends private ${feature} recovery`, async ({
      app,
    }) =>
      guarded(async () => {
        await prepare(app, feature);
        await lose(app, feature);
        await app.arm();
        app.session = state;
        await app.observe();
        await expect.poll(sample(app, "kind")).toBe(state);
        expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
        expect((await app.snapshot())["canRecover"]).toBe(false);
        expect(await privateInvisible(app.page)).toBe(true);
        expect(app.wires.length).toBe(1);
        app.session = "authenticated";
        app.epoch();
        await app.outsideRetry();
        await refresh(app, feature);
        await expect.poll(sample(app, "unknown")).toBe(true);
        expect(app.wires.length).toBe(1);
      }));

test("a late page cannot restore its old row or next-page cursor", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open(`/catalog/organizations/${ids.organization}/projects`, true);
    await expect.poll(() => healthyProjectRow(page)).toBe(true);
    const old = app.hold("projects", {
      json: {
        projects: [{ ...app.project, name: "Owned delayed private row" }],
        nextCursor: "b3duZWQtZGVsYXllZC1jdXJzb3I",
      },
    });
    await button(page, "Refresh catalog").click();
    await old.entered;
    await app.arm();
    app.identity(ids.foreignActor);
    await app.observe();
    await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
    expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
    old.release();
    await old.finished;
    expect(
      await page.evaluate(
        () =>
          ![...document.querySelectorAll(".catalog-list a")].some(
            (node) =>
              node.getClientRects().length > 0 &&
              node.textContent?.includes("Owned delayed private row"),
          ),
      ),
    ).toBe(true);
    expect(
      await page
        .getByRole("link", { name: "Next page", exact: true })
        .isVisible(),
    ).toBe(false);
    app.identity();
    await app.outsideRetry();
    expect((await app.snapshot())["readReady"]).toBe(false);
    await button(page, "Refresh catalog").click();
    await expect.poll(() => healthyProjectRow(page)).toBe(true);
    expect(
      await page
        .getByRole("link", { name: "Next page", exact: true })
        .isVisible(),
    ).toBe(false);
    expect(app.wires.length).toBe(0);
  }));

test("a late saved-revision parent cannot restore its report admission", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open(revisionPath, true);
    await expect.poll(sample(app, "reportsReady")).toBe(true);
    const old = app.hold("revision", { json: app.revision });
    await button(page, "Refresh catalog").click();
    await old.entered;
    await app.arm();
    app.epoch();
    await app.observe();
    await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
    expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
    old.release();
    await old.finished;
    expect((await app.snapshot())["readReady"]).toBe(false);
    expect((await app.snapshot())["reportsReady"]).toBe(false);
    await app.outsideRetry();
    expect((await app.snapshot())["readReady"]).toBe(false);
    await button(page, "Refresh catalog").click();
    await expect.poll(sample(app, "reportsReady")).toBe(true);
    expect(app.wires.length).toBe(0);
  }));

for (const feature of ["metadata", "upload", "append"] as const)
  test(`current resource denial or unavailability retains private ${feature} uncertainty`, async ({
    app,
  }) =>
    guarded(async () => {
      await prepare(app, feature);
      await lose(app, feature);
      const resource: Kind = feature === "upload" ? "project" : "mapping";
      for (const code of [403, 404, 503]) {
        app.plans.set(
          resource,
          catalogRefused(
            code,
            code === 404
              ? "notFound"
              : code === 403
                ? "identityRejected"
                : "unavailable",
          ),
        );
        app.epoch();
        await app.outsideRetry();
        await refresh(app, feature);
        expect((await app.snapshot())["canRecover"]).toBe(false);
        expect(await privateInvisible(app.page)).toBe(true);
        await expect.poll(sample(app, "kind")).toBe("authenticated");
        expect(app.wires.length).toBe(1);
      }
      app.plans.delete(resource);
      await refresh(app, feature);
      await expect.poll(sample(app, "unknown")).toBe(true);
      expect(app.wires.length).toBe(1);
    }));

test("reloading loses a tab-only unknown original without resending it", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "creation");
    await lose(app, "creation");
    expect((await app.snapshot())["retained"]).toBe(true);
    const before = await page.evaluate(() =>
      (
        window as unknown as {
          __weft119StorageCounts(): Record<string, number>;
        }
      ).__weft119StorageCounts(),
    );
    expect(Object.values(before).every((value) => value === 0)).toBe(true);
    page.once("dialog", (dialog) => {
      void dialog.accept();
    });
    await page.reload();
    await expect(page.locator("#project-name")).toBeVisible();
    expect((await app.snapshot())["retained"]).toBe(false);
    expect((await app.snapshot())["unknown"]).toBe(false);
    expect(
      await page
        .locator("#project-name")
        .evaluate((node) => (node as HTMLInputElement).value.length === 0),
    ).toBe(true);
    expect(
      await page.evaluate(
        () => localStorage.length === 0 && sessionStorage.length === 0,
      ),
    ).toBe(true);
    expect(app.wires.length).toBe(1);
    // This is browser memory loss only; no assertion claims the first server request was rolled back.
  }));

test("metadata recovery explicitly reviews a distinct validator while preserving the earlier unknown warning", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await prepare(app, "metadata");
    await lose(app, "metadata");
    app.epoch();
    await app.outsideRetry();
    await refresh(app, "metadata");
    app.mapping.version = "5";
    app.mapping.name = "Owned distinct current baseline";
    app.mapping.description = "Owned distinct baseline description";
    const reads = app.count("metadata");
    await button(page, "Review latest").click();
    await expect.poll(() => app.count("metadata")).toBeGreaterThan(reads);
    await expect(page.locator("#metadata-review")).toBeVisible();
    expect((await app.snapshot())["unknown"]).toBe(true);
    expect(app.wires.length).toBe(1);
    await button(page, "Keep mine").click();
    app.plans.set("patch", {
      json: {
        ...app.metadata(),
        name: "Owned private draft",
        description: "Owned private description",
        version: "6",
      },
      headers: {
        ETag: `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:6"`,
      },
    });
    await button(page, "Save metadata").click();
    await expect.poll(sample(app, "acknowledged")).toBe(true);
    const first = app.wires[0],
      reviewed = app.wires[1];
    expect(app.wires.length).toBe(2);
    expect(
      first?.matchesCurrentValidator && !first.differsFromOriginalValidator,
    ).toBe(true);
    expect(
      reviewed?.matchesCurrentValidator &&
        reviewed.differsFromOriginalValidator &&
        reviewed.exactBody &&
        reviewed.freshCatalogToken &&
        reviewed.distinctAudiences &&
        reviewed.correctHeaders,
    ).toBe(true);
    expect((await app.snapshot())["latestSaveChanged"]).toBe(true);
    expect((await app.snapshot())["unknown"]).toBe(true);
  }));
