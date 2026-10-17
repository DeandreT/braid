import type { Page, Response } from "@playwright/test";
import {
  test,
  expect,
  ControlledAppend,
  type Reply,
  type Receipt,
  synthetic,
  catalogApi,
  mappingPath,
  appendPath,
  otherActor,
  otherOrganization,
  otherProjectId,
  otherMappingId,
  originalBytes,
  replacementBytes,
  codeReply,
} from "./native-revision-append-controlled.fixture";

// Synthetic UI boundaries only. Real storage/worker/equal-head publication and
// acknowledgement-loss recovery are independently qualified by the actual lane.
// Root's F config must disable traces, screenshots, video and DOM attachments.
const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
const filenameField = (page: Page) =>
  page.getByRole("textbox", { name: "Saved filename", exact: true });
const fileField = (page: Page) =>
  page.getByLabel("Native mapping file", { exact: true });
const summary = (page: Page) =>
  page.getByRole("alert", {
    name: "Append fields need attention",
    exact: true,
  });
async function privateAction(action: () => Promise<unknown>): Promise<void> {
  try {
    await action();
  } catch {
    throw new Error("A controlled append input action did not complete.");
  }
}
async function body(response: Promise<Response>): Promise<void> {
  let nonempty: boolean;
  try {
    nonempty = (await (await response).body()).length > 0;
  } catch {
    throw new Error("A controlled append response body could not be consumed.");
  }
  expect(nonempty, "A controlled append response body was empty.").toBe(true);
}
async function ready(page: Page, fixture: ControlledAppend): Promise<void> {
  await page.goto(appendPath(fixture.scope));
  await expect(heading(page, "Upload revision")).toBeVisible();
  await expect(fileField(page)).toBeEnabled();
  await expect(heading(page, "Reviewed head for this append")).toBeVisible();
}
async function prepare(
  page: Page,
  fixture: ControlledAppend,
  filename = "original.native.json",
  bytes = originalBytes(),
  mimeType = "application/json",
): Promise<void> {
  fixture.draft(filename);
  await privateAction(() =>
    fileField(page).setInputFiles(
      fixture.file("selected.native.json", bytes, mimeType),
    ),
  );
  await privateAction(() => filenameField(page).fill(filename));
  await expect(button(page, "Append revision")).toBeEnabled();
}
async function dispatch(
  page: Page,
  fixture: ControlledAppend,
  status: number,
  label = "Append revision",
): Promise<void> {
  const response = fixture.response("append", status);
  await button(page, label).click();
  await body(response);
}
async function refresh(
  page: Page,
  fixture: ControlledAppend,
  status = 200,
): Promise<void> {
  const response = fixture.response("context", status);
  await button(page, "Refresh access").click();
  await body(response);
  await expect(button(page, "Refresh access")).toBeEnabled();
}
async function unknown(page: Page): Promise<void> {
  await expect(heading(page, "Append outcome is unknown")).toBeVisible();
}
async function saved(page: Page, invalid = false): Promise<void> {
  await expect(
    heading(
      page,
      invalid ? "Revision saved with validation issues" : "Revision saved",
    ),
  ).toBeVisible();
  await expect(button(page, "Retry revision access")).toBeEnabled();
  await expect(button(page, "Replay append result")).toHaveCount(0);
  await expect(button(page, "Resume append")).toHaveCount(0);
}
async function loseOriginal(
  page: Page,
  fixture: ControlledAppend,
): Promise<void> {
  fixture.appendReply = { abort: true };
  const failed = fixture.failedWrite();
  await button(page, "Append revision").click();
  await failed;
  await unknown(page);
  await expect(button(page, "Resend original append")).toBeEnabled();
}
function wires(fixture: ControlledAppend, count: number): void {
  expect(fixture.appendCount).toBe(count);
  expect(
    fixture.wires.every((wire) => wire.exactBytes),
    "Native byte identity differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.exactQuery),
    "The frozen encoded append query differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.equalHeadOnly),
    "An append did not preserve its exact reviewed-head condition.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.correctHeaders),
    "Native append transport headers differ.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.freshToken),
    "A deliberate mutation did not use fresh admission.",
  ).toBe(true);
}
function sameOriginal(fixture: ControlledAppend): void {
  expect(
    fixture.wires.every((wire) => wire.sameOriginalBytes),
    "Recovery changed the original native bytes.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.sameOriginalQuery),
    "Recovery changed the original equal-head query.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.sameOriginalKey),
    "Recovery changed the original request key.",
  ).toBe(true);
}
async function unloadWarning(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
}
async function filenameMatches(page: Page, value: string): Promise<boolean> {
  return filenameField(page).evaluate(
    (element, expected) => (element as HTMLInputElement).value === expected,
    value,
  );
}
async function factMatches(
  page: Page,
  id: string,
  label: string,
  value: string,
): Promise<boolean> {
  return page
    .locator(`#${id}`)
    .locator("xpath=..")
    .evaluate(
      (element, expected) => {
        const label = [...element.querySelectorAll("dt")].find(
          (node) => node.textContent?.trim() === expected.label,
        );
        return label?.nextElementSibling?.textContent === expected.value;
      },
      { label, value },
    );
}
async function controlled(
  page: Page,
  run: (fixture: ControlledAppend) => Promise<void>,
): Promise<void> {
  const fixture = new ControlledAppend(page);
  await fixture.install();
  try {
    await run(fixture);
  } finally {
    fixture.release();
    expect(fixture.unexpectedRequests).toBe(0);
  }
}
async function isolated(
  parent: Page,
  run: (page: Page, fixture: ControlledAppend) => Promise<void>,
): Promise<void> {
  const page = await parent.context().newPage();
  try {
    await controlled(page, (fixture) => run(page, fixture));
  } finally {
    await page.close();
  }
}
async function keyCalls(page: Page): Promise<number> {
  return page.evaluate(
    () =>
      (window as unknown as { __appendKeyCalls: { calls: number } })
        .__appendKeyCalls.calls,
  );
}
async function probeKeys(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const counts = { calls: 0 };
    Object.defineProperty(window, "__appendKeyCalls", { value: counts });
    const uuid = crypto.randomUUID.bind(crypto),
      random = crypto.getRandomValues.bind(crypto);
    Object.defineProperty(crypto, "randomUUID", {
      configurable: true,
      value: () => {
        ++counts.calls;
        return uuid();
      },
    });
    Object.defineProperty(crypto, "getRandomValues", {
      configurable: true,
      value: <T extends Exclude<BufferSource, ArrayBuffer>>(array: T): T => {
        ++counts.calls;
        return random(array);
      },
    });
  });
}

test("controlled revision append: full scope and frontend selectors are rejected before scoped reads", async ({
  page,
}) => {
  const scope = synthetic.mapping.scope;
  const invalid = [
    appendPath({
      ...scope,
      organizationId: "00000000-0000-0000-0000-000000000000",
    }),
    appendPath({ ...scope, projectId: "invalid" }),
    appendPath({ ...scope, mappingId: "30000000-0000-4000-8000-00000000000A" }),
    appendPath(scope) + "?head=any",
    appendPath(scope) + "?operationId=50000000-0000-4000-8000-000000000088",
    appendPath(scope) + "?cursor=a&cursor=b",
  ];
  for (const path of invalid)
    await isolated(page, async (current, fixture) => {
      await current.goto(path);
      await expect(heading(current, "Append form unavailable")).toBeVisible();
      await expect(fileField(current)).toHaveCount(0);
      expect(fixture.mappingReads).toBe(0);
      expect(fixture.appendCount).toBe(0);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
    });
});

