import {
  test,
  expect,
  refused,
  ids,
  browserPaths,
  mappingPath,
  projectPath,
  type ControlledSession,
  editField,
} from "./fixture";
import { inputIds, type Feature } from "./data";

const guarded = async (work: () => Promise<void>) => {
  try {
    await work();
  } catch (error) {
    if (
      error instanceof Error &&
      /^Controlled draft admission failed: busy=(true|false); preparingFile=(true|false); fileAbsent=(true|false); filenameAbsent=(true|false); needsReview=(true|false); admitted=(true|false); newDispatchAllowed=(true|false)\.$/.test(
        error.message,
      )
    )
      throw error;
    const stack =
      error instanceof Error && typeof error.stack === "string"
        ? error.stack.slice(0, 16384)
        : "";
    const frame = stack
      .split("\n")
      .find(
        (line) =>
          line.startsWith("    at ") &&
          /\/boundary\.spec\.ts:\d{1,5}:\d{1,4}\)?$/.test(line),
      );
    const location = frame?.match(/boundary\.spec\.ts:(\d{1,5}):(\d{1,4})\)?$/);
    throw new Error(
      `The controlled session boundary predicate failed at ${location ? `boundary.spec.ts:${location[1]}:${location[2]}` : "unavailable"}.`,
    );
  }
};
const sample = (app: ControlledSession, key: string) => async () =>
  (await app.snapshot())[key];
