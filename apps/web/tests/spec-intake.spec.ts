import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { createHash, randomUUID } from "node:crypto";
import {
  synthetic,
  syntheticCatalog,
  projectPath,
  catalogApi,
} from "./catalog.fixture";
import {
  evidence,
  inventory,
  recovery,
  type Inventory,
  type Upload,
  type SpecObject,
} from "../src/catalog/specs/spec-model";
const id = "50000000-0000-4000-8000-000000000001";

for (const failure of ["503", "invalid"]) {
  test(`capabilities ${failure} preserves authorized inventory and reads; refresh recovers intake`, async ({
    page,
  }) => {
    await setup(page);
    await create(page);
    await page
      .getByLabel("Or paste text (1 MiB request maximum)")
      .fill("retained source text");
    await page.getByRole("button", { name: "Save pasted text" }).click();
    await expect(page.getByText("Original bytes retained.")).toBeVisible();
    await page.route("**/capabilities", (route) =>
      route.fulfill(
        failure === "503"
          ? { status: 503, json: { code: "Unavailable" } }
          : { json: { version: 99, formats: [] } },
      ),
    );
    await page
      .getByRole("button", { name: "Refresh inventory and access" })
      .click();
    await expect(
      page.getByText(
        /New intake is unavailable because server capabilities could not be confirmed/,
      ),
    ).toBeVisible();
    await expect(page.getByText("Original bytes retained.")).toBeVisible();
    await expect(page.getByLabel("Specification file")).toBeDisabled();
    await expect(
      page.getByLabel("Or paste text (1 MiB request maximum)"),
    ).toBeDisabled();
    await page
      .getByRole("button", { name: "Read extraction", exact: true })
      .click();
    await expect(page.locator(".citation pre")).toHaveText(
      "retained source text",
    );
    await page.unroute("**/capabilities");
    await page
      .getByRole("button", { name: "Refresh inventory and access" })
      .click();
    await expect(page.getByLabel("Specification file")).toBeEnabled();
    await expect(
      page.getByLabel("Or paste text (1 MiB request maximum)"),
    ).toBeEnabled();
    await expect(
      page.getByText(
        /New intake is unavailable because server capabilities could not be confirmed/,
      ),
    ).toHaveCount(0);
  });
}

for (const status of [401, 403]) {
  test(`capability authentication refusal ${status} still clears private inventory`, async ({
    page,
  }) => {
    await setup(page);
    await create(page);
    await page
      .getByLabel("Or paste text (1 MiB request maximum)")
      .fill("private source");
    await page.getByRole("button", { name: "Save pasted text" }).click();
    await page
      .getByRole("button", { name: "Read extraction", exact: true })
      .click();
    await expect(page.locator(".citation pre")).toHaveText("private source");
    await page.route("**/capabilities", (route) =>
      route.fulfill({ status, json: { code: "IdentityRejected" } }),
    );
    await page
      .getByRole("button", { name: "Refresh inventory and access" })
      .click();
    await expect(
      page.getByText(/Sign-in or content authorization needs attention/),
    ).toBeVisible();
    await expect(page.getByText("Original bytes retained.")).toHaveCount(0);
    await expect(page.locator(".citation")).toHaveCount(0);
  });
}

for (const format of ["docx", "xlsx", "pdf"]) {
  test(`server-admitted ${format} upload preserves explicit format and Partial receipt`, async ({
    page,
  }) => {
    const state = await setup(page);
    state.formats.push(format);
    await create(page);
    await expect(
      page.locator(`#spec-format option[value="${format}"]`),
    ).toHaveCount(1);
    await page.getByLabel("Specification file").setInputFiles({
      name: `source.${format}`,
      mimeType: "application/octet-stream",
      buffer: Buffer.from("captured bytes"),
    });
    await expect(page.getByLabel("Extraction format")).toHaveValue(format);
    await page.getByRole("button", { name: "Upload selected file" }).click();
    await expect(
      page.getByText(
        "Partial structural extraction — document content and layout may be missing",
      ),
    ).toBeVisible();
    expect(state.writes[0]!.format).toBe(format);
    await page
      .getByRole("button", { name: "Read extraction", exact: true })
      .click();
    await expect(page.locator(".citation pre")).toContainText("fixture text");
    await expect(
      page.getByRole("heading", { name: "Retained request" }),
    ).toHaveCount(0);
  });
}