test("controlled revision append: view, upload feature, target action and persisted backend are independent gates", async ({
  page,
}) => {
  const gates: ((fixture: ControlledAppend) => void)[] = [
    (fixture) => {
      fixture.context.features.uploadRevisions = false;
    },
    (fixture) => {
      fixture.mapping.access.actions.uploadRevisions = false;
    },
    (fixture) => {
      fixture.context.features.readCatalog = false;
    },
    (fixture) => {
      fixture.mapping.access.actions.view = false;
    },
    (fixture) => {
      fixture.mapping.access.role = "viewer";
      fixture.mapping.access.actions.uploadRevisions = false;
    },
    (fixture) => {
      fixture.mapping.access.role = "runner";
      fixture.mapping.access.actions.uploadRevisions = false;
    },
    (fixture) => {
      fixture.mapping.backendId = "another-engine";
    },
    (fixture) => {
      fixture.mappingReply = {
        json: { ...fixture.mapping, scope: fixture.otherScope },
      };
    },
    (fixture) => {
      fixture.mapping.head.scope.projectId = otherProjectId;
    },
    (fixture) => {
      fixture.mapping.head.sequence = "9223372036854775808";
    },
    (fixture) => {
      fixture.contextReply = { json: { ...fixture.context, requestToken: "" } };
    },
  ];
  for (const change of gates)
    await isolated(page, async (current, fixture) => {
      change(fixture);
      await current.goto(appendPath(fixture.scope));
      await expect(heading(current, "Append form unavailable")).toBeVisible();
      await expect(fileField(current)).toHaveCount(0);
      await expect(button(current, "Refresh access")).toBeEnabled();
      expect(fixture.appendCount).toBe(0);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
    });
  for (const change of [
    gates[0]!,
    gates[1]!,
    (fixture: ControlledAppend) => {
      fixture.context.actorPrincipalId = otherActor;
    },
  ])
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      change(fixture);
      const fresh = fixture.response("context", 200);
      await button(current, "Append revision").click();
      await body(fresh);
      await expect(button(current, "Refresh access")).toBeEnabled();
      expect(fixture.appendCount).toBe(0);
      expect(fixture.resumeCount).toBe(0);
    });
});

test("controlled revision append: changed initial head retains the draft and allocates no key until explicit review", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await probeKeys(page);
    await ready(page, fixture);
    await prepare(page, fixture, "reviewed +%&'().native.json");
    expect(await keyCalls(page)).toBe(0);
    fixture.advanceHead();
    const fresh = fixture.response("mapping", 200);
    await button(page, "Append revision").click();
    await body(fresh);
    await expect(button(page, "Review current head")).toBeEnabled();
    await expect(heading(page, "Reviewed head for this append")).toBeFocused();
    expect(await keyCalls(page)).toBe(0);
    expect(fixture.appendCount).toBe(0);
    expect(await filenameMatches(page, "reviewed +%&'().native.json")).toBe(
      true,
    );
    expect(
      await factMatches(
        page,
        "append-review",
        "Reviewed sequence",
        "9007199254740993",
      ),
    ).toBe(true);
    expect(
      await factMatches(
        page,
        "append-current-mapping",
        "Current sequence",
        "9007199254740994",
      ),
    ).toBe(true);
    const reviewed = fixture.response("mapping", 200);
    await button(page, "Review current head").click();
    await body(reviewed);
    fixture.acceptCurrentHead();
    await expect(button(page, "Append revision")).toBeEnabled();
    expect(await keyCalls(page)).toBe(0);
    expect(fixture.appendCount).toBe(0);
    expect(
      await factMatches(
        page,
        "append-review",
        "Reviewed sequence",
        "9007199254740994",
      ),
    ).toBe(true);
    fixture.mapping.name = "Metadata changed after review";
    fixture.mapping.description =
      "Metadata-only changes do not replace the reviewed native head.";
    fixture.mapping.version = "2";
    fixture.mapping.updatedAtUtc = "2026-10-15T12:00:01Z";
    fixture.appendReply = fixture.committed();
    await dispatch(page, fixture, 200);
    await saved(page);
    expect((await keyCalls(page)) > 0).toBe(true);
    expect(
      await factMatches(
        page,
        "append-saved",
        "Saved sequence",
        "9007199254740995",
      ),
    ).toBe(true);
    wires(fixture, 1);
  });
});

test("controlled revision append: filename and file-size errors remain local before reading or sending", async ({
  page,
}) => {
  const invalid = [
    "",
    ".",
    "..",
    "folder/file.native.json",
    "folder\\file.native.json",
    "bad\u0085name",
    "bad\0name",
    "😀".repeat(64),
  ];
  for (const value of invalid)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      await privateAction(() => filenameField(current).fill(value));
      await button(current, "Append revision").click();
      await expect(summary(current)).toBeVisible();
      expect(
        (await filenameField(current).getAttribute("aria-invalid")) === "true",
      ).toBe(true);
      expect(fixture.appendCount).toBe(0);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(current, fixture);
    await filenameField(current).evaluate((element) => {
      (element as HTMLInputElement).value = "bad" + String.fromCharCode(0xd800);
      element.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await button(current, "Append revision").click();
    await expect(summary(current)).toBeVisible();
    expect(fixture.appendCount).toBe(0);
  });
  for (const bytes of [Buffer.alloc(0), Buffer.alloc(4_194_305)])
    await isolated(page, async (current, fixture) => {
      await current.addInitScript(() => {
        const counts = { reads: 0 },
          original = Blob.prototype.arrayBuffer;
        Object.defineProperty(window, "__appendSourceReads", { value: counts });
        Blob.prototype.arrayBuffer = function () {
          ++counts.reads;
          return original.call(this);
        };
      });
      await ready(current, fixture);
      await privateAction(() =>
        fileField(current).setInputFiles(
          fixture.file("bounded.native.json", bytes),
        ),
      );
      await expect(summary(current)).toBeVisible();
      await expect(button(current, "Append revision")).toBeDisabled();
      expect(
        await current.evaluate(
          () =>
            (window as unknown as { __appendSourceReads: { reads: number } })
              .__appendSourceReads.reads,
        ),
      ).toBe(0);
      expect(fixture.appendCount).toBe(0);
    });
});

test("controlled revision append: raw Blob and once-encoded filename preserve exact bytes and large decimal sequence", async ({
  page,
}) => {
  for (const filename of [
    " original +%&?#='()!*.native.json ",
    "😀".repeat(63) + "abc",
  ])
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture, filename, originalBytes(), "text/xml");
      fixture.appendReply = fixture.committed();
      await dispatch(current, fixture, 200);
      await saved(current);
      await expect(
        current.getByRole("link", { name: "View saved revision", exact: true }),
      ).toBeVisible();
      expect(
        await factMatches(
          current,
          "append-saved",
          "Saved sequence",
          "9007199254740994",
        ),
      ).toBe(true);
      expect(
        await factMatches(current, "append-saved", "Saved filename", filename),
      ).toBe(true);
      wires(fixture, 1);
      expect(await unloadWarning(current)).toBe(false);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(
      current,
      fixture,
      "maximum.native.json",
      Buffer.alloc(4_194_304, 0x7b),
      "application/xml",
    );
    fixture.appendReply = fixture.pending();
    await dispatch(current, fixture, 202);
    await expect(heading(current, "Append pending")).toBeVisible();
    wires(fixture, 1);
  });
});