const submitName: Record<Feature, string> = {
  creation: "Create project",
  metadata: "Save metadata",
  upload: "Upload mapping",
  append: "Append revision",
};
const refreshName: Record<Feature, string> = {
  creation: "Refresh admission",
  metadata: "Refresh admission",
  upload: "Refresh access",
  append: "Refresh access",
};
async function validMountedDraft(
  app: ControlledSession,
  feature: Feature,
): Promise<void> {
  const page = app.page;
  await app.open(browserPaths[feature], true);
  await expect(page.locator("#" + inputIds[feature])).toBeVisible();
  app.draft(
    "Owned unsent retained value",
    "Owned private description",
    "owned +%.native.json",
  );
  if (feature === "append") {
    const before = app.count("context");
    await page
      .getByRole("button", { name: "Review current head", exact: true })
      .click();
    await expect.poll(() => app.count("context")).toBeGreaterThan(before);
    await expect
      .poll(async () => {
        const facts = await app.snapshot();
        return facts["busy"] === false && facts["admitted"] === true;
      })
      .toBe(true);
  } else {
    await editField(
      page,
      "#" + inputIds[feature],
      "Owned unsent retained value",
    );
    await editField(
      page,
      feature === "creation"
        ? "#project-description"
        : feature === "metadata"
          ? "#metadata-description"
          : "#upload-description",
      "Owned private description",
    );
  }
  if (feature === "upload" || feature === "append") {
    const fileInput = page.locator(
      feature === "upload" ? "#upload-file" : "#append-file",
    );
    await expect(fileInput).toBeEnabled();
    await fileInput.setInputFiles(app.file());
    await editField(
      page,
      feature === "upload" ? "#upload-filename" : "#append-filename",
      "owned +%.native.json",
    );
  }
  // Working actionable material is required before hidden synthetic submit can prove suppression.
  try {
    await expect(
      page.getByRole("button", { name: submitName[feature], exact: true }),
    ).toBeEnabled();
  } catch {
    const facts = await app.snapshot();
    const flags = [
      "busy",
      "preparingFile",
      "fileAbsent",
      "filenameAbsent",
      "needsReview",
      "admitted",
      "newDispatchAllowed",
    ]
      .map((key) => `${key}=${facts[key] === true}`)
      .join("; ");
    throw new Error(`Controlled draft admission failed: ${flags}.`);
  }
  await app.arm();
  await page.evaluate((id) => {
    const node = document.getElementById(id) as HTMLInputElement | null;
    if (!node) throw new Error("Mounted witness unavailable.");
    const descriptor = Object.getOwnPropertyDescriptor(
      HTMLInputElement.prototype,
      "value",
    )!;
    const initial = node.value;
    let writes = 0,
      changed = false,
      detached = false;
    Object.defineProperty(node, "value", {
      configurable: true,
      get: () => descriptor.get!.call(node),
      set: (value) => {
        ++writes;
        changed ||= value !== initial;
        descriptor.set!.call(node, value);
      },
    });
    const consume = (records: MutationRecord[]) => {
      for (const record of records)
        record.removedNodes.forEach((removed) => {
          if (removed === node || removed.contains(node)) detached = true;
        });
    };
    const observer = new MutationObserver(consume);
    observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
    });
    Object.defineProperty(window, "__weft119Mounted", {
      configurable: true,
      value: {
        inspect: () => {
          consume(observer.takeRecords());
          return {
            same: node.isConnected && document.getElementById(id) === node,
            value: node.value === initial,
            writes,
            changed,
            detached,
            inert: node.closest("[inert]") !== null,
            hidden:
              node.getClientRects().length === 0 &&
              node.closest('[aria-hidden="true"]') !== null,
            visible:
              node.getClientRects().length > 0 &&
              node.closest('[inert], [aria-hidden="true"], [hidden]') === null,
          };
        },
        attemptFocus: () => {
          node.focus();
          return document.activeElement !== node;
        },
        focused: () => document.activeElement === node,
        submit: () => node.form?.requestSubmit(),
      },
    });
  }, inputIds[feature]);
}
type Mounted = {
  __weft119Mounted: {
    inspect(): {
      same: boolean;
      value: boolean;
      writes: number;
      changed: boolean;
      detached: boolean;
      inert: boolean;
      hidden: boolean;
      visible: boolean;
    };
    attemptFocus(): boolean;
    focused(): boolean;
    submit(): void;
  };
};
/** A routine check keeps the mounted form visible and focusable; a boundary hides it. */
async function retainedMountedDraft(
  app: ControlledSession,
  presentation: "visible" | "hidden",
  submit = false,
): Promise<void> {
  const witness = await app.page.evaluate(
    ({ submit, presentation }) => {
      const witness = (window as unknown as Mounted).__weft119Mounted;
      const result = witness.inspect();
      const focus =
        presentation === "hidden"
          ? witness.attemptFocus()
          : !witness.attemptFocus() && witness.focused();
      if (submit) witness.submit();
      return { ...result, focus };
    },
    { submit, presentation },
  );
  const facts = await app.snapshot();
  expect(
    witness.same &&
      witness.value &&
      !witness.changed &&
      !witness.detached &&
      witness.writes === 0,
  ).toBe(true);
  if (presentation === "hidden")
    expect(witness.hidden && witness.inert && witness.focus).toBe(true);
  else {
    expect(witness.visible && !witness.inert && witness.focus).toBe(true);
    expect(facts["visible"]).toBe(true);
  }
  expect(facts["newDispatchAllowed"]).toBe(false);
}
async function restoredMountedDraft(
  app: ControlledSession,
  feature: Feature,
): Promise<void> {
  await expect.poll(sample(app, "busy")).toBe(false);
  await expect(app.page.locator("#" + inputIds[feature])).toBeVisible();
  await expect(
    app.page.getByRole("button", { name: submitName[feature], exact: true }),
  ).toBeEnabled();
  expect(
    await app.page.evaluate(() => {
      const value = (
        window as unknown as {
          __weft119Mounted: {
            inspect(): {
              same: boolean;
              value: boolean;
              writes: number;
              changed: boolean;
              detached: boolean;
            };
          };
        }
      ).__weft119Mounted.inspect();
      return (
        value.same &&
        value.value &&
        value.writes === 0 &&
        !value.changed &&
        !value.detached
      );
    }),
  ).toBe(true);
  expect((await app.snapshot())["notices"]).toBe(0);
  expect(app.wires.length).toBe(0);
}

test("startup status gates protected work without guessing Development from errors", async ({
  app,
  page,
}) =>
  guarded(async () => {
    const held = app.hold("status", refused(503));
    await app.open(projectPath);
    await held.entered;
    expect(app.count("context")).toBe(0);
    expect(app.count("project")).toBe(0);
    expect(app.wires.length).toBe(0);
    expect(await page.locator(".catalog-context").isVisible()).toBe(false);
    held.release();
    await held.finished;
    expect(app.count("context")).toBe(0);
    expect(app.authPosts).toBe(0);
  }));