test("explicit admitted format supports a source without a recognized extension", async ({
  page,
}) => {
  const state = await setup(page);
  state.formats.push("pdf");
  await create(page);
  await page.getByLabel("Extraction format").selectOption("pdf");
  await page.getByLabel("Specification file").setInputFiles({
    name: "source.data",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("captured PDF bytes"),
  });
  await expect(page.getByLabel("Extraction format")).toHaveValue("pdf");
  await page.getByRole("button", { name: "Upload selected file" }).click();
  await expect(page.getByText("Original bytes retained.")).toBeVisible();
  expect(state.writes[0]!.format).toBe("pdf");
});

test("revoked format capability prevents a pending document upload", async ({
  page,
}) => {
  const state = await setup(page);
  state.formats.push("pdf");
  await create(page);
  await page.getByLabel("Specification file").setInputFiles({
    name: "source.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF"),
  });
  state.formats = ["text", "json", "openapi-json"];
  await page.getByRole("button", { name: "Upload selected file" }).click();
  await expect(
    page.getByText(/Specification intake is unavailable/),
  ).toBeVisible();
  expect(state.writes).toHaveLength(0);
  await expect(page.locator('#spec-format option[value="pdf"]')).toHaveCount(0);
});

for (const status of ["needsOcr", "encrypted"]) {
  test(`PDF ${status} remains an explicit refusal`, async ({ page }) => {
    const state = await setup(page);
    state.formats.push("pdf");
    state.pdfStatus = status;
    await create(page);
    await page.getByLabel("Specification file").setInputFiles({
      name: "source.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF"),
    });
    await page.getByRole("button", { name: "Upload selected file" }).click();
    await expect(
      page.getByText("Extraction unsupported — original retained"),
    ).toBeVisible();
    await page
      .getByRole("button", { name: "Read extraction", exact: true })
      .click();
    await expect(
      page.getByText(
        status === "needsOcr"
          ? "Diagnostic: pdf-needs-ocr"
          : "Diagnostic: pdf-encrypted",
        { exact: true },
      ),
    ).toBeVisible();
    if (status === "needsOcr")
      await expect(
        page.getByText("Missing text on this page. No OCR was performed."),
      ).toBeVisible();
    else await expect(page.locator(".citation")).toHaveCount(0);
  });
}

test("source HTML and URLs are rendered as inert text", async ({ page }) => {
  await setup(page);
  await create(page);
  const text = "<img src=x onerror=alert(1)> https://inert.invalid";
  await page.getByLabel("Or paste text (1 MiB request maximum)").fill(text);
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await expect(page.locator(".citation pre")).toHaveText(text);
  await expect(page.locator(".citation img, .citation a")).toHaveCount(0);
});

for (const delayed of [false, true]) {
  test(`project metadata denial clears evidence and fences prior reads (delayed=${delayed})`, async ({
    page,
  }) => {
    await setup(page);
    await create(page);
    await page
      .getByLabel("Or paste text (1 MiB request maximum)")
      .fill("private source");
    await page.getByRole("button", { name: "Save pasted text" }).click();
    await page
      .getByRole("button", { name: "Read extraction", exact: true })
      .click();
    await expect(page.locator(".citation pre")).toHaveText("private source");
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    if (delayed) {
      await page.route("**/extractions/**", async (r) => {
        await gate;
        await r.fulfill({
          json: [
            "braid.spec-evidence.v1",
            "complete",
            [0, "late private source", null, 0, 19, 1, 1],
          ],
        });
      });
      const request = page.waitForRequest("**/extractions/**");
      await page
        .getByRole("button", { name: "Read extraction", exact: true })
        .click();
      await request;
    }
    await page.route(
      `**${catalogApi(projectPath(synthetic.project.scope))}`,
      (r) => r.fulfill({ status: 404, json: { code: "NotFound" } }),
    );
    await page
      .getByRole("button", { name: "Refresh inventory and access" })
      .click();
    await expect(
      page.getByText(/Bundle unavailable or content access denied/),
    ).toBeVisible();
    if (delayed) {
      const response = page.waitForResponse("**/extractions/**");
      release();
      await (await response).finished();
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
    }
    await expect(
      page.getByRole("region", { name: "Extraction evidence" }),
    ).toHaveCount(0);
    await expect(page.locator(".citation")).toHaveCount(0);
    await expect(page.getByText("Original bytes retained.")).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Refresh inventory and access" }),
    ).toBeEnabled();
  });
}