test("controlled revision append: late, replaced and failed file preparation cannot publish stale bytes", async ({
  page,
}) => {
  await isolated(page, async (current, fixture) => {
    await current.addInitScript(() => {
      const digest = SubtleCrypto.prototype.digest,
        state = { entered: false };
      Object.defineProperty(window, "__appendHashProbe", { value: state });
      let first = true;
      SubtleCrypto.prototype.digest = async function (algorithm, bytes) {
        if (first) {
          first = false;
          state.entered = true;
          await new Promise<void>((resolve) =>
            window.addEventListener(
              "controlled-append-hash-release",
              () => resolve(),
              { once: true },
            ),
          );
        }
        return digest.call(this, algorithm, bytes);
      };
    });
    await ready(current, fixture);
    const filenameAtChange = await fileField(current).evaluateHandle(
      (input) => {
        if (!(input instanceof HTMLInputElement))
          throw new Error("The controlled file input was unavailable.");
        let selectedName: string | null = null;
        let observed = false;
        let synchronous = false;
        const capture = (event: Event) => {
          if (event.target === input)
            selectedName = input.files?.item(0)?.name ?? null;
        };
        const inspect = (event: Event) => {
          if (event.target !== input) return;
          observed = true;
          const filename = document.getElementById("append-filename");
          synchronous =
            selectedName !== null &&
            filename instanceof HTMLInputElement &&
            filename.value === selectedName;
          selectedName = null;
          document.removeEventListener("input", capture, true);
          document.removeEventListener("change", inspect);
        };
        // Keep the selected name before Angular clears FileList; inspect after its handler.
        document.addEventListener("input", capture, {
          capture: true,
          passive: true,
        });
        document.addEventListener("change", inspect, { passive: true });
        return () => observed && synchronous;
      },
    );
    await privateAction(() => fileField(current).setInputFiles(fixture.file()));
    expect(
      await filenameAtChange.evaluate((read) => read()),
      "The selected filename was not reflected before the file change event completed.",
    ).toBe(true);
    await filenameAtChange.dispose();
    await expect(
      current.getByText("Preparing the selected file…", { exact: false }),
    ).toBeVisible();
    await expect
      .poll(() =>
        current.evaluate(
          () =>
            (window as unknown as { __appendHashProbe: { entered: boolean } })
              .__appendHashProbe.entered,
        ),
      )
      .toBe(true);
    fixture.draft("replacement.native.json");
    await privateAction(() =>
      fileField(current).setInputFiles(
        fixture.file("replacement.native.json", replacementBytes()),
      ),
    );
    await current.evaluate(() =>
      window.dispatchEvent(new Event("controlled-append-hash-release")),
    );
    await expect(button(current, "Append revision")).toBeEnabled();
    fixture.appendReply = fixture.pending();
    await dispatch(current, fixture, 202);
    await expect(heading(current, "Append pending")).toBeVisible();
    wires(fixture, 1);
  });
  for (const failure of ["read", "hash"] as const)
    await isolated(page, async (current, fixture) => {
      await current.addInitScript((kind) => {
        if (kind === "read")
          Blob.prototype.arrayBuffer = async function () {
            throw new Error("controlled-private-native-detail");
          };
        else
          Object.defineProperty(SubtleCrypto.prototype, "digest", {
            configurable: true,
            value: async function () {
              throw new Error("controlled-private-native-detail");
            },
          });
      }, failure);
      await ready(current, fixture);
      await privateAction(() =>
        fileField(current).setInputFiles(fixture.file()),
      );
      await expect(summary(current)).toBeVisible();
      await expect(button(current, "Append revision")).toBeDisabled();
      expect(
        await current
          .locator("body")
          .evaluate(
            (element) =>
              !element.textContent?.includes(
                "controlled-private-native-detail",
              ),
          ),
      ).toBe(true);
      expect(fixture.appendCount).toBe(0);
    });
  await isolated(page, async (current, fixture) => {
    await current.addInitScript(() => {
      const digest = SubtleCrypto.prototype.digest,
        state = { entered: false };
      Object.defineProperty(window, "__appendHashProbe", { value: state });
      SubtleCrypto.prototype.digest = async function (algorithm, bytes) {
        state.entered = true;
        await new Promise<void>((resolve) =>
          window.addEventListener(
            "controlled-append-hash-release",
            () => resolve(),
            { once: true },
          ),
        );
        return digest.call(this, algorithm, bytes);
      };
    });
    await ready(current, fixture);
    await privateAction(() => fileField(current).setInputFiles(fixture.file()));
    await expect(button(current, "Stop waiting")).toBeEnabled();
    await expect
      .poll(() =>
        current.evaluate(
          () =>
            (window as unknown as { __appendHashProbe: { entered: boolean } })
              .__appendHashProbe.entered,
        ),
      )
      .toBe(true);
    await current
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await expect(heading(current, "Leave this append draft?")).toBeVisible();
    await button(current, "Discard draft and leave").click();
    await expect(heading(current, fixture.mapping.name)).toBeVisible();
    fixture.context.actorPrincipalId = otherActor;
    await current.goBack();
    await expect(fileField(current)).toBeEnabled();
    await current.evaluate(() =>
      window.dispatchEvent(new Event("controlled-append-hash-release")),
    );
    await expect(button(current, "Append revision")).toBeDisabled();
    expect(await filenameMatches(current, "")).toBe(true);
    expect(fixture.appendCount).toBe(0);
  });
});

test("controlled revision append: omitted and explicit-null result members retain their contract meaning", async ({
  page,
}) => {
  for (const mode of ["omitted", "null", "decodedInvalid"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      const reply = fixture.committed(
        mode === "decodedInvalid" ? "decodedInvalid" : "valid",
      );
      if (mode === "omitted") {
        delete (reply.json as Record<string, unknown>)["failure"];
        delete (reply.json as Record<string, unknown>)["rejection"];
      }
      fixture.appendReply = reply;
      await dispatch(current, fixture, 200);
      await saved(current, mode === "decodedInvalid");
      wires(fixture, 1);
    });
  for (const mode of ["omitted", "null"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      fixture.appendReply = {
        status: 422,
        json:
          mode === "omitted"
            ? { outcome: "rejected", rejection: "unregisteredBackend" }
            : {
                outcome: "rejected",
                rejection: "unregisteredBackend",
                receipt: null,
                failure: null,
                committedRevisionUrl: null,
              },
      };
      await dispatch(current, fixture, 422);
      await expect(
        heading(current, "Append call was not acknowledged as saved"),
      ).toBeVisible();
      await expect(button(current, "Resend original append")).toBeEnabled();
      await expect(heading(current, "Revision saved")).toHaveCount(0);
      wires(fixture, 1);
    });
  for (const [status, rejection] of [
    [400, "emptyNative"],
    [413, "nativeLimit"],
  ] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      fixture.appendReply = {
        status,
        json: { outcome: "rejected", rejection, receipt: null },
      };
      await dispatch(current, fixture, status);
      await expect(
        heading(current, "Append call was not acknowledged as saved"),
      ).toBeVisible();
      await expect(button(current, "Resend original append")).toBeEnabled();
      wires(fixture, 1);
    });
});