test("public snapshots acknowledge the first and each overlapping requested publication", async ({
  app,
}) => {
  let phase = "open";
  try {
    await app.open("/__state", true);
    phase = "first-publication";
    const first = await app.snapshot();
    expect(first["requestedSamples"]).toBe(1);
    expect(first["kind"]).toBe("checking");
    expect(first["completed"]).toBe(0);
    expect(first["failed"]).toBe(0);
    phase = "overlapping-publications";
    const overlapping = await Promise.all([
      app.snapshot(),
      app.snapshot(),
      app.snapshot(),
    ]);
    expect(overlapping.map((value) => value["requestedSamples"])).toEqual([
      2, 3, 4,
    ]);
    for (const value of overlapping) {
      expect(Number.isFinite(value["completed"])).toBe(true);
      expect(Number.isFinite(value["failed"])).toBe(true);
      expect(value["kind"]).toBe("checking");
    }
    phase = "changed-publication";
    await app.invalidate();
    const changed = await app.snapshot();
    expect(changed["requestedSamples"]).toBe(5);
    expect(changed["kind"]).toBe("unavailable");
    phase = "reopened-publication";
    await app.open("/__state", true);
    const reopened = await app.snapshot();
    expect(reopened["requestedSamples"]).toBe(1);
    expect(reopened["completed"]).toBe(0);
    expect(reopened["failed"]).toBe(0);
    phase = "protected-counts";
    expect(app.count("status")).toBe(0);
    expect(app.count("context")).toBe(0);
    expect(app.wires.length).toBe(0);
  } catch {
    throw new Error(
      `The controlled sample-publication predicate failed: ${phase}.`,
    );
  }
});

for (const mode of ["disabled", "development"] as const)
  test(`explicit closed bootstrap mode ${mode} is a configuration observation`, async ({
    app,
  }) =>
    guarded(async () => {
      app.mode = mode;
      await app.open("/__state", true);
      await app.observe();
      await expect.poll(sample(app, "kind")).toBe(mode);
      expect(app.wires.length).toBe(0);
      expect(app.authPosts).toBe(0);
      if (mode === "disabled") {
        await app.outsideRetry();
        expect(app.count("context")).toBe(0);
      } else {
        await app.outsideRetry();
        await expect.poll(() => app.count("context")).toBe(1);
      }
    }));