test("lost source-version deletion acknowledgement reconciles only that version", async ({
  page,
}) => {
  const state = await setup(page);
  await create(page);
  await page
    .getByLabel("Or paste text (1 MiB request maximum)")
    .fill("version one");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await expect(page.getByText("Original bytes retained.")).toBeVisible();
  state.data!.objects.push(
    ...state.data!.objects.map((o) => ({
      ...o,
      objectId: randomUUID(),
      sourceVersion: 2,
    })),
  );
  state.data!.bundle.version += 2;
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(page.getByText("Original bytes retained.")).toHaveCount(2);
  state.uncertainDelete = true;
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "Delete source version", exact: true })
    .first()
    .click();
  await expect(page.getByText(/Request could not be confirmed/)).toBeVisible();
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText(
      "Source deletion confirmed; physical retirement may still be pending.",
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Retained request" }),
  ).toHaveCount(0);
  await expect(page.getByText("Original bytes retained.")).toHaveCount(1);
  expect(
    state
      .data!.objects.filter((o) => o.sourceVersion === 1)
      .every((o) => o.state === "retiring"),
  ).toBe(true);
  expect(
    state
      .data!.objects.filter((o) => o.sourceVersion === 2)
      .every((o) => o.state === "published"),
  ).toBe(true);
});
test("strict evidence and inventory parsers refuse untrusted shapes", () => {
  expect(() =>
    evidence([
      "braid.spec-evidence.v1",
      "complete",
      [0, "text", null, -1, 1, 1, 1],
    ]),
  ).toThrow();
  expect(() =>
    evidence([
      "braid.spec-evidence.v1",
      "complete",
      [0, "text", null, 0, 1, 2, 1],
    ]),
  ).toThrow();
  expect(
    evidence(["braid.spec-evidence.v1", "serialized-evidence-bytes"]).chunks,
  ).toEqual([]);
  expect(() =>
    inventory(
      {
        bundle: { bundleId: id, version: Infinity, tombstoned: false },
        objects: [],
      },
      id,
    ),
  ).toThrow();
  expect(() =>
    inventory(
      { bundle: { bundleId: id, version: 1, tombstoned: false }, objects: [] },
      synthetic.org,
    ),
  ).toThrow();
});
async function setup(page: Page) {
  await syntheticCatalog(page);
  const state = {
    data: null as Inventory | null,
    writes: [] as Upload[],
    uncertain: false,
    conflict: false,
    unavailable: false,
    actor: synthetic.actor,
    deleted: false,
    uncertainDelete: false,
    evidence: new Map<string, unknown[]>(),
    evidenceBodies: new Map<string, string>(),
    formats: ["text", "json", "openapi-json"],
    pdfStatus: "partial",
  };
  await page.route("**/api/v1/catalog/context", (r) =>
    r.fulfill({
      json: { ...synthetic.context, actorPrincipalId: state.actor },
    }),
  );
  await page.route("**/spec-bundles/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const bundle = url.pathname.split("/spec-bundles/")[1]!.split("/")[0]!;
    if (state.unavailable)
      return route.fulfill({ status: 503, json: { code: "Unavailable" } });
    if (request.method() === "PUT") {
      state.data ??= {
        bundle: { bundleId: bundle, version: 1, tombstoned: false },
        objects: [],
      };
      return route.fulfill({ json: state.data.bundle });
    }
    if (!state.data)
      return route.fulfill({ status: 404, json: { code: "NotFound" } });
    if (url.pathname.endsWith("/capabilities"))
      return route.fulfill({
        json: {
          version: 1,
          maximumTextRequestBytes: 1048576,
          formats: state.formats.map((format) => ({
            format,
            maximumSourceBytes: format === "pdf" ? 8388608 : 10485760,
            extractorVersion:
              format === "pdf" ? 4 : ["docx", "xlsx"].includes(format) ? 3 : 2,
            evidenceFormat: ["docx", "xlsx", "pdf"].includes(format)
              ? "braid.spec-evidence.v2"
              : "braid.spec-evidence.v1",
          })),
        },
      });
    if (request.method() === "DELETE") {
      expect(request.headers()["idempotency-key"]).toBeTruthy();
      if (
        Number(request.headers()["if-match"]?.replaceAll('"', "")) !==
        state.data.bundle.version
      )
        return route.fulfill({ status: 409, json: { code: "Conflict" } });
      state.deleted = true;
      state.data.bundle.version++;
      state.data.bundle.tombstoned = !url.pathname.includes("/sources/");
      const source = url.pathname.split("/sources/")[1]?.split("/versions/");
      for (const object of state.data.objects) {
        if (
          !source ||
          (object.sourceId === source[0] &&
            object.sourceVersion === Number(source[1]))
        )
          object.state = "retiring";
      }
      if (state.uncertainDelete) {
        state.uncertainDelete = false;
        return route.abort("failed");
      }
      return route.fulfill({ json: state.data.bundle });
    }
    if (request.method() === "POST") {
      const upload: Upload = url.pathname.endsWith("/text")
        ? request.postDataJSON().upload
        : JSON.parse(request.headers()["x-braid-spec-upload"]!);
      state.writes.push(upload);
      expect(request.headers()["x-xsrf-token"]).toBe(
        synthetic.context.requestToken,
      );
      if (state.conflict) {
        state.conflict = false;
        return route.fulfill({ status: 409, json: { code: "Conflict" } });
      }
      const bytes = url.pathname.endsWith("/text")
        ? Buffer.from(request.postDataJSON().text, "utf8")
        : request.postDataBuffer()!;
      const text = bytes.toString("utf8");
      let offset = 0;
      let extraction: unknown[] = [
        "braid.spec-evidence.v1",
        "complete",
        ...text.split("\n").map((line, ordinal) => {
          const length = Buffer.byteLength(line, "utf8");
          const chunk = [
            ordinal,
            line,
            null,
            offset,
            length,
            ordinal + 1,
            ordinal + 1,
          ];
          offset += length + 1;
          return chunk;
        }),
      ];
      const structural = ["docx", "xlsx", "pdf"].includes(upload.format);
      if (structural)
        extraction = [
          "braid.spec-evidence.v2",
          upload.format === "docx"
            ? "office-body-text-only"
            : upload.format === "xlsx"
              ? "office-stored-cells-only"
              : "pdf-page-text-only",
          upload.format === "docx"
            ? [
                0,
                "Word fixture text",
                ["docx-paragraph", "word/document.xml", 1, null, null, null],
                null,
              ]
            : upload.format === "xlsx"
              ? [
                  0,
                  "Excel fixture text",
                  [
                    "xlsx-cell",
                    "xl/worksheets/sheet1.xml",
                    "Sheet",
                    "A1",
                    "String",
                    false,
                  ],
                  null,
                ]
              : [0, "PDF fixture text", ["pdf-page", 1], null, false],
        ];
      if (upload.format === "pdf" && state.pdfStatus !== "partial")
        extraction =
          state.pdfStatus === "needsOcr"
            ? [
                "braid.spec-evidence.v2",
                "pdf-needs-ocr",
                [0, "", ["pdf-page", 1], null, true],
              ]
            : ["braid.spec-evidence.v2", "pdf-encrypted"];
      const resultOutcome =
        upload.format === "pdf" && state.pdfStatus !== "partial"
          ? "Unsupported"
          : structural
            ? "Partial"
            : "Complete";
      // Utf8JsonWriter's canonical Unicode escaping for the multibyte fixture.
      const extractionBody =
        text === "café\n東京"
          ? '["braid.spec-evidence.v1","complete",[0,"caf\\u00E9",null,0,5,1,1],[1,"\\u6771\\u4EAC",null,6,6,2,2]]'
          : JSON.stringify(extraction);
      const extractionBytes = Buffer.from(extractionBody);
      state.evidence.set(upload.extractionObjectId, extraction);
      state.evidenceBodies.set(upload.extractionObjectId, extractionBody);
      const original: SpecObject = {
        objectId: upload.originalObjectId,
        sourceId: upload.sourceId,
        sourceVersion: 1,
        kind: "Original",
        format:
          upload.format === "docx"
            ? "Docx"
            : upload.format === "xlsx"
              ? "Xlsx"
              : upload.format === "pdf"
                ? "Pdf"
                : "Text",
        digest: createHash("sha256").update(bytes).digest("hex"),
        byteCount: bytes.length,
        state: "published",
        outcome: null,
      };
      state.data.objects.push(original, {
        ...original,
        objectId: upload.extractionObjectId,
        kind: "Extraction",
        digest: createHash("sha256").update(extractionBytes).digest("hex"),
        byteCount: extractionBytes.length,
        outcome: resultOutcome,
      });
      state.data.bundle.version += 2;
      if (state.uncertain) {
        state.uncertain = false;
        return route.abort("failed");
      }
      return route.fulfill({
        json: {
          originalObjectId: upload.originalObjectId,
          extractionObjectId: upload.extractionObjectId,
          reservationVersion: state.data.bundle.version,
          outcome: resultOutcome,
        },
      });
    }
    if (url.pathname.includes("/extractions/"))
      return route.fulfill({
        contentType: "application/json",
        body: state.evidenceBodies.get(url.pathname.split("/extractions/")[1]!),
      });
    return route.fulfill({ json: state.data });
  });
  return state;
}
async function create(page: Page) {
  await page.goto(projectPath(synthetic.project.scope));
  await page.getByRole("link", { name: "Open specification intake" }).click();
  await page.getByRole("button", { name: "Start new bundle" }).click();
  await page.getByRole("button", { name: "Create this bundle" }).click();
  await expect(
    page.getByText("No source objects have been reserved yet."),
  ).toBeVisible();
}