test("controlled revision append: incompatible response status, ancestry, nullability, URL and sequence never acknowledge a save", async ({
  page,
}) => {
  const variants: {
    label: string;
    change: (reply: Reply, fixture: ControlledAppend) => void;
  }[] = [
    {
      label: "create-only success status",
      change: (reply) => {
        reply.status = 201;
      },
    },
    {
      label: "missing committed receipt",
      change: (reply) => {
        delete (reply.json as Record<string, unknown>)["receipt"];
      },
    },
    {
      label: "missing committed URL",
      change: (reply) => {
        delete (reply.json as Record<string, unknown>)["committedRevisionUrl"];
      },
    },
    {
      label: "queried committed URL",
      change: (reply) => {
        (reply.json as Record<string, unknown>)["committedRevisionUrl"] =
          String(
            (reply.json as Record<string, unknown>)["committedRevisionUrl"],
          ) + "?head=any";
      },
    },
    {
      label: "absolute committed URL",
      change: (reply) => {
        (reply.json as Record<string, unknown>)["committedRevisionUrl"] =
          "https://invalid.example/foreign";
      },
    },
    {
      label: "extra result member",
      change: (reply) => {
        (reply.json as Record<string, unknown>)["extra"] = true;
      },
    },
    {
      label: "foreign project",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.project.projectId =
          otherProjectId;
      },
    },
    {
      label: "foreign existing mapping",
      change: (reply) => {
        const receipt = (reply.json as { receipt: Receipt }).receipt;
        receipt.futureMapping.mappingId = otherMappingId;
        receipt.futureRevision.mappingId = otherMappingId;
      },
    },
    {
      label: "inconsistent future ancestry",
      change: (reply) => {
        (
          reply.json as { receipt: Receipt }
        ).receipt.futureRevision.organizationId = otherOrganization;
      },
    },
    {
      label: "noncanonical operation",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.operationId =
          "00000000-0000-0000-0000-000000000000";
      },
    },
    {
      label: "different saved filename",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.originalFilename =
          "another.native.json";
      },
    },
    {
      label: "wrong large decimal successor",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedSequence =
          "9007199254740993";
      },
    },
    {
      label: "hardcoded second sequence",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedSequence = "2";
      },
    },
    {
      label: "overflowing Int64 sequence",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedSequence =
          "9223372036854775808";
      },
    },
    {
      label: "noncanonical decimal sequence",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedSequence =
          "09007199254740994";
      },
    },
    {
      label: "missing required nullable receipt field",
      change: (reply) => {
        delete (
          (reply.json as { receipt: Receipt }).receipt as unknown as Record<
            string,
            unknown
          >
        )["latestFailure"];
      },
    },
    {
      label: "claim budget exceeded",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.claimCount = 9;
      },
    },
    {
      label: "committed receipt without completion",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.completedAtUtc = null;
      },
    },
    {
      label: "completion predates reservation",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.completedAtUtc =
          "2026-10-15T11:59:59Z";
      },
    },
    {
      label: "admission contradicts outcome",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedAdmission =
          "decodedInvalid";
      },
    },
    {
      label: "committed receipt has failure",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.latestFailure =
          "needsSource";
      },
    },
    {
      label: "pending receipt without Location",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        delete reply.headers;
      },
    },
    {
      label: "queried pending Location",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        reply.headers = { Location: fixture.operationPath + "?x=1" };
      },
    },
    {
      label: "foreign pending Location",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        reply.headers = { Location: "/api/v1/foreign" };
      },
    },
    {
      label: "pending receipt with completion",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        (reply.json as { receipt: Receipt }).receipt.completedAtUtc =
          "2026-10-15T12:00:01Z";
      },
    },
    {
      label: "source requirement without matching failure",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.needsSource());
        (reply.json as Record<string, unknown>)["failure"] = null;
      },
    },
    {
      label: "terminal conflict falsely claims saved sequence",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.conflict());
        (reply.json as { receipt: Receipt }).receipt.committedSequence =
          "9007199254740994";
      },
    },
    {
      label: "terminal conflict lacks own receipt",
      change: (reply) => {
        reply.status = 409;
        reply.json = { outcome: "headConflict", receipt: null };
      },
    },
    {
      label: "native rejection lacks terminal receipt",
      change: (reply) => {
        reply.status = 422;
        reply.json = {
          outcome: "rejected",
          rejection: "nativeDecode",
          receipt: null,
        };
      },
    },
    {
      label: "closed error extension",
      change: (reply) => {
        reply.status = 500;
        reply.json = { code: "internalFailure", extra: true };
      },
    },
  ];
  for (const variant of variants)
    await test.step(variant.label, () =>
      isolated(page, async (current, fixture) => {
        await ready(current, fixture);
        await prepare(current, fixture);
        const reply = fixture.committed();
        variant.change(reply, fixture);
        fixture.appendReply = reply;
        await dispatch(current, fixture, reply.status!);
        await unknown(current);
        await expect(button(current, "Resend original append")).toBeEnabled();
        await expect(heading(current, "Revision saved")).toHaveCount(0);
        await expect(button(current, "Check append status")).toHaveCount(0);
        expect(fixture.revisionReads).toBe(0);
        expect(await unloadWarning(current)).toBe(true);
        wires(fixture, 1);
      }),
    );
});

test("controlled revision append: pending status is explicit GET-only and needsSource resends the frozen original", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    await page.clock.install();
    fixture.appendReply = fixture.pending("staged", 2);
    await dispatch(page, fixture, 202);
    await expect(heading(page, "Append pending")).toBeVisible();
    await expect(button(page, "Check append status")).toBeEnabled();
    await expect(fileField(page)).toBeDisabled();
    expect(
      await filenameField(page).evaluate(
        (element) => (element as HTMLInputElement).readOnly,
      ),
    ).toBe(true);
    await page.clock.fastForward(10000);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    fixture.advanceHead();
    fixture.receiptReply = { json: fixture.receipt("ready", 2) };
    const checked = fixture.response("receipt", 200);
    await button(page, "Check append status").click();
    await body(checked);
    await expect(button(page, "Resume append")).toBeEnabled();
    expect(fixture.receiptReads).toBe(1);
    expect(fixture.receiptValid).toBe(true);
    expect(fixture.resumeCount).toBe(0);
    fixture.resumeReply = fixture.needsSource(3);
    const resumed = fixture.response("resume", 409);
    await button(page, "Resume append").click();
    await body(resumed);
    await expect(button(page, "Resend original file")).toBeEnabled();
    await expect(button(page, "Resume append")).toHaveCount(0);
    expect(fixture.resumeValid).toBe(true);
    expect(fixture.resumeCount).toBe(1);
    fixture.appendReply = fixture.committed("valid", 3);
    await dispatch(page, fixture, 200, "Resend original file");
    await saved(page);
    wires(fixture, 2);
    sameOriginal(fixture);
    expect(fixture.receiptReads).toBe(1);
    expect(await unloadWarning(page)).toBe(false);
  });
});