const malformed: ReadonlyArray<{
  name: string;
  mutate: (value: Record<string, unknown>) => unknown;
}> = [
  {
    name: "missing-display-name",
    mutate: ({ displayName: _, ...rest }) => rest,
  },
  {
    name: "wrong-display-name-type",
    mutate: (value) => ({ ...value, displayName: 1 }),
  },
  {
    name: "oversized-display-name",
    mutate: (value) => ({ ...value, displayName: "a".repeat(201) }),
  },
  {
    name: "bidi-display-name",
    mutate: (value) => ({ ...value, displayName: "a\u202eb" }),
  },
  {
    name: "malformed-display-name",
    mutate: (value) => ({ ...value, displayName: "a\ud800" }),
  },
  { name: "missing-version", mutate: ({ version: _, ...rest }) => rest },
  { name: "future-version", mutate: (value) => ({ ...value, version: 2 }) },
  {
    name: "wrong-version-type",
    mutate: (value) => ({ ...value, version: "1" }),
  },
  { name: "wrong-mode-type", mutate: (value) => ({ ...value, mode: null }) },
  { name: "wrong-state-type", mutate: (value) => ({ ...value, state: 1 }) },
  {
    name: "wrong-action-token-type",
    mutate: (value) => ({ ...value, authActionRequestToken: 1 }),
  },
  {
    name: "wrong-epoch-token-type",
    mutate: (value) => ({ ...value, requestToken: [] }),
  },
  {
    name: "wrong-fingerprint-type",
    mutate: (value) => ({ ...value, identityContext: {} }),
  },
  {
    name: "unknown-mode",
    mutate: (value) => ({ ...value, mode: "providerFallback" }),
  },
  {
    name: "unknown-state",
    mutate: (value) => ({ ...value, state: "success" }),
  },
  {
    name: "missing-action-token",
    mutate: ({ authActionRequestToken: _, ...rest }) => rest,
  },
  {
    name: "missing-epoch-token",
    mutate: ({ requestToken: _, ...rest }) => rest,
  },
  {
    name: "missing-fingerprint",
    mutate: ({ identityContext: _, ...rest }) => rest,
  },
  {
    name: "authenticated-null",
    mutate: (value) => ({
      ...value,
      requestToken: null,
      identityContext: null,
    }),
  },
  {
    name: "negative-positive-fields",
    mutate: (value) => ({ ...value, state: "anonymous" }),
  },
  {
    name: "empty-token",
    mutate: (value) => ({ ...value, authActionRequestToken: "" }),
  },
  {
    name: "overbound-token",
    mutate: (value) => ({ ...value, requestToken: "A".repeat(4097) }),
  },
  {
    name: "token-control",
    mutate: (value) => ({ ...value, requestToken: "owned\u0000marker" }),
  },
  {
    name: "token-whitespace",
    mutate: (value) => ({ ...value, authActionRequestToken: " owned " }),
  },
  {
    name: "fingerprint-prefix",
    mutate: (value) => ({ ...value, identityContext: "v2." + "A".repeat(43) }),
  },
  {
    name: "fingerprint-length",
    mutate: (value) => ({ ...value, identityContext: "v1." + "A".repeat(42) }),
  },
  {
    name: "fingerprint-padding",
    mutate: (value) => ({
      ...value,
      identityContext: "v1." + "A".repeat(42) + "=",
    }),
  },
  {
    name: "fingerprint-noncanonical-bits",
    mutate: (value) => ({
      ...value,
      identityContext: "v1." + "A".repeat(42) + "B",
    }),
  },
  {
    name: "unexpected-role",
    mutate: (value) => ({ ...value, role: "administrator" }),
  },
  {
    name: "development-extra-token",
    mutate: (value) => ({
      version: 1,
      mode: "development",
      requestToken: value["requestToken"],
    }),
  },
  { name: "array", mutate: () => [] },
  { name: "null", mutate: () => null },
];
for (const variant of malformed)
  test(`closed status schema refuses ${variant.name}`, async ({ app }) =>
    guarded(async () => {
      app.plans.set("status", {
        json: variant.mutate(app.status() as Record<string, unknown>),
      });
      await app.open("/__state", true);
      await app.observe();
      await expect.poll(sample(app, "kind")).toBe("unavailable");
      expect((await app.snapshot())["visible"]).toBe(false);
      expect(app.count("context")).toBe(0);
      expect(app.wires.length).toBe(0);
    }));

for (const code of [401, 404, 503])
  test(`bootstrap HTTP ${code} is unavailable without mode fallback`, async ({
    app,
  }) =>
    guarded(async () => {
      app.plans.set("status", refused(code));
      await app.open("/__state", true);
      await app.observe();
      await expect.poll(sample(app, "kind")).toBe("unavailable");
      expect(app.count("context")).toBe(0);
    }));
test("malformed provider-shaped bootstrap JSON is closed without response text", async ({
  app,
}) =>
  guarded(async () => {
    app.plans.set("status", {
      body: '{"private_provider_detail":"owned-marker"',
    });
    await app.open("/__state", true);
    await app.observe();
    await expect.poll(sample(app, "kind")).toBe("unavailable");
    expect(app.count("context")).toBe(0);
    expect(
      await app.page.evaluate(
        () => !document.body.textContent?.includes("owned-marker"),
      ),
    ).toBe(true);
  }));