// Synthetic future publication, not an upload-capability or backend qualification.
async function structural(
  page: Page,
  format: "Docx" | "Xlsx",
  value: unknown[],
) {
  const state = await setup(page);
  await create(page);
  const body = JSON.stringify(value);
  const objectId = randomUUID();
  state.data!.objects.push({
    objectId,
    sourceId: randomUUID(),
    sourceVersion: 1,
    kind: "Extraction",
    format,
    digest: createHash("sha256")
      .update(Buffer.from(body, "utf8"))
      .digest("hex"),
    byteCount: Buffer.byteLength(body, "utf8"),
    state: "published",
    outcome: "Partial",
  });
  state.evidenceBodies.set(objectId, body);
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText(
      "Partial structural extraction — document content and layout may be missing",
      { exact: true },
    ),
  ).toBeVisible();
  return { state, objectId, body };
}

test("v2 DOCX body and table citations stay inert, accessible and upload admission stays server-aligned", async ({
  page,
}) => {
  const source = "<img src=x onerror=alert(1)> café 東京 https://inert.invalid";
  const { state } = await structural(page, "Docx", [
    "braid.spec-evidence.v2",
    "office-body-text-only",
    [
      0,
      source,
      ["docx-paragraph", "word/document.xml", 2, null, null, null],
      null,
    ],
    [
      1,
      "table text",
      ["docx-paragraph", "word/document.xml", 3, 1, 2, 4],
      null,
    ],
  ]);
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await expect(page.locator(".citation .evidence-text")).toHaveText([
    source,
    "table text",
  ]);
  await expect(page.locator(".citation").first()).toContainText(
    "Body paragraph 2",
  );
  await expect(page.locator(".citation").nth(1)).toContainText(
    "Table 1 · row 2 · cell 4 · paragraph 3",
  );
  await expect(page.locator(".citation").first()).toContainText(
    "word/document.xml",
  );
  await expect(page.locator(".citation img, .citation a")).toHaveCount(0);
  await expect(page.getByText(/byte offset/)).toHaveCount(0);
  await expect(page.getByText("Evidence format v2")).toBeVisible();
  await expect(page.locator("#spec-format option")).toHaveText([
    "Plain text",
    "JSON",
    "OpenAPI JSON (structural extraction)",
  ]);
  await page.getByLabel("Specification file").setInputFiles({
    name: "guide.docx",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("PK"),
  });
  await expect(page.getByText(/The file was not sent/)).toBeVisible();
  expect(state.writes).toHaveLength(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  expect(
    (await new AxeBuilder({ page }).include(".catalog-page").analyze())
      .violations,
  ).toEqual([]);
});

test("v2 XLSX displays literal formulas, missing caches, empty caches and raw stored cells distinctly", async ({
  page,
}) => {
  const location = (address: string, kind: string, cache = false) => [
    "xlsx-cell",
    "xl/worksheets/sheet1.xml",
    "Données",
    address,
    kind,
    cache,
  ];
  const formula =
    'HYPERLINK("https://inert.invalid","<img src=x onerror=alert(1)>")';
  await structural(page, "Xlsx", [
    "braid.spec-evidence.v2",
    "office-stored-cells-only",
    [0, "3", location("A1", "Number", true), "SUM(1,2)"],
    [1, "", location("B1", "Blank"), formula],
    [2, "", location("C1", "String", true), '""'],
    [3, "", location("D1", "Blank"), null],
    [4, "東京", location("E1", "SharedString"), null],
  ]);
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await expect(page.locator(".citation .evidence-text")).toHaveText([
    "3",
    "",
    "",
    "",
    "東京",
  ]);
  await expect(page.locator(".citation .formula")).toHaveText([
    "SUM(1,2)",
    formula,
    '""',
  ]);
  await expect(page.locator(".citation").nth(1)).toContainText(
    "No stored formula cache; no calculated result is available.",
  );
  await expect(page.locator(".citation").nth(2)).toContainText(
    "Stored cache is an empty string.",
  );
  await expect(page.locator(".citation").nth(3)).toContainText(
    "Blank stored cell.",
  );
  await expect(page.locator(".citation").nth(4)).toContainText(
    "Sheet Données · cell E1",
  );
  await expect(page.locator(".citation").nth(0)).toContainText(
    "Stored formula cache present; may be stale.",
  );
  await expect(page.locator(".citation img, .citation a")).toHaveCount(0);
  await expect(page.getByText(/byte offset/)).toHaveCount(0);
});

test("v2 reads reject mismatched exact bytes and invalid coordinates without displaying earlier content", async ({
  page,
}) => {
  const value = [
    "braid.spec-evidence.v2",
    "office-body-text-only",
    [
      0,
      "private café",
      ["docx-paragraph", "word/document.xml", 1, null, null, null],
      null,
    ],
  ];
  const { state, objectId } = await structural(page, "Docx", value);
  const read = page.getByRole("button", {
    name: "Read extraction",
    exact: true,
  });
  await read.click();
  await expect(page.locator(".citation .evidence-text")).toHaveText(
    "private café",
  );
  state.evidenceBodies.set(
    objectId,
    JSON.stringify(value).replace("private café", "tampered café"),
  );
  await read.click();
  await expect(page.getByText(/Request could not be confirmed/)).toBeVisible();
  await expect(page.locator(".citation")).toHaveCount(0);
  const invalid = JSON.stringify([
    "braid.spec-evidence.v2",
    "office-body-text-only",
    [
      0,
      "must not show",
      ["docx-paragraph", "../private.xml", 1, null, null, null],
      null,
    ],
  ]);
  state.evidenceBodies.set(objectId, invalid);
  state.data!.objects[0]!.byteCount = Buffer.byteLength(invalid);
  state.data!.objects[0]!.digest = createHash("sha256")
    .update(invalid)
    .digest("hex");
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await read.click();
  await expect(page.getByText(/Request could not be confirmed/)).toBeVisible();
  await expect(page.locator(".citation")).toHaveCount(0);
});

test("revoked project scope fences an otherwise valid late v2 response", async ({
  page,
}) => {
  const { body } = await structural(page, "Docx", [
    "braid.spec-evidence.v2",
    "office-body-text-only",
    [
      0,
      "late private structural source",
      ["docx-paragraph", "word/document.xml", 1, null, null, null],
      null,
    ],
  ]);
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/extractions/**", async (route) => {
    await gate;
    await route.fulfill({ contentType: "application/json", body });
  });
  const request = page.waitForRequest("**/extractions/**");
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await request;
  await page.route(
    `**${catalogApi(projectPath(synthetic.project.scope))}`,
    (route) => route.fulfill({ status: 404, json: { code: "NotFound" } }),
  );
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText(/Bundle unavailable or content access denied/),
  ).toBeVisible();
  const response = page.waitForResponse("**/extractions/**");
  release();
  await (await response).finished();
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
  await expect(
    page.getByRole("region", { name: "Extraction evidence" }),
  ).toHaveCount(0);
  await expect(page.getByText("late private structural source")).toHaveCount(0);
});
test("project entry, real Chromium upload/paste/citation/delete and reload", async ({
  page,
}) => {
  const state = await setup(page);
  await create(page);
  const url = page.url();
  await page.getByLabel("Specification file").setInputFiles({
    name: "guide.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("café\n東京", "utf8"),
  });
  await page.getByRole("button", { name: "Upload selected file" }).click();
  await expect(page.getByText("Original bytes retained.")).toBeVisible();
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await expect(page.locator(".citation pre")).toHaveText(["café", "東京"]);
  await expect(page.locator(".citation").nth(0)).toContainText(
    "lines 1–1 · byte offset 0, length 5",
  );
  await expect(page.locator(".citation").nth(1)).toContainText(
    "lines 2–2 · byte offset 6, length 6",
  );
  expect(state.data!.objects[0]!.byteCount).toBe(12);
  const body = state.evidenceBodies.get(state.writes[0]!.extractionObjectId)!;
  expect(JSON.parse(body)).toEqual(
    state.evidence.get(state.writes[0]!.extractionObjectId),
  );
  const chunks = evidence(JSON.parse(body)).chunks;
  expect(
    chunks.map((c) =>
      c.locator.kind === "byte-range"
        ? [c.locator.offset, c.locator.length]
        : null,
    ),
  ).toEqual([
    [0, 5],
    [6, 6],
  ]);
  for (const chunk of chunks) {
    if (chunk.locator.kind !== "byte-range")
      throw new Error("Expected v1 bytes");
    expect(
      Buffer.from("café\n東京", "utf8").subarray(
        chunk.locator.offset,
        chunk.locator.offset + chunk.locator.length,
      ),
    ).toEqual(Buffer.from(chunk.text, "utf8"));
  }
  const digest = createHash("sha256").update(Buffer.from(body)).digest("hex");
  expect(state.data!.objects[1]!.digest).toBe(digest);
  expect(state.data!.objects[1]!.byteCount).toBe(Buffer.byteLength(body));
  await expect(
    page.getByRole("region", { name: "Extraction evidence" }),
  ).toContainText(digest);
  await page.getByLabel("Or paste text (1 MiB request maximum)").fill("paste");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await expect(page.getByText("Original bytes retained.")).toHaveCount(2);
  await page.reload();
  expect(page.url()).toBe(url);
  await expect(page.getByText("Original bytes retained.")).toHaveCount(2);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  expect(
    (await new AxeBuilder({ page }).include(".catalog-page").analyze())
      .violations,
  ).toEqual([]);
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "Delete bundle", exact: true })
    .click();
  await expect(
    page.getByText(
      "Bundle deletion acknowledged. Physical retirement may still be pending.",
    ),
  ).toBeVisible();
  expect(state.deleted).toBe(true);
});
test("ambiguous upload reconciles inventory without allocating another attempt", async ({
  page,
}) => {
  const state = await setup(page);
  await create(page);
  state.uncertain = true;
  await page
    .getByLabel("Or paste text (1 MiB request maximum)")
    .fill("private");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await expect(
    page.getByRole("heading", { name: "Retained request" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Refresh inventory and access" }),
  ).toBeEnabled();
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText("Both publications confirmed in inventory."),
  ).toBeVisible();
  expect(state.writes).toHaveLength(1);
});
test("CAS retry retains IDs; owner change clears private draft; unsupported and unavailable explicit", async ({
  page,
}) => {
  const state = await setup(page);
  await create(page);
  await page.getByLabel("Specification file").setInputFiles({
    name: "guide.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("pdf"),
  });
  await expect(page.getByText(/The file was not sent/)).toBeVisible();
  expect(state.writes).toHaveLength(0);
  state.conflict = true;
  await page.getByLabel("Or paste text (1 MiB request maximum)").fill("secret");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await expect(page.getByText(/Version conflict/)).toBeVisible();
  await page.getByRole("button", { name: "Retry retained request" }).click();
  await expect(page.getByText("Original bytes retained.")).toBeVisible();
  expect(state.writes[0]).toEqual(state.writes[1]);
  expect(recovery(state.writes[0]!, state.data!)).toBe("complete");
  await page
    .getByLabel("Or paste text (1 MiB request maximum)")
    .fill("owner-private");
  state.actor = id;
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText("Request could not be confirmed.", { exact: false }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByLabel("Or paste text (1 MiB request maximum)"),
  ).toHaveValue("");
  state.unavailable = true;
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText(/Specification intake is unavailable on this server/),
  ).toBeVisible();
});
test("unsent navigation can be cancelled and authentication failure hides retained content", async ({
  page,
}) => {
  await setup(page);
  await create(page);
  const url = page.url();
  await page
    .getByLabel("Or paste text (1 MiB request maximum)")
    .fill("unsent private evidence");
  page.once("dialog", (d) => d.dismiss());
  await page.getByRole("link", { name: "Project", exact: true }).click();
  expect(page.url()).toBe(url);
  await expect(
    page.getByLabel("Or paste text (1 MiB request maximum)"),
  ).toHaveValue("unsent private evidence");
  await page.route("**/api/v1/catalog/context", (r) =>
    r.fulfill({ status: 401, json: { code: "IdentityRejected" } }),
  );
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByLabel("Or paste text (1 MiB request maximum)"),
  ).toHaveCount(0);
  await expect(page.getByText("unsent private evidence")).toHaveCount(0);
});