test("controlled revision append: terminal headConflict is never saved or rebased and only a deliberate new attempt changes key", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    await page.clock.install();
    const originalConflict = fixture.receipt("headConflict");
    fixture.appendReply = fixture.conflict();
    await dispatch(page, fixture, 409);
    await expect(
      heading(page, "Append was not saved: head changed"),
    ).toBeVisible();
    await expect(
      page.getByText(
        "The mapping head changed; this append was not saved as a revision.",
        { exact: false },
      ),
    ).toBeVisible();
    await expect(heading(page, "Revision saved")).toHaveCount(0);
    await expect(
      page.getByRole("link", { name: "View saved revision", exact: true }),
    ).toHaveCount(0);
    await expect(button(page, "Resume append")).toHaveCount(0);
    await expect(button(page, "Replay append result")).toHaveCount(0);
    await expect(button(page, "Resend original append")).toHaveCount(0);
    expect(fixture.revisionReads).toBe(0);
    expect(await unloadWarning(page)).toBe(false);
    await page.clock.fastForward(10000);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    fixture.advanceHead();
    const current = fixture.response("mapping", 200);
    await button(page, "Review current head").click();
    await body(current);
    await expect(button(page, "Check append status")).toBeEnabled();
    fixture.receiptReply = { json: originalConflict };
    const own = fixture.response("receipt", 200);
    await button(page, "Check append status").click();
    await body(own);
    await expect(
      heading(page, "Append was not saved: head changed"),
    ).toBeVisible();
    expect(fixture.receiptReads).toBe(1);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    const newAdmission = fixture.response("mapping", 200);
    await button(page, "Start a new append").click();
    await body(newAdmission);
    fixture.nextAttempt();
    await expect(fileField(page)).toBeEnabled();
    await expect(button(page, "Append revision")).toBeDisabled();
    await expect(
      heading(page, "Append was not saved: head changed"),
    ).toHaveCount(0);
    await prepare(
      page,
      fixture,
      "deliberate-new.native.json",
      replacementBytes(),
    );
    fixture.appendReply = fixture.committed();
    await dispatch(page, fixture, 200);
    await saved(page);
    wires(fixture, 2);
    expect(fixture.lastKeyDiffers).toBe(true);
  });
});

test("controlled revision append: lost acknowledgement stays unknown through later refusals and recovers after current head advances", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture, "historical +%.native.json");
    await loseOriginal(page, fixture);
    fixture.advanceHead("9007199254740996");
    const refusals = [
      [400, "invalidRequest"],
      [413, "bodyLimit"],
      [415, "unsupportedMediaType"],
      [503, "ingestionAdmissionBusy"],
      [500, "persistenceFailure"],
      [404, "notFound"],
    ] as const;
    for (const [index, [status, code]] of refusals.entries()) {
      fixture.appendReply = codeReply(status, code);
      await dispatch(page, fixture, status, "Resend original append");
      if (status === 404) await refresh(page, fixture);
      await unknown(page);
      await expect(button(page, "Resend original append")).toBeEnabled();
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
      expect(fixture.revisionReads).toBe(0);
      wires(fixture, index + 2);
      sameOriginal(fixture);
    }
    fixture.appendReply = fixture.committed();
    await dispatch(page, fixture, 200, "Resend original append");
    await saved(page);
    await expect(heading(page, "Append outcome is unknown")).toHaveCount(0);
    expect(
      await factMatches(
        page,
        "append-saved",
        "Saved sequence",
        "9007199254740994",
      ),
    ).toBe(true);
    expect(
      await factMatches(
        page,
        "append-current-mapping",
        "Current sequence",
        "9007199254740996",
      ),
    ).toBe(true);
    wires(fixture, refusals.length + 2);
    sameOriginal(fixture);
    expect(await unloadWarning(page)).toBe(false);
  });
});

test("controlled revision append: receipt-less later recovery replies retain the learned operation and earlier uncertainty", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    fixture.appendReply = fixture.pending("staged", 2);
    await dispatch(page, fixture, 202);
    await expect(button(page, "Resume append")).toBeEnabled();
    fixture.resumeReply = { abort: true };
    const failed = fixture.failedWrite("resume");
    await button(page, "Resume append").click();
    await failed;
    await unknown(page);
    const later = [
      codeReply(503, "ingestionAdmissionBusy"),
      {
        status: 409,
        json: {
          outcome: "claimLost",
          receipt: null,
          failure: null,
          rejection: null,
          committedRevisionUrl: null,
        },
      },
      { status: 409, json: { outcome: "idempotencyConflict" } },
      {
        status: 404,
        json: { outcome: "notAuthorizedOrNotFound", receipt: null },
      },
    ];
    for (const reply of later) {
      fixture.resumeReply = reply;
      const response = fixture.response("resume", reply.status!);
      await button(page, "Resume append").click();
      await body(response);
      if (reply.status === 404) await refresh(page, fixture);
      await unknown(page);
      await expect(button(page, "Check append status")).toBeEnabled();
      await expect(button(page, "Resend original append")).toHaveCount(0);
      wires(fixture, 1);
    }
    fixture.receiptReply = { json: fixture.receipt("committed", 2) };
    const checked = fixture.response("receipt", 200);
    await button(page, "Check append status").click();
    await body(checked);
    await saved(page);
    await expect(heading(page, "Append outcome is unknown")).toHaveCount(0);
    expect(fixture.receiptValid).toBe(true);
    expect(fixture.resumeValid).toBe(true);
    expect(fixture.resumeCount).toBe(later.length + 1);
    wires(fixture, 1);
  });
});

test("controlled revision append: learned identity, claim count and terminal facts cannot be replaced by later receipt reads", async ({
  page,
}) => {
  const changes: ((receipt: Receipt) => void)[] = [
    (receipt) => {
      receipt.operationId = "50000000-0000-4000-8000-000000000099";
    },
    (receipt) => {
      receipt.futureMapping.mappingId = otherMappingId;
      receipt.futureRevision.mappingId = otherMappingId;
    },
    (receipt) => {
      receipt.futureRevision.revisionId =
        "40000000-0000-4000-8000-000000000099";
    },
    (receipt) => {
      receipt.reservedAtUtc = "2026-10-15T12:00:00.1234568Z";
    },
    (receipt) => {
      receipt.claimCount = 1;
    },
  ];
  for (const change of changes)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      fixture.appendReply = fixture.pending("ready", 2);
      await dispatch(current, fixture, 202);
      await expect(button(current, "Check append status")).toBeEnabled();
      const receipt = fixture.receipt("staged", 2);
      change(receipt);
      fixture.receiptReply = { json: receipt };
      const checked = fixture.response("receipt", 200);
      await button(current, "Check append status").click();
      await body(checked);
      await expect(current.getByRole("alert")).toHaveText(
        "The reply could not be verified as the original append operation.",
      );
      await expect(button(current, "Check append status")).toBeEnabled();
      await expect(button(current, "Resume append")).toBeEnabled();
      await expect(heading(current, "Revision saved")).toHaveCount(0);
      expect(await unloadWarning(current)).toBe(true);
      wires(fixture, 1);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(current, fixture);
    fixture.appendReply = fixture.pending("ready", 2);
    await dispatch(current, fixture, 202);
    fixture.receiptReply = { json: fixture.receipt("staged", 3) };
    const checked = fixture.response("receipt", 200);
    await button(current, "Check append status").click();
    await body(checked);
    await expect(button(current, "Resume append")).toBeEnabled();
    await expect(current.getByRole("alert")).toHaveCount(0);
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(current, fixture);
    fixture.appendReply = fixture.committed();
    await dispatch(current, fixture, 200);
    await saved(current);
    fixture.receiptReply = { json: fixture.receipt("committed", 2) };
    const checked = fixture.response("receipt", 200);
    await button(current, "Check append status").click();
    await body(checked);
    await expect(current.getByRole("alert")).toHaveText(
      "The reply could not be verified as the original append operation.",
    );
    await saved(current);
    expect(await unloadWarning(current)).toBe(false);
    wires(fixture, 1);
  });
});