for (const state of ["anonymous", "authenticationRequired"] as const)
  test(`session ${state} is no catalog authority`, async ({ app }) => {
    let phase = "open";
    try {
      app.session = state;
      await app.open("/__state", true);
      phase = "context-admission";
      await app.outsideRetry();
      phase = "terminal-progress";
      const terminal = await app.snapshot();
      expect(terminal["completed"]).toBe(0);
      expect(terminal["failed"]).toBe(1);
      expect(app.count("status")).toBe(1);
      phase = "categorical-state";
      await expect.poll(sample(app, "kind")).toBe(state);
      phase = "context-count";
      expect(app.count("context")).toBe(0);
      phase = "mutation-count";
      expect(app.wires.length).toBe(0);
    } catch {
      let category = "unread";
      let completed = -1;
      let failed = -1;
      try {
        const observed = await app.snapshot();
        if (
          [
            "checking",
            "disabled",
            "development",
            "anonymous",
            "authenticationRequired",
            "authenticated",
            "unavailable",
          ].includes(String(observed["kind"]))
        )
          category = String(observed["kind"]);
        if (
          typeof observed["completed"] === "number" &&
          Number.isSafeInteger(observed["completed"])
        )
          completed = observed["completed"];
        if (
          typeof observed["failed"] === "number" &&
          Number.isSafeInteger(observed["failed"])
        )
          failed = observed["failed"];
      } catch {
        /* Keep diagnostics closed if the public probe is unavailable. */
      }
      throw new Error(
        `The controlled negative-session predicate failed: phase=${phase}; kind=${category}; completed=${completed}; failed=${failed}; statusReads=${app.count("status")}; contextReads=${app.count("context")}.`,
      );
    }
  });

test("only overlapping observations share; canceled waiter is not settled authority", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open("/__state", true);
    const held = app.hold("status", { json: app.status() });
    await app.observe();
    await held.entered;
    await app.observe();
    expect(app.count("status")).toBe(1);
    await page.locator("#probe-cancel-first").click();
    expect(held.canceled).toBe(false);
    held.release();
    await held.finished;
    await expect.poll(sample(app, "kind")).toBe("authenticated");
    const completed = Number((await app.snapshot())["completed"]);
    await app.observe();
    await expect.poll(() => app.count("status")).toBe(2);
    await expect.poll(sample(app, "completed")).toBeGreaterThan(completed);
    const generation = (await app.snapshot())["generation"];
    // The canceled routine check would have observed sign-out; it must never publish.
    app.session = "anonymous";
    const final = app.hold("status", { json: app.status() });
    app.session = "authenticated";
    await app.observe();
    await final.entered;
    expect((await app.snapshot())["checking"]).toBe(true);
    await page.locator("#probe-cancel-all").click();
    await expect.poll(() => final.canceled).toBe(true);
    final.release();
    await final.finished;
    // Canceling observed nothing: the confirmed state before the check stands.
    const canceled = await app.snapshot();
    expect(canceled["kind"]).toBe("authenticated");
    expect(canceled["visible"]).toBe(true);
    expect(canceled["checking"]).toBe(false);
    expect(canceled["generation"]).toBe(generation);
    await app.observe();
    await expect.poll(() => app.count("status")).toBe(4);
  }));

test("late superseded status cannot restore a positive observation", async ({
  app,
}) =>
  guarded(async () => {
    await app.open("/__state", true);
    const old = app.hold("status", { json: app.status() });
    await app.observe();
    await old.entered;
    await app.invalidate();
    app.session = "anonymous";
    await app.observe();
    await expect.poll(sample(app, "kind")).toBe("anonymous");
    old.release();
    await old.finished;
    expect((await app.snapshot())["kind"]).toBe("anonymous");
    expect(app.wires.length).toBe(0);
  }));