test("failed extraction outcomes remain explicit and foreign organization routes dispatch no bundle reads", async ({
  page,
}) => {
  const state = await setup(page);
  await create(page);
  await page.getByLabel("Or paste text (1 MiB request maximum)").fill("source");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await expect(page.getByText("Original bytes retained.")).toBeVisible();
  for (const [value, message] of [
    [
      "LimitExceeded",
      "Extraction limit exceeded — no partial evidence; original retained",
    ],
    ["Malformed", "Could not parse the source — original retained"],
    ["Unsupported", "Extraction unsupported — original retained"],
  ] as const) {
    state.data!.objects[1]!.outcome = value;
    await page
      .getByRole("button", { name: "Refresh inventory and access" })
      .click();
    await expect(page.getByText(message, { exact: true })).toBeVisible();
  }
  let foreignReads = 0;
  await page.route(
    `**/organizations/${id}/projects/**/spec-bundles/**`,
    (r) => {
      foreignReads++;
      return r.fulfill({ status: 500, json: { code: "Unavailable" } });
    },
  );
  await page.goto(page.url().replace(synthetic.org, id));
  await expect(
    page.getByRole("button", { name: "Refresh inventory and access" }),
  ).toBeEnabled();
  await expect(
    page.getByLabel("Or paste text (1 MiB request maximum)"),
  ).toHaveCount(0);
  expect(foreignReads).toBe(0);
});