test("controlled revision append: verified terminal rejection, exhausted claims and retryable failure remain distinct", async ({
  page,
}) => {
  const terminal = [
    (fixture: ControlledAppend) => fixture.rejected(),
    (fixture: ControlledAppend) => ({
      status: 422,
      json: {
        outcome: "rejected",
        receipt: fixture.receipt("rejected", 1, "evidenceLimit"),
        rejection: "resourceLimit",
        failure: "evidenceLimit",
      },
    }),
    (fixture: ControlledAppend) => ({
      status: 409,
      json: {
        outcome: "attemptExhausted",
        receipt: fixture.receipt("attemptExhausted", 8),
      },
    }),
  ];
  for (const reply of terminal)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      fixture.appendReply = reply(fixture);
      await dispatch(current, fixture, fixture.appendReply.status!);
      await expect(
        heading(current, "Append ended without a saved revision"),
      ).toBeVisible();
      await expect(button(current, "Resume append")).toHaveCount(0);
      await expect(button(current, "Resend original append")).toHaveCount(0);
      await expect(heading(current, "Revision saved")).toHaveCount(0);
      await expect(button(current, "Start a new append")).toBeEnabled();
      expect(await unloadWarning(current)).toBe(false);
      expect(fixture.revisionReads).toBe(0);
      wires(fixture, 1);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(current, fixture);
    fixture.appendReply = {
      status: 503,
      json: {
        outcome: "retryableFailure",
        receipt: fixture.receipt("staged", 2, "backendUnavailable"),
        failure: "backendUnavailable",
      },
    };
    await dispatch(current, fixture, 503);
    await expect(heading(current, "Append pending")).toBeVisible();
    await expect(button(current, "Resume append")).toBeEnabled();
    await expect(button(current, "Start a new append")).toHaveCount(0);
    expect(await unloadWarning(current)).toBe(true);
    wires(fixture, 1);
  });
});

test("controlled revision append: maximum Int64 head is admitted without inventing an exhaustion result", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    fixture.mapping.head.sequence = "9223372036854775807";
    fixture.acceptCurrentHead();
    await ready(page, fixture);
    await prepare(page, fixture);
    fixture.appendReply = codeReply(500, "internalFailure");
    await dispatch(page, fixture, 500);
    await unknown(page);
    await expect(button(page, "Resend original append")).toBeEnabled();
    await expect(
      heading(page, "Append ended without a saved revision"),
    ).toHaveCount(0);
    wires(fixture, 1);
  });
});

test("controlled revision append: saved proof survives failed reads and view-only retry performs no mutation", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    fixture.revisionReply = codeReply(503, "unavailable");
    fixture.appendReply = fixture.committed("decodedInvalid");
    await dispatch(page, fixture, 200);
    await saved(page, true);
    await expect(
      page.getByRole("link", { name: "View saved revision", exact: true }),
    ).toHaveCount(0);
    expect(await unloadWarning(page)).toBe(false);
    fixture.context.features.uploadRevisions = false;
    fixture.mapping.access.role = "viewer";
    fixture.mapping.access.actions.uploadRevisions = false;
    fixture.revisionReply = null;
    fixture.advanceHead();
    const accessed = fixture.response("revision", 200);
    await button(page, "Retry revision access").click();
    await body(accessed);
    await saved(page, true);
    await expect(
      page.getByRole("link", { name: "View saved revision", exact: true }),
    ).toBeVisible();
    const status = button(page, "Check append status");
    expect((await status.count()) === 0 || (await status.isDisabled())).toBe(
      true,
    );
    await expect(button(page, "Start a new append")).toHaveCount(0);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Revision saved")).toBeVisible();
    await expect(
      page.locator("#append-saved").locator("xpath=..").locator("dl"),
    ).toHaveCount(0);
    await expect(
      page.getByRole("link", { name: "View saved revision", exact: true }),
    ).toHaveCount(0);
    await expect(button(page, "Retry revision access")).toBeEnabled();
    fixture.contextReply = null;
    const retried = fixture.response("revision", 200);
    const readsBefore = fixture.revisionReads;
    await button(page, "Retry revision access").click();
    await body(retried);
    await saved(page, true);
    expect(fixture.revisionReads).toBe(readsBefore + 1);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Revision saved")).toBeVisible();
    await expect(
      page.locator("#append-saved").locator("xpath=..").locator("dl"),
    ).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = otherActor;
    await refresh(page, fixture);
    await expect(
      heading(page, "An original append is retained privately"),
    ).toBeVisible();
    await expect(heading(page, "Revision saved")).toHaveCount(0);
    await expect(
      heading(page, "Revision saved with validation issues"),
    ).toHaveCount(0);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Revision saved")).toHaveCount(0);
    await expect(button(page, "Retry revision access")).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = synthetic.actor;
    await refresh(page, fixture);
    await saved(page, true);
    await expect(
      page.getByRole("link", { name: "View saved revision", exact: true }),
    ).toBeVisible();
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    await ready(current, fixture);
    await prepare(current, fixture);
    fixture.revisionReply = {
      json: {
        ...fixture.revision(),
        nativeDigest: { algorithm: "sha256", value: "0".repeat(64) },
      },
    };
    fixture.appendReply = fixture.committed();
    await dispatch(current, fixture, 200);
    await saved(current);
    await expect(
      current.getByRole("link", { name: "View saved revision", exact: true }),
    ).toHaveCount(0);
    fixture.revisionReply = null;
    const accessed = fixture.response("revision", 200);
    await button(current, "Retry revision access").click();
    await body(accessed);
    await saved(current);
    await expect(
      current.getByRole("link", { name: "View saved revision", exact: true }),
    ).toBeVisible();
    wires(fixture, 1);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
  });
});

test("controlled revision append: fresh role and feature revocation hide recovery without reusing authority", async ({
  page,
}) => {
  for (const gate of ["role", "feature"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      fixture.appendReply = fixture.pending("staged", 2);
      await dispatch(current, fixture, 202);
      await expect(button(current, "Resume append")).toBeEnabled();
      if (gate === "role") {
        fixture.mapping.access.role = "viewer";
        fixture.mapping.access.actions.uploadRevisions = false;
      } else fixture.context.features.uploadRevisions = false;
      await refresh(current, fixture);
      await expect(heading(current, "Append form unavailable")).toBeVisible();
      for (const label of [
        "Check append status",
        "Resume append",
        "Resend original file",
      ]) {
        const target = button(current, label);
        expect(
          (await target.count()) === 0 || (await target.isDisabled()),
        ).toBe(true);
      }
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
      wires(fixture, 1);
      fixture.mapping.access.role = "publisher";
      fixture.mapping.access.actions.uploadRevisions = true;
      fixture.context.features.uploadRevisions = true;
      await refresh(current, fixture);
      await expect(button(current, "Check append status")).toBeEnabled();
      await expect(button(current, "Resume append")).toBeEnabled();
      fixture.receiptReply = { json: fixture.receipt("staged", 2) };
      const checked = fixture.response("receipt", 200);
      await button(current, "Check append status").click();
      await body(checked);
      expect(fixture.receiptReads).toBe(1);
      expect(fixture.receiptValid).toBe(true);
      expect(fixture.resumeCount).toBe(0);
      wires(fixture, 1);
    });
});