test("session context mismatch fails the initiating admission and explicit retry is separate", async ({
  app,
}) =>
  guarded(async () => {
    const mismatch = app.contextReply();
    app.epoch();
    app.plans.set("context", mismatch);
    await app.open("/__state", true);
    await app.outsideRetry();
    await expect.poll(sample(app, "failed")).toBe(1);
    expect(app.count("project")).toBe(0);
    expect(app.wires.length).toBe(0);
    app.plans.delete("context");
    await app.outsideRetry();
    await expect.poll(sample(app, "context")).toBe(true);
    expect(app.count("project")).toBe(0);
  }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`routine checking keeps the same private mounted ${feature} form visible and blocks its dispatch`, async ({
    app,
  }) =>
    guarded(async () => {
      await validMountedDraft(app, feature);
      const held = app.hold("status", () => ({ json: app.status() }));
      await app.observe();
      await held.entered;
      await retainedMountedDraft(app, "visible", true);
      const checking = await app.snapshot();
      expect(checking["kind"]).toBe("authenticated");
      expect(checking["checking"]).toBe(true);
      expect(checking["accepts"]).toBe(true);
      expect(checking["canDispatch"]).toBe(false);
      expect(
        await app.page
          .getByText("Checking workspace access…", { exact: true })
          .count(),
      ).toBe(0);
      expect(app.wires.length).toBe(0);
      held.release();
      await held.finished;
      await expect.poll(sample(app, "kind")).toBe("authenticated");
      await restoredMountedDraft(app, feature);
    }));

test("a routine re-read keeps the catalog page attached, visible and focus-stable", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open(projectPath, true);
    await expect.poll(sample(app, "readReady")).toBe(true);
    await expect(page.locator(".catalog-summary")).toBeVisible();
    await page.evaluate(() => {
      const title = document.getElementById("catalog-page-title")!,
        summary = document.querySelector(".catalog-summary")!;
      let detached = false;
      const observer = new MutationObserver((records) => {
        for (const record of records)
          record.removedNodes.forEach((removed) => {
            if (removed.contains(title) || removed.contains(summary))
              detached = true;
          });
      });
      observer.observe(document.documentElement, {
        childList: true,
        subtree: true,
      });
      title.focus();
      Object.defineProperty(window, "__weft324Page", {
        configurable: true,
        value: () =>
          !detached &&
          title.isConnected &&
          summary.isConnected &&
          document.activeElement === title &&
          [title, summary].every(
            (node) =>
              node.getClientRects().length > 0 &&
              node.closest('[inert], [aria-hidden="true"], [hidden]') === null,
          ) &&
          !document.body.textContent?.includes("Checking workspace access"),
      });
    });
    const stable = () =>
      page.evaluate(() =>
        (window as unknown as { __weft324Page(): boolean }).__weft324Page(),
      );
    expect(await stable()).toBe(true);
    const contexts = app.count("context");
    const status = app.hold("status", () => ({ json: app.status() }));
    const resource = app.hold("project", { json: app.project });
    // Returning to the tab reads the displayed page again.
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await status.entered;
    expect(await stable()).toBe(true);
    await expect(page.locator("section.catalog-page")).toHaveAttribute(
      "aria-busy",
      "true",
    );
    status.release();
    await status.finished;
    await resource.entered;
    expect(await stable()).toBe(true);
    resource.release();
    await resource.finished;
    await expect(page.locator("section.catalog-page")).not.toHaveAttribute(
      "aria-busy",
      "true",
    );
    expect(await stable()).toBe(true);
    expect(app.count("context")).toBe(contexts + 1);
    expect((await app.snapshot())["readReady"]).toBe(true);
    expect(app.wires.length).toBe(0);
  }));

test("a save waits for its own check, shows it on the Save control and is sent once", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await validMountedDraft(app, "metadata");
    app.plans.set("patch", {
      json: {
        ...app.metadata(),
        name: "Owned unsent retained value",
        description: "Owned private description",
        version: "2",
      },
      headers: {
        ETag: `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:2"`,
      },
    });
    const save = page.getByRole("button", {
      name: "Save metadata",
      exact: true,
    });
    const statuses = app.count("status"),
      contexts = app.count("context");
    const status = app.hold("status", () => ({ json: app.status() }));
    await save.click();
    await status.entered;
    await expect(save).toHaveAttribute("aria-busy", "true");
    // The form stays on screen; a second submit while the check runs sends nothing.
    await retainedMountedDraft(app, "visible", true);
    await page.evaluate(() =>
      document
        .querySelector<HTMLButtonElement>("button.metadata-primary")
        ?.click(),
    );
    expect(app.count("status")).toBe(statuses + 1);
    expect(app.count("context")).toBe(contexts);
    expect(app.count("patch")).toBe(0);
    status.release();
    await status.finished;
    await expect(page.locator("#metadata-saved")).toBeVisible();
    await expect(save).not.toHaveAttribute("aria-busy", "true");
    expect(app.count("patch")).toBe(1);
    expect(app.count("context")).toBe(contexts + 1);
    expect(app.wires.length).toBe(1);
    expect(app.wires[0]?.freshCatalogToken).toBe(true);
  }));