test("a content refusal clears previously displayed citations", async ({
  page,
}) => {
  await setup(page);
  await create(page);
  await page.getByLabel("Or paste text (1 MiB request maximum)").fill("source");
  await page.getByRole("button", { name: "Save pasted text" }).click();
  await page
    .getByRole("button", { name: "Read extraction", exact: true })
    .click();
  await expect(page.locator(".citation pre")).toBeVisible();
  await page.route("**/spec-bundles/**", (r) =>
    r.fulfill({ status: 404, json: { code: "NotFound" } }),
  );
  await page
    .getByRole("button", { name: "Refresh inventory and access" })
    .click();
  await expect(
    page.getByText(/Bundle unavailable or content access denied/),
  ).toBeVisible();
  await expect(page.locator(".citation")).toHaveCount(0);
});

for (const uncertain of [false, true]) {
  test(`upload preserves unrelated drafts and navigation protection (uncertain=${uncertain})`, async ({
    page,
  }) => {
    const state = await setup(page);
    await create(page);
    const url = page.url();
    const draft = page.getByLabel("Or paste text (1 MiB request maximum)");
    const file = page.getByLabel("Specification file");
    await draft.fill("unsent pasted draft");
    await file.setInputFiles({
      name: "guide.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("text"),
    });
    state.uncertain = uncertain;
    await page.getByRole("button", { name: "Upload selected file" }).click();
    if (uncertain) {
      await expect(
        page.getByText(/Request could not be confirmed/),
      ).toBeVisible();
      await page
        .getByRole("button", { name: "Refresh inventory and access" })
        .click();
      await expect(
        page.getByText("Both publications confirmed in inventory."),
      ).toBeVisible();
    }
    await expect(page.getByText("Original bytes retained.")).toBeVisible();
    await expect(draft).toHaveValue("unsent pasted draft");
    await expect(
      page.getByRole("button", { name: "Upload selected file" }),
    ).toBeDisabled();
    page.once("dialog", (d) => d.dismiss());
    await page.getByRole("link", { name: "Project", exact: true }).click();
    expect(page.url()).toBe(url);
    await expect(draft).toHaveValue("unsent pasted draft");
    await file.setInputFiles({
      name: "next.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("next"),
    });
    await page.getByRole("button", { name: "Save pasted text" }).click();
    await expect(page.getByText("Original bytes retained.")).toHaveCount(2);
    await expect(draft).toHaveValue("");
    await expect(
      page.getByRole("button", { name: "Upload selected file" }),
    ).toBeEnabled();
    page.once("dialog", (d) => d.dismiss());
    await page.getByRole("link", { name: "Project", exact: true }).click();
    expect(page.url()).toBe(url);
    expect(state.writes).toHaveLength(2);
  });
}