test("controlled revision append: foreign actor, organization or mode latches private hiding through later context failure", async ({
  page,
}) => {
  const changes: ((fixture: ControlledAppend) => void)[] = [
    (fixture) => {
      fixture.context.actorPrincipalId = otherActor;
    },
    (fixture) => {
      fixture.context.allowedOrganizationId = otherOrganization;
    },
    (fixture) => {
      // @ts-expect-error Deliberately send an unsupported wire mode to test invalidation.
      fixture.context.mode = "another-mode";
    },
  ];
  for (const change of changes)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture, "private-original.native.json");
      await loseOriginal(current, fixture);
      change(fixture);
      if (change === changes[0]) {
        const reads = {
            context: fixture.contextReads,
            mapping: fixture.mappingReads,
          },
          held = fixture.hold("mapping", {
            json: structuredClone(fixture.mapping),
          }),
          context = fixture.response("context", 200),
          mappingPathname = catalogApi(mappingPath(fixture.scope)),
          origin = new URL(current.url()).origin;
        const mappingRequest = current
          .waitForRequest(
            (request) => {
              const url = new URL(request.url());
              return (
                request.method() === "GET" &&
                url.origin === origin &&
                url.pathname === mappingPathname &&
                url.search === ""
              );
            },
            { timeout: 10000 },
          )
          .catch(() => {
            throw new Error(
              "The held foreign admission mapping request was not observed.",
            );
          });
        void mappingRequest.catch(() => undefined);
        await button(current, "Refresh access").click();
        await body(context);
        const request = await mappingRequest;
        await expect.poll(held.entered).toBe(true);
        await expect(
          heading(current, "An original append is retained privately"),
        ).toBeVisible();
        await expect(filenameField(current)).toHaveCount(0);
        await expect(button(current, "Resend original append")).toHaveCount(0);
        await expect(heading(current, "Revision saved")).toHaveCount(0);
        expect(
          await current
            .locator(".native-append")
            .evaluate(
              (element) =>
                !element.textContent?.includes("private-original.native.json"),
            ),
          "The held foreign admission exposed the original filename.",
        ).toBe(true);
        await expect(current.locator(".native-append")).toHaveAttribute(
          "aria-busy",
          "true",
        );
        await expect(button(current, "Refresh access")).toBeDisabled();
        expect(
          held.canceled(),
          "The current foreign admission mapping was canceled while held.",
        ).toBe(false);
        expect(fixture.appendCount).toBe(1);
        expect(fixture.receiptReads).toBe(0);
        expect(fixture.resumeCount).toBe(0);
        held.release();
        const delivered = await request.response().catch(() => null);
        expect(
          delivered !== null,
          "The exact held foreign admission mapping had no response.",
        ).toBe(true);
        if (!delivered)
          throw new Error(
            "The exact held foreign admission mapping had no response.",
          );
        expect(
          delivered.request() === request,
          "The foreign admission response belongs to another request.",
        ).toBe(true);
        await body(Promise.resolve(delivered));
        expect(
          request.failure() === null,
          "The delivered foreign admission mapping request failed.",
        ).toBe(true);
        expect(
          held.canceled(),
          "The foreign admission mapping was canceled before complete delivery.",
        ).toBe(false);
        await expect(button(current, "Refresh access")).toBeEnabled();
        await expect(
          heading(current, "An original append is retained privately"),
        ).toBeVisible();
        expect(fixture.contextReads).toBe(reads.context + 1);
        expect(fixture.mappingReads).toBe(reads.mapping + 1);
        fixture.mappingReply = null;
      } else await refresh(current, fixture);
      await expect(filenameField(current)).toHaveCount(0);
      await expect(button(current, "Resend original append")).toHaveCount(0);
      await expect(heading(current, "Revision saved")).toHaveCount(0);
      fixture.contextReply = codeReply(503, "unavailable");
      await refresh(current, fixture, 503);
      await expect(filenameField(current)).toHaveCount(0);
      await expect(button(current, "Resend original append")).toHaveCount(0);
      expect(
        await current
          .locator(".native-append")
          .evaluate(
            (element) =>
              !element.textContent?.includes("private-original.native.json"),
          ),
      ).toBe(true);
      fixture.contextReply = null;
      fixture.context.actorPrincipalId = synthetic.actor;
      fixture.context.allowedOrganizationId = synthetic.org;
      fixture.context.mode = "development";
      const restoredReads = {
        context: fixture.contextReads,
        mapping: fixture.mappingReads,
      };
      await refresh(current, fixture);
      await unknown(current);
      await expect(button(current, "Resend original append")).toBeEnabled();
      expect(fixture.contextReads).toBe(restoredReads.context + 1);
      expect(fixture.mappingReads).toBe(restoredReads.mapping + 1);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
      wires(fixture, 1);
    });
});

test("controlled revision append: canceled late replies and a second full mapping target cannot replace the one original slot", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    const held = fixture.hold("append", fixture.committed());
    await button(page, "Append revision").click();
    await expect.poll(held.entered).toBe(true);
    const failed = fixture.failedWrite();
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await expect(
      heading(page, "Leave and keep append recovery?"),
    ).toBeVisible();
    await button(page, "Keep recovery information and leave").click();
    await failed;
    await expect(heading(page, fixture.mapping.name)).toBeVisible();
    fixture.context.actorPrincipalId = otherActor;
    await page.goBack();
    await expect(
      heading(page, "An original append is retained privately"),
    ).toBeVisible();
    await expect(filenameField(page)).toHaveCount(0);
    await expect(button(page, "Resend original append")).toHaveCount(0);
    held.release();
    expect(held.canceled()).toBe(true);
    await expect(heading(page, "Revision saved")).toHaveCount(0);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Revision saved")).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = synthetic.actor;
    await refresh(page, fixture);
    await unknown(page);
    await expect(button(page, "Resend original append")).toBeEnabled();
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await button(page, "Keep recovery information and leave").click();
    await expect(heading(page, fixture.mapping.name)).toBeVisible();
    for (const target of [
      fixture.otherScope,
      fixture.otherProjectScope,
      { ...fixture.scope, organizationId: otherOrganization },
    ]) {
      await page.evaluate((path) => {
        history.pushState(null, "", path);
        window.dispatchEvent(new PopStateEvent("popstate"));
      }, appendPath(target));
      await expect(
        heading(page, "An original append is retained privately"),
      ).toBeVisible();
      await expect(filenameField(page)).toHaveCount(0);
      await expect(button(page, "Append revision")).toHaveCount(0);
      await expect(button(page, "Resend original append")).toHaveCount(0);
      wires(fixture, 1);
      await page.goBack();
      await expect(
        heading(page, "Leave and keep append recovery?"),
      ).toBeVisible();
      await button(page, "Keep recovery information and leave").click();
      await expect(heading(page, fixture.mapping.name)).toBeVisible();
    }
    await page.goBack();
    await unknown(page);
    await expect(button(page, "Resend original append")).toBeEnabled();
    fixture.appendReply = fixture.committed();
    await dispatch(page, fixture, 200, "Resend original append");
    await saved(page);
    wires(fixture, 2);
    sameOriginal(fixture);
  });
});

test("controlled revision append: focus refresh preserves the draft and duplicate submit events dispatch only once", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture, " raw +%.native.json ");
    const focused = fixture.response("context", 200);
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await body(focused);
    await expect(fileField(page)).toBeEnabled();
    expect(await filenameMatches(page, " raw +%.native.json ")).toBe(true);
    expect(fixture.appendCount).toBe(0);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    const held = fixture.hold("append", fixture.pending());
    const response = fixture.response("append", 202);
    await button(page, "Append revision").evaluate((element) => {
      (element as HTMLButtonElement).click();
      (element as HTMLButtonElement).click();
    });
    await expect.poll(held.entered).toBe(true);
    wires(fixture, 1);
    await page.evaluate(() => {
      window.dispatchEvent(new Event("focus"));
      document
        .querySelector(".append-form")
        ?.dispatchEvent(
          new Event("submit", { bubbles: true, cancelable: true }),
        );
    });
    await expect(button(page, "Refresh access")).toBeDisabled();
    wires(fixture, 1);
    held.release();
    await body(response);
    await expect(button(page, "Check append status")).toBeEnabled();
    wires(fixture, 1);
    expect(fixture.resumeCount).toBe(0);
  });
});