for (const feature of ["creation", "metadata", "upload", "append"] as const)
  test(`actual ${feature} child refresh preserves the form through complete fresh admission`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await validMountedDraft(app, feature);
      const status = app.hold("status", () => ({ json: app.status() }));
      const context = app.hold("context", () => app.contextReply());
      const resource =
        feature === "creation"
          ? null
          : app.hold(feature === "upload" ? "project" : "mapping", {
              json: feature === "upload" ? app.project : app.mapping,
            });
      await page
        .getByRole("button", { name: refreshName[feature], exact: true })
        .click();
      await status.entered;
      await retainedMountedDraft(app, "visible", true);
      expect(app.wires.length).toBe(0);
      status.release();
      await status.finished;
      await context.entered;
      await retainedMountedDraft(app, "visible");
      expect(app.wires.length).toBe(0);
      context.release();
      await context.finished;
      if (resource) {
        await resource.entered;
        await retainedMountedDraft(app, "visible");
        expect(app.wires.length).toBe(0);
        resource.release();
        await resource.finished;
      }
      await restoredMountedDraft(app, feature);
    }));

for (const [label, path] of [
  ["project", projectPath],
  ["mapping", mappingPath],
  ...Object.entries(browserPaths),
] as const)
  test(`true invalidation clears the actual mounted ${label} consumer`, async ({
    app,
  }) =>
    guarded(async () => {
      await app.open(path, true);
      const consumer = app.page.locator(
        label in inputIds
          ? "#" + inputIds[label as Feature]
          : "#catalog-page-title",
      );
      await expect(consumer).toBeVisible();
      if (!(label in inputIds))
        expect(
          await consumer.evaluate(
            (node, kind) =>
              node.textContent?.trim() ===
              (kind === "project"
                ? "Owned synthetic project"
                : "Owned synthetic mapping"),
            label,
          ),
        ).toBe(true);
      await app.arm();
      expect((await app.snapshot())["actualOwner"]).toBe(true);
      app.identity(ids.foreignActor);
      const held = app.hold("status", () => ({ json: app.status() }));
      await app.observe();
      await held.entered;
      // Until the changed identity is observed, the check keeps the consumer on screen.
      await expect(consumer).toBeVisible();
      const checking = await app.snapshot();
      expect(checking["checking"]).toBe(true);
      expect(checking["notices"]).toBe(0);
      held.release();
      await held.finished;
      await expect.poll(sample(app, "notices")).toBeGreaterThan(0);
      expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
      expect(app.wires.length).toBe(0);
    }));

test("protected persistence uses no browser storage and reloading loses tab-only recovery", async ({
  app,
  page,
}) =>
  guarded(async () => {
    await app.open(browserPaths.creation, true);
    await expect(page.locator("#project-name")).toBeVisible();
    await editField(page, "#project-name", "Owned memory-only draft");
    const storage = await page.evaluate(() =>
      (
        window as unknown as {
          __weft119StorageCounts(): Record<string, number>;
        }
      ).__weft119StorageCounts(),
    );
    expect(Object.values(storage).every((value) => value === 0)).toBe(true);
    expect(
      await page.evaluate(
        () => localStorage.length === 0 && sessionStorage.length === 0,
      ),
    ).toBe(true);
    page.once("dialog", (dialog) => {
      void dialog.accept();
    });
    await page.reload();
    await expect(page.locator("#project-name")).toBeVisible();
    expect(
      await page
        .locator("#project-name")
        .evaluate((node) => (node as HTMLInputElement).value.length === 0),
    ).toBe(true);
  }));

test("bootstrap transport failure is unavailable without a guessed mode", async ({
  app,
}) =>
  guarded(async () => {
    app.plans.set("status", { abort: true });
    await app.open("/__state", true);
    await app.observe();
    await expect.poll(sample(app, "kind")).toBe("unavailable");
    expect(app.count("context")).toBe(0);
    expect(app.wires.length).toBe(0);
  }));

test("status deadline cancels its original held request without admitting protected work", async ({
  app,
}) =>
  guarded(async () => {
    const held = app.hold("status", { json: app.status() });
    await app.open("/__state", true);
    await app.observe();
    await held.entered;
    await expect.poll(sample(app, "kind")).toBe("unavailable");
    await expect.poll(() => held.canceled).toBe(true);
    held.release();
    await held.finished;
    expect(app.count("context")).toBe(0);
    expect(app.wires.length).toBe(0);
  }));

for (const variant of ["missing-fingerprint", "development-context"] as const)
  test(`a positive session cannot adopt ${variant} as catalog authority`, async ({
    app,
  }) =>
    guarded(async () => {
      const original = app.contextReply().json as Record<string, unknown>;
      const { identityContext: _, ...withoutFingerprint } = original;
      app.plans.set("context", {
        json:
          variant === "missing-fingerprint"
            ? withoutFingerprint
            : { ...original, mode: "development" },
      });
      await app.open("/__state", true);
      await app.outsideRetry();
      expect((await app.snapshot())["context"]).toBe(false);
      expect((await app.snapshot())["failed"]).toBe(1);
      expect(app.count("project")).toBe(0);
      expect(app.wires.length).toBe(0);
      app.plans.delete("context");
      await app.outsideRetry();
      await expect.poll(sample(app, "context")).toBe(true);
    }));

test("the public sign-in page remains reachable while protected status is unavailable", async ({
  app,
  page,
}) =>
  guarded(async () => {
    app.plans.set("status", refused(503));
    await app.open("/sign-in");
    await expect.poll(() => page.title()).toBe("Sign in · Weft");
    await expect(
      page.getByRole("button", { name: "Check again", exact: true }),
    ).toBeVisible();
    expect(app.count("context")).toBe(0);
    expect(app.wires.length).toBe(0);
    expect(app.authPosts).toBe(0);
  }));

for (const owner of [
  "generic",
  "creation",
  "metadata",
  "upload",
  "append",
] as const)
  test(`actual ${owner} owner fences old queued focus and admits a new healthy coalesced event`, async ({
    app,
    page,
  }) =>
    guarded(async () => {
      await app.open(
        owner === "generic" ? projectPath : browserPaths[owner],
        true,
      );
      if (owner === "generic")
        await expect.poll(sample(app, "readReady")).toBe(true);
      else await expect(page.locator("#" + inputIds[owner])).toBeVisible();
      await app.arm();
      expect((await app.snapshot())["actualOwner"]).toBe(true);
      const resource =
        owner === "creation"
          ? null
          : owner === "generic" || owner === "upload"
            ? "project"
            : "mapping";
      const completeHealthyFocus = async () => {
        const before = app.count("status"),
          contexts = app.count("context"),
          resources = resource ? app.count(resource) : 0;
        const held = app.hold("status", () => ({ json: app.status() }));
        const currentResource = resource
          ? app.hold(resource, {
              json: resource === "project" ? app.project : app.mapping,
            })
          : null;
        await page.evaluate(() => {
          window.dispatchEvent(new Event("focus"));
          window.dispatchEvent(new Event("focus"));
          document.dispatchEvent(new Event("visibilitychange"));
        });
        await held.entered;
        expect(app.count("status") - before).toBe(1);
        held.release();
        await held.finished;
        await expect.poll(() => app.count("context")).toBe(contexts + 1);
        if (currentResource && resource) {
          await currentResource.entered;
          expect(app.count(resource)).toBe(resources + 1);
          expect((await app.snapshot())["busy"]).toBe(true);
          currentResource.release();
          await currentResource.finished;
        }
        await expect
          .poll(sample(app, owner === "generic" ? "readReady" : "admitted"))
          .toBe(true);
        await expect.poll(sample(app, "busy")).toBe(false);
        await page.waitForTimeout(300);
        expect(app.count("status") - before).toBe(1);
        expect(app.count("context") - contexts).toBe(1);
      };
      await completeHealthyFocus();
      const settled = app.order.length,
        wires = app.wires.length;
      await page.evaluate(() => {
        window.dispatchEvent(new Event("focus"));
        document.dispatchEvent(new Event("visibilitychange"));
        (
          document.getElementById("probe-invalidate") as HTMLButtonElement
        ).click();
      });
      await page.waitForTimeout(300);
      expect(app.order.length).toBe(settled);
      expect(app.wires.length).toBe(wires);
      expect((await app.snapshot())["clearedSynchronously"]).toBe(true);
      await completeHealthyFocus();
      expect(app.wires.length).toBe(wires);
    }));