test("controlled revision append: timeout and Stop waiting retain original recovery with no automatic send", async ({
  page,
}) => {
  for (const action of ["timeout", "stop"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current, fixture);
      await prepare(current, fixture);
      if (action === "timeout") await current.clock.install();
      const held = fixture.hold("append", fixture.committed());
      await button(current, "Append revision").click();
      await expect.poll(held.entered).toBe(true);
      const failed = fixture.failedWrite();
      if (action === "timeout") await current.clock.fastForward(30001);
      else await button(current, "Stop waiting").click();
      await failed;
      await unknown(current);
      await expect(button(current, "Resend original append")).toBeEnabled();
      expect(await unloadWarning(current)).toBe(true);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
      wires(fixture, 1);
      held.release();
      fixture.appendReply = fixture.committed();
      await dispatch(current, fixture, 200, "Resend original append");
      await saved(current);
      wires(fixture, 2);
      sameOriginal(fixture);
      expect(await unloadWarning(current)).toBe(false);
    });
});

test("controlled revision append: leave and acknowledged abandonment distinguish an unsent draft from uncertain server work", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page, fixture);
    await prepare(page, fixture);
    expect(await unloadWarning(page)).toBe(true);
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await expect(heading(page, "Leave this append draft?")).toBeVisible();
    await expect(heading(page, "Leave this append draft?")).toBeFocused();
    await button(page, "Stay").click();
    await expect(heading(page, "Upload revision")).toBeFocused();
    expect(await filenameMatches(page, "original.native.json")).toBe(true);
    expect(fixture.appendCount).toBe(0);
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await button(page, "Discard draft and leave").click();
    await expect(heading(page, fixture.mapping.name)).toBeVisible();
    expect(await unloadWarning(page)).toBe(false);
    await page.goBack();
    await expect(fileField(page)).toBeEnabled();
    await expect(button(page, "Append revision")).toBeDisabled();
    expect(await filenameMatches(page, "")).toBe(true);
    await prepare(page, fixture);
    await loseOriginal(page, fixture);
    await button(page, "Abandon original append").click();
    await expect(
      heading(page, "Forget this append's recovery information?"),
    ).toBeVisible();
    await expect(
      heading(page, "Forget this append's recovery information?"),
    ).toBeFocused();
    await expect(button(page, "Abandon and start a new append")).toBeDisabled();
    await button(page, "Keep original append").click();
    await unknown(page);
    await expect(page.locator("#append-status")).toBeFocused();
    wires(fixture, 1);
    await button(page, "Abandon original append").click();
    await page
      .getByRole("checkbox", {
        name: "I understand an append may already have saved.",
        exact: true,
      })
      .check();
    await button(page, "Abandon and start a new append").click();
    await expect(fileField(page)).toBeEnabled();
    await expect(button(page, "Append revision")).toBeDisabled();
    await expect(button(page, "Resend original append")).toHaveCount(0);
    expect(await unloadWarning(page)).toBe(false);
    wires(fixture, 1);
    fixture.nextAttempt();
    await prepare(
      page,
      fixture,
      "deliberate-new.native.json",
      replacementBytes(),
    );
    fixture.appendReply = fixture.pending();
    await dispatch(page, fixture, 202);
    await expect(button(page, "Check append status")).toBeEnabled();
    wires(fixture, 2);
    expect(fixture.lastKeyDiffers).toBe(true);
  });
});

test("controlled revision append: keyboard and 390px flow render literal text and keep recovery out of browser persistence", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.addInitScript(() => {
      const counts = { storageWrites: 0, databaseOpens: 0, cacheOpens: 0 };
      Object.defineProperty(window, "__appendBoundaryPrivacy", {
        value: counts,
      });
      Object.defineProperty(Storage.prototype, "setItem", {
        configurable: true,
        value: function () {
          ++counts.storageWrites;
        },
      });
      Object.defineProperty(IDBFactory.prototype, "open", {
        configurable: true,
        value: function () {
          ++counts.databaseOpens;
          throw new Error("Controlled persistence is unavailable.");
        },
      });
      if (typeof CacheStorage !== "undefined")
        Object.defineProperty(CacheStorage.prototype, "open", {
          configurable: true,
          value: function () {
            ++counts.cacheOpens;
            return Promise.reject(
              new Error("Controlled persistence is unavailable."),
            );
          },
        });
    });
    await page.setViewportSize({ width: 390, height: 844 });
    const literalName =
      "<img src=x onerror=document.documentElement.dataset.appendMarkup=1>";
    fixture.mapping.name = literalName;
    await ready(page, fixture);
    await fileField(page).focus();
    await expect(fileField(page)).toBeFocused();
    const chooser = page.waitForEvent("filechooser");
    await fileField(page).press("Enter");
    const selected = await chooser;
    const filename = "<img onerror=1>" + "x".repeat(180) + ".native.json";
    fixture.draft(filename);
    await privateAction(() => selected.setFiles(fixture.file(filename)));
    await expect(button(page, "Append revision")).toBeEnabled();
    await fileField(page).focus();
    await fileField(page).press("Tab");
    await expect(filenameField(page)).toBeFocused();
    await privateAction(() => filenameField(page).fill(""));
    await filenameField(page).press("Enter");
    await expect(summary(page)).toBeFocused();
    await page
      .getByRole("link", { name: "Enter a saved filename.", exact: true })
      .press("Enter");
    await expect(filenameField(page)).toBeFocused();
    await privateAction(() => filenameField(page).fill(filename));
    fixture.appendReply = fixture.committed();
    const response = fixture.response("append", 200);
    await filenameField(page).press("Enter");
    await body(response);
    await saved(page);
    await expect(page.locator("#append-status")).toBeFocused();
    expect(
      await factMatches(page, "append-saved", "Saved filename", filename),
    ).toBe(true);
    expect(
      await factMatches(page, "append-current-mapping", "Name", literalName),
    ).toBe(true);
    expect(await page.locator(".native-append img").count()).toBe(0);
    expect(
      (await page.locator("html").getAttribute("data-append-markup")) === null,
    ).toBe(true);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth + 1,
      ),
    ).toBe(true);
    const privacy = await page.evaluate(() => {
      const counts = (
        window as unknown as {
          __appendBoundaryPrivacy: {
            storageWrites: number;
            databaseOpens: number;
            cacheOpens: number;
          };
        }
      ).__appendBoundaryPrivacy;
      return {
        ...counts,
        local: localStorage.length,
        session: sessionStorage.length,
        noQuery: location.search === "",
        noHash: location.hash === "",
      };
    });
    expect(privacy.storageWrites).toBe(0);
    expect(privacy.databaseOpens).toBe(0);
    expect(privacy.cacheOpens).toBe(0);
    expect(privacy.local).toBe(0);
    expect(privacy.session).toBe(0);
    expect(privacy.noQuery).toBe(true);
    expect(privacy.noHash).toBe(true);
    wires(fixture, 1);
    expect(fixture.resumeCount).toBe(0);
  });
});
