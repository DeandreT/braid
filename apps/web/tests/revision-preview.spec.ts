import { expect, test, type Page, type Route } from "@playwright/test";
import { createHash } from "node:crypto";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  mappingPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";

type Run = components["schemas"]["CatalogPreviewRunV1"];
type Submission = components["schemas"]["CatalogPreviewSubmissionV1"];

const scope = synthetic.revision.scope;
const RUN = "80000000-0000-4000-8000-000000000001";
const submitPath = `${catalogApi(revisionPath(scope))}/previews`;
const runPath = `/api/v1/organizations/${scope.organizationId}/previews/${RUN}`;
const output = '{"orders":[{"id":"SO-1","qty":9007199254740993}]}';
const outputDigest = createHash("sha256").update(output).digest("hex");

function run(state: Run["state"], change: Partial<Run> = {}): Run {
  const terminal = state === "completed" || state === "canceled";
  return {
    runId: RUN,
    revision: scope,
    subjectKind: "revision",
    draft: null,
    requestFingerprint: "b".repeat(64),
    currentDateTime: "2026-10-20T08:30:00Z",
    state,
    attempt: state === "queued" ? 0 : 1,
    maxAttempts: 3,
    cancelRequested: false,
    outcome: state === "canceled" ? "canceled" : null,
    failure: null,
    provenance: null,
    recordsWritten: null,
    outputs: [],
    createdAtUtc: "2026-10-20T08:30:00Z",
    startedAtUtc: state === "queued" ? null : "2026-10-20T08:30:01Z",
    finishedAtUtc: terminal ? "2026-10-20T08:30:02Z" : null,
    expiresAtUtc: "2026-10-21T08:30:00Z",
    ...change,
  };
}

const succeeded = run("completed", {
  outcome: "succeeded",
  recordsWritten: "1",
  provenance: {
    confirmed: true,
    engineRevision: "740af0c38ede7d814a17146bab2bffd7301320c9",
    backendBuild: "sha256:" + "c".repeat(64),
    adapterBuild: "braid.ferrule-preview-adapter.v1",
    profile: "braid.ferrule-json-preview.v1",
  },
  outputs: [
    {
      ordinal: 0,
      boundaryId: "orders",
      targetName: "",
      targetNameTruncated: false,
      mediaType: "application/json",
      recordsWritten: "1",
      sha256: outputDigest,
      byteCount: Buffer.byteLength(output),
    },
  ],
});

/** A controlled preview API: each GET of the run returns the next scripted state. */
class Previews {
  outputBytes = output;
  outputMedia = "application/json";
  readonly submissions: { key: string; body: Submission }[] = [];
  cancels = 0;
  reads = 0;
  submitReply: { status: number; json: unknown } | null = null;
  states: Run[] = [run("running"), succeeded];
  token = synthetic.context.requestToken;

  constructor(private readonly page: Page) {}

  async install(
    role = "publisher",
    allowed = true,
    enabled = true,
    xml = false,
  ) {
    const context = structuredClone(synthetic.context);
    context.features.preview = enabled;
    context.features.xmlPreview = enabled && xml;
    const mapping = structuredClone(synthetic.mapping);
    mapping.access = {
      role: role as typeof mapping.access.role,
      actions: { ...mapping.access.actions, preview: allowed },
    };
    await syntheticCatalog(this.page, {
      "/api/v1/catalog/context": context,
      [catalogApi(mappingPath(mapping.scope))]: mapping,
    });
    await this.page.route(`**${submitPath}`, (route) => this.submit(route));
    await this.page.route(`**${runPath}**`, (route) => this.run(route));
  }

  private async submit(route: Route) {
    const request = route.request();
    const headers = request.headers();
    expect(request.method()).toBe("POST");
    expect(headers["content-type"]).toContain("application/json");
    expect(headers["x-xsrf-token"]).toBe(this.token);
    const key = headers["idempotency-key"];
    expect(key).toMatch(/^[0-9a-f-]{36}$/);
    this.submissions.push({ key, body: request.postDataJSON() as Submission });
    if (this.submitReply) {
      await route.fulfill(this.submitReply);
      return;
    }
    const replay =
      this.submissions.filter((item) => item.key === key).length > 1;
    await route.fulfill({
      status: replay ? 200 : 201,
      headers: replay ? {} : { Location: runPath },
      json: { outcome: replay ? "replayed" : "created", run: run("queued") },
    });
  }

  private async run(route: Route) {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "POST" && path === `${runPath}/cancel`) {
      this.cancels++;
      this.states = [run("canceled")];
      await route.fulfill({
        json: { outcome: "canceled", run: run("canceled") },
      });
      return;
    }
    if (request.method() === "GET" && path === `${runPath}/outputs/0`) {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": this.outputMedia },
        body: this.outputBytes,
      });
      return;
    }
    if (request.method() === "GET" && path === runPath) {
      this.reads++;
      const state =
        this.states.length > 1 ? this.states.shift()! : this.states[0];
      await route.fulfill({ json: state });
      return;
    }
    await route.fulfill({ status: 404, json: { code: "notFound" } });
  }
}

const panel = (page: Page) =>
  page.getByRole("region", { name: "Sample preview", exact: true });
const documentView = (page: Page) =>
  page.getByRole("region", { name: "Document bytes", exact: true });

test("XML preview is explicit, escaped and read only without DOM resource fetching", async ({
  page,
}) => {
  const previews = new Previews(page);
  const bytes =
    '<?xml-stylesheet href="https://never-fetch.invalid/style"?><Order><![CDATA[<script>alert(1)</script>]]><code>A</code></Order>\r\n';
  let fetched = false;
  await page.route("https://never-fetch.invalid/**", async (route) => {
    fetched = true;
    await route.abort();
  });
  previews.outputBytes = bytes;
  previews.outputMedia = "application/xml";
  previews.states = [
    run("completed", {
      outcome: "succeeded",
      recordsWritten: "1",
      provenance: {
        ...succeeded.provenance!,
        profile: "braid.ferrule-xml-preview.v1",
        adapterBuild: "braid.ferrule-xml-preview-adapter.v1",
      },
      outputs: [
        {
          ...succeeded.outputs[0]!,
          boundaryId: "primary-output",
          targetName: "Order",
          mediaType: "application/xml",
          byteCount: Buffer.byteLength(bytes),
          sha256: createHash("sha256").update(bytes).digest("hex"),
          xmlMetadata: {
            schema: "braid.xml-boundary.v1",
            safetyProfile: "braid.xml-safe.v1",
            schemaHints: {
              noNamespaceLocation: "https://never-fetch.invalid/schema",
              locations: [],
            },
          },
        },
      ],
    }),
  ];
  await previews.install("publisher", true, true, true);
  await open(page);
  const region = panel(page);
  await region.getByLabel("Preview admission").selectOption("xml");
  await region.getByLabel("Primary input (XML)").fill("<Order/>");
  await region
    .getByRole("button", { name: "Run preview", exact: true })
    .click();
  await region.getByRole("button", { name: "Show Order", exact: true }).click();
  expect(previews.submissions[0]!.body.profile).toBe(
    "braid.ferrule-xml-preview.v1",
  );
  await expect(
    documentView(page).getByRole("list", {
      name: "Read-only XML lexical outline",
    }),
  ).toBeVisible();
  await expect(
    documentView(page).locator("script,iframe,img,object,embed"),
  ).toHaveCount(0);
  await expect(region.getByRole("button", { name: /Save.*case/i })).toHaveCount(
    0,
  );
  await documentView(page)
    .getByText("Saved XML metadata (read only)", { exact: true })
    .click();
  await expect(documentView(page)).toContainText(
    "https://never-fetch.invalid/schema",
  );
  await documentView(page)
    .getByRole("button", { name: "Raw text", exact: true })
    .click();
  await expect(documentView(page).locator("pre").last()).toHaveText(
    bytes.trimEnd(),
  );
  const downloadEvent = page.waitForEvent("download");
  await region
    .getByRole("button", { name: "Download Order", exact: true })
    .click();
  const download = await downloadEvent;
  expect(download.suggestedFilename()).toBe("preview-0-Order.xml");
  const chunks: Buffer[] = [];
  for await (const chunk of await download.createReadStream())
    chunks.push(Buffer.from(chunk));
  expect(Buffer.concat(chunks).toString()).toBe(bytes);
  expect(fetched).toBe(false);
});

test("XML preview has no option before its server capability is offered", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  await open(page);
  await expect(
    panel(page).getByLabel("Preview admission").locator('option[value="xml"]'),
  ).toHaveCount(0);
});

for (const [reason, kind, observed, limit, phrase] of [
  ["invalidXml", "invalidInput", null, null, "safe syntax policy"],
  ["depthLimit", "resourceLimit", "65", "64", "depth limit"],
  ["nodeLimit", "resourceLimit", "1000001", "1000000", "node limit"],
  ["metadataTooLarge", "resourceLimit", "32769", "32768", "metadata exceeds"],
] as const) {
  test(`XML preview ${reason} preflight refusal shows safe facts without inventing a run`, async ({
    page,
  }) => {
    const previews = new Previews(page);
    previews.submitReply = {
      status: 422,
      json: {
        code: "xmlPreflight",
        kind,
        reason,
        location: { line: 2, column: 3 },
        statistics: {
          originalBytes: 100,
          physicalNodes: 4,
          maximumDepth: 2,
          maximumAttributes: 0,
        },
        observed,
        limit,
      },
    };
    await previews.install("publisher", true, true, true);
    await open(page);
    const region = panel(page);
    await region.getByLabel("Preview admission").selectOption("xml");
    await region.getByLabel("Primary input (XML)").fill("<private-sentinel/>");
    await region
      .getByRole("button", { name: "Run preview", exact: true })
      .click();
    const alert = region.getByRole("alert");
    await expect(alert).toContainText(phrase);
    await expect(alert).toContainText("Line 2, column 3");
    await expect(alert).toContainText("No preview run was created");
    await expect(alert).not.toContainText("private-sentinel");
    if (limit) await expect(alert).toContainText("Limit " + limit);
    expect(previews.submissions).toHaveLength(1);
    expect(previews.reads).toBe(0);
    await expect(region.getByRole("button", { name: /^Show / })).toHaveCount(0);
    expect(
      await page.evaluate(() =>
        Object.keys(sessionStorage).filter((key) =>
          key.startsWith("braid.preview.v1/"),
        ),
      ),
    ).toHaveLength(0);
  });
}

for (const family of ["x12", "edifact"] as const) {
  test(`explicit document admission renders and downloads ${family} segments`, async ({
    page,
  }) => {
    const previews = new Previews(page);
    const bytes =
      family === "x12"
        ? "ISA*00*          *00*          *ZZ*BRAIDSENDER    *ZZ*BRAIDRECEIVER  *261024*1200*^*00501*000000330*0*T*:~GS*PO*BRAIDSENDER*BRAIDRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*BRAID-330~SE*3*0330~GE*1*330~IEA*1*000000330~"
        : "UNH+330+ORDERS:D:96A:UN'BGM+220+BRAID?'330'UNT+3+330'";
    const media =
      family === "x12" ? "application/edi-x12" : "application/edifact";
    previews.outputBytes = bytes;
    previews.outputMedia = media;
    previews.states = [
      run("completed", {
        ...succeeded,
        provenance: {
          ...succeeded.provenance!,
          profile: "braid.ferrule-document-preview.v1",
          adapterBuild: "braid.ferrule-document-preview-adapter.v1",
        },
        outputs: [
          {
            ...succeeded.outputs[0]!,
            mediaType: media,
            byteCount: Buffer.byteLength(bytes),
            sha256: createHash("sha256").update(bytes).digest("hex"),
          },
        ],
      }),
    ];
    await previews.install();
    await open(page);
    const region = panel(page);
    await region.getByLabel("Preview admission").selectOption("document");
    await region.getByLabel("Primary input (document)").fill(bytes);
    await region
      .getByRole("button", { name: "Run preview", exact: true })
      .click();
    await expect(
      region.getByRole("button", { name: "Show Primary output" }),
    ).toBeVisible();
    expect(previews.submissions[0]!.body.profile).toBe(
      "braid.ferrule-document-preview.v1",
    );
    expect(
      Buffer.from(
        previews.submissions[0]!.body.inputs[0]!.content,
        "base64",
      ).toString(),
    ).toBe(bytes);
    await region.getByRole("button", { name: "Show Primary output" }).click();
    await expect(
      documentView(page)
        .getByRole("list", { name: "EDI segments" })
        .getByRole("listitem"),
    ).toHaveCount(family === "x12" ? 7 : 3);
    if (process.env.BRAID_EDI_SCREENSHOT_ROOT) {
      await documentView(page).screenshot({
        path: `${process.env.BRAID_EDI_SCREENSHOT_ROOT}/preview-${family}.png`,
      });
    }
    const downloaded = page.waitForEvent("download");
    await region
      .getByRole("button", { name: "Download Primary output" })
      .click();
    const download = await downloaded;
    expect(download.suggestedFilename()).toBe(
      `preview-0-primary.${family === "x12" ? "x12" : "edi"}`,
    );
    const stream = await download.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(Buffer.from(chunk));
    expect(Buffer.concat(chunks).toString()).toBe(bytes);
    await documentView(page)
      .getByRole("button", { name: "Raw text", exact: true })
      .click();
    await expect(documentView(page).locator("pre")).toHaveText(bytes);
    await region
      .getByRole("button", { name: "Start a new preview", exact: true })
      .click();
    await expect(documentView(page)).toHaveCount(0);
  });
}

test("headerless custom X12 stays raw without guessed boundaries", async ({
  page,
}) => {
  const previews = new Previews(page);
  const bytes = "BGM|220|ONE!BGM|220|TWO!";
  previews.outputBytes = bytes;
  previews.outputMedia = "application/edi-x12";
  previews.states = [
    run("completed", {
      ...succeeded,
      provenance: {
        ...succeeded.provenance!,
        profile: "braid.ferrule-document-preview.v1",
        adapterBuild: "braid.ferrule-document-preview-adapter.v1",
      },
      outputs: [
        {
          ...succeeded.outputs[0]!,
          mediaType: previews.outputMedia,
          byteCount: Buffer.byteLength(bytes),
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      ],
    }),
  ];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Preview admission").selectOption("document");
  await region.getByLabel("Primary input (document)").fill(bytes);
  await region
    .getByRole("button", { name: "Run preview", exact: true })
    .click();
  await region.getByRole("button", { name: "Show Primary output" }).click();
  await expect(documentView(page)).toBeVisible();
  if (process.env.BRAID_EDI_SCREENSHOT_ROOT) {
    await documentView(page).screenshot({
      path: `${process.env.BRAID_EDI_SCREENSHOT_ROOT}/preview-headerless-x12.png`,
    });
  }
  await expect(
    documentView(page).getByRole("list", { name: "EDI segments" }),
  ).toHaveCount(0);
  await expect(documentView(page).locator("pre")).toHaveText(bytes);
});

async function open(page: Page) {
  await page.goto(revisionPath(scope));
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    synthetic.mapping.name,
  );
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
}

test("mixed X12 output keeps each interchange declaration and both business records", async ({
  page,
}) => {
  const first =
    "ISA*00*          *00*          *ZZ*BRAIDSENDER    *ZZ*BRAIDRECEIVER  *261024*1200*^*00501*000000330*0*T*:~GS*PO*S*R*20261024*1200*330*X*005010~ST*850*1~BEG*ONE*A:B^C:D~SE*3*1~GE*1*330~IEA*1*000000330~";
  const second = [...first.replace("ONE", "TWO")]
    .map(
      (value) =>
        (
          ({ "*": "|", ":": "~", "~": ":", "^": "!" }) as Record<string, string>
        )[value] ?? value,
    )
    .join("");
  const bytes = first + "\n" + second;
  const previews = new Previews(page);
  previews.outputBytes = bytes;
  previews.outputMedia = "application/edi-x12";
  previews.states = [
    run("completed", {
      ...succeeded,
      outputs: [
        {
          ...succeeded.outputs[0]!,
          mediaType: previews.outputMedia,
          byteCount: Buffer.byteLength(bytes),
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      ],
    }),
  ];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region
    .getByRole("button", { name: "Run preview", exact: true })
    .click();
  await region.getByRole("button", { name: "Show Primary output" }).click();
  await expect(documentView(page)).toBeVisible();
  if (process.env.BRAID_EDI_SCREENSHOT_ROOT)
    await documentView(page).screenshot({
      path: `${process.env.BRAID_EDI_SCREENSHOT_ROOT}/mixed-x12.png`,
    });
  const segments = documentView(page)
    .getByRole("list", { name: "EDI segments" })
    .getByRole("listitem");
  await expect(segments).toHaveCount(14);
  await expect(segments.nth(3)).toContainText("BEG*ONE*A:B^C:D~");
  await expect(segments.nth(10)).toContainText("BEG|TWO|A~B!C~D:");
});

test("mixed EDIFACT advice stays local in the Result viewer and raw download", async ({
  page,
}, testInfo) => {
  const first =
    "UNB+UNOC:3+S+R+261026:1200+A'UNH+MA+ORDERS:D:96A:UN'BGM+220+FIRST?'VALUE'UNT+3+MA'UNZ+1+A'";
  const second =
    "UNA'|.! ?UNB|UNOC'3|S|R|261026'1200|B?UNH|MB|ORDERS'D'96A'UN?BGM|220|SECOND!!VALUE!?LITERAL?UNT|3|MB?UNZ|1|B?";
  const bytes = first + "\r\n \t" + second;
  const previews = new Previews(page);
  previews.outputBytes = bytes;
  previews.outputMedia = "application/edifact";
  previews.states = [
    run("completed", {
      ...succeeded,
      provenance: {
        ...succeeded.provenance!,
        profile: "braid.ferrule-document-preview.v1",
        adapterBuild: "braid.ferrule-document-preview-adapter.v1",
      },
      outputs: [
        {
          ...succeeded.outputs[0]!,
          mediaType: previews.outputMedia,
          byteCount: Buffer.byteLength(bytes),
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      ],
    }),
  ];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Preview admission").selectOption("document");
  await region.getByLabel("Primary input (document)").fill(bytes);
  await region
    .getByRole("button", { name: "Run preview", exact: true })
    .click();
  await region
    .getByRole("button", { name: "Show Primary output", exact: true })
    .click();
  const segments = documentView(page).getByRole("listitem");
  await expect(segments).toHaveCount(11);
  await expect(segments.nth(5)).toHaveText("UNA'|.! ?");
  await expect(segments.nth(8)).toHaveText("BGM|220|SECOND!!VALUE!?LITERAL?");
  await page.screenshot({
    path: testInfo.outputPath("mixed-una-result.png"),
    fullPage: true,
  });
  await documentView(page)
    .getByRole("button", { name: "Raw text", exact: true })
    .click();
  await expect(documentView(page).locator("pre")).toHaveText(bytes);
  const pending = page.waitForEvent("download");
  await region
    .getByRole("button", { name: "Download Primary output", exact: true })
    .click();
  const stream = await (await pending).createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(Buffer.from(chunk));
  expect(Buffer.concat(chunks).toString()).toBe(bytes);
});

test("document preview drafts participate in the shared sign-in warning", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  const identityContext = `v1.${"A".repeat(43)}`;
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: {
        version: 1,
        mode: "session",
        state: "authenticated",
        authActionRequestToken: "YXV0aC1h",
        requestToken: "ZXBvY2gtYQ",
        identityContext,
        displayName: "Synthetic publisher",
      },
    }),
  );
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...synthetic.context,
        mode: "session",
        identityContext,
        requestToken: "ZXBvY2gtYQ",
        features: { ...synthetic.context.features, preview: true },
      },
    }),
  );
  await open(page);
  const region = panel(page);
  const account = page.locator("#account-trigger");
  const warning = page.locator("#sign-in-navigation-warning");
  await account.click();
  await expect(warning).toHaveCount(0);
  await account.click();
  await region.getByLabel("Preview admission").selectOption("document");
  await account.click();
  await expect(warning).toBeVisible();
  await account.click();
  await region.getByLabel("Preview admission").selectOption("json");
  await account.click();
  await expect(warning).toHaveCount(0);
  await account.click();
  await region.getByLabel("Primary input (JSON)").fill('{"local":"sample"}');
  await account.click();
  await expect(warning).toBeVisible();
  await account.click();
  await region.getByLabel("Primary input (JSON)").fill("");
  await account.click();
  await expect(warning).toHaveCount(0);
  expect(previews.submissions).toHaveLength(0);
});

test("a preview runs, shows its outputs and downloads their exact bytes", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  await open(page);
  const region = panel(page);
  if (process.env.BRAID_EDI_SCREENSHOT_ROOT) {
    await region.screenshot({
      path: `${process.env.BRAID_EDI_SCREENSHOT_ROOT}/preview-default.png`,
    });
  }
  await region.getByLabel("Primary input (JSON)").fill('{"id":"SO-1"}');
  await region.getByRole("button", { name: "Add named input" }).click();
  await region.getByLabel("Source name").fill("rates");
  await region.getByLabel("Named input 1 (JSON)").fill('{"currency":"EUR"}');
  await region.getByRole("button", { name: "Add parameter" }).click();
  await region.getByLabel("Name", { exact: true }).fill("region");
  await region.getByLabel("Value", { exact: true }).fill("west");
  await region.getByRole("button", { name: "Run preview" }).click();

  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);
  const body = previews.submissions[0].body;
  expect(body.target).toEqual({ kind: "all", name: null });
  expect(body.parameters).toEqual([
    { name: "region", kind: "string", value: "west" },
  ]);
  expect(
    body.inputs.map((input) => [
      input.name,
      Buffer.from(input.content, "base64").toString(),
    ]),
  ).toEqual([
    [null, '{"id":"SO-1"}'],
    ["rates", '{"currency":"EUR"}'],
  ]);
  const outputs = region.getByRole("region", { name: "Preview outputs" });
  await expect(outputs).toContainText(outputDigest);
  await expect(outputs).toContainText(String(Buffer.byteLength(output)));

  await region.getByRole("button", { name: "Show Primary output" }).click();
  await expect(
    documentView(page).getByRole("list", { name: "Read-only JSON tree" }),
  ).toContainText("9007199254740993");
  if (process.env.BRAID_DOCUMENT_VIEW_SCREENSHOT)
    await documentView(page).screenshot({
      path: process.env.BRAID_DOCUMENT_VIEW_SCREENSHOT,
    });
  await documentView(page).getByRole("button", { name: "Raw text" }).click();
  await expect(documentView(page).locator("pre")).toHaveText(output);
  const download = page.waitForEvent("download");
  await region.getByRole("button", { name: "Download Primary output" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("preview-0-primary.json");
  const stream = await file.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  expect(Buffer.concat(chunks).toString()).toBe(output);
});

for (const mode of [
  "valid",
  "corrupt",
  "overflow",
  "short",
  "leave",
  "fallback",
] as const) {
  test(`verified download: ${mode}`, async ({ page }) => {
    const previews = new Previews(page);
    previews.states = [succeeded];
    await previews.install();
    await open(page);
    const region = panel(page);
    await region.getByLabel("Primary input (JSON)").fill("{}");
    await region.getByRole("button", { name: "Run preview" }).click();
    const button = region.getByRole("button", {
      name: "Download Primary output",
    });
    await expect(button).toBeEnabled();
    await page.evaluate(
      ({ path, text, mode }) => {
        const original = window.fetch.bind(window);
        if (mode === "fallback")
          Object.defineProperty(window, "TransformStream", {
            value: undefined,
          });
        window.fetch = (input, init) => {
          if (input !== path) return original(input, init);
          const bytes = new TextEncoder().encode(text);
          const headers = {
            "Content-Type": "application/json",
            ...(mode === "fallback"
              ? { "Content-Length": String(bytes.length) }
              : {}),
          };
          return Promise.resolve(
            new Response(
              new ReadableStream<Uint8Array>({
                start(controller) {
                  controller.enqueue(bytes.slice(0, 8));
                  if (mode === "fallback") {
                    controller.enqueue(bytes.slice(8));
                    controller.close();
                    return;
                  }
                  init?.signal?.addEventListener(
                    "abort",
                    () => (document.body.dataset.transferAborted = "true"),
                    { once: true },
                  );
                  window.addEventListener(
                    "fixture-finish",
                    () => {
                      if (init?.signal?.aborted) return;
                      if (mode === "corrupt") bytes[8] ^= 1;
                      controller.enqueue(
                        bytes.slice(8, mode === "short" ? -1 : undefined),
                      );
                      if (mode === "overflow")
                        controller.enqueue(new Uint8Array([1]));
                      controller.close();
                    },
                    { once: true },
                  );
                },
              }),
              { headers },
            ),
          );
        };
        const create = URL.createObjectURL.bind(URL);
        URL.createObjectURL = (blob) => {
          document.body.dataset.urlCreated = "true";
          return create(blob);
        };
      },
      { path: `${runPath}/outputs/0`, text: output, mode },
    );
    const file =
      mode === "valid" || mode === "fallback"
        ? page.waitForEvent("download")
        : null;
    await button.click();
    if (mode !== "fallback") {
      await expect(
        region.getByRole("status").filter({ hasText: "Verifying download:" }),
      ).toContainText(`8 of ${Buffer.byteLength(output)} bytes`);
      expect(
        await page.locator("body").getAttribute("data-url-created"),
      ).toBeNull();
      if (mode === "valid" && process.env.BRAID_DOWNLOAD_SCREENSHOT)
        await region.screenshot({
          path: process.env.BRAID_DOWNLOAD_SCREENSHOT,
        });
      if (mode === "leave") {
        await page.evaluate(() => {
          history.pushState(
            {},
            "",
            "/organizations/11111111-1111-4111-8111-111111111111/projects",
          );
          window.dispatchEvent(new PopStateEvent("popstate"));
        });
        await expect(page.locator("body")).toHaveAttribute(
          "data-transfer-aborted",
          "true",
        );
      }
      await page.evaluate(() =>
        window.dispatchEvent(new Event("fixture-finish")),
      );
    }
    if (file) {
      const chunks = [];
      for await (const chunk of await (await file).createReadStream())
        chunks.push(chunk as Buffer);
      expect(Buffer.concat(chunks)).toEqual(Buffer.from(output));
    } else {
      if (mode !== "leave")
        await expect(region.getByRole("alert")).toContainText("did not match");
      expect(
        await page.locator("body").getAttribute("data-url-created"),
      ).toBeNull();
    }
  });
}

test("invalid input and unsupported mappings show bounded failures", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [
    run("completed", {
      outcome: "invalidInput",
      failure: {
        code: "preview.invalidInput",
        message: "Input is not JSON.",
        messageTruncated: false,
        subject: "rates",
        subjectTruncated: false,
      },
    }),
  ];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(region.getByRole("alert")).toContainText(
    "Enter or choose the primary JSON document.",
  );
  expect(previews.submissions).toHaveLength(0);

  await region.getByLabel("Primary input (JSON)").fill("not json");
  await region.getByRole("button", { name: "Run preview" }).click();
  const failure = region.locator(".preview-failure");
  await expect(failure).toContainText("An input is not valid for this mapping");
  await expect(failure).toContainText("rates");
  await expect(failure).toContainText("Input is not JSON.");

  previews.states = [
    run("completed", {
      outcome: "unsupportedCapability",
      failure: {
        code: "preview.unsupportedCapability",
        message: "Database sources are not supported.",
        messageTruncated: false,
        subject: null,
        subjectTruncated: false,
      },
    }),
  ];
  await region.getByRole("button", { name: "Start a new preview" }).click();
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(region.locator(".preview-failure")).toContainText(
    "The mapping uses features that preview does not support",
  );
  expect(new Set(previews.submissions.map((item) => item.key)).size).toBe(2);
});

test("a running preview can be canceled", async ({ page }) => {
  const previews = new Previews(page);
  previews.states = [run("running")];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Running" }),
  ).toBeVisible();
  await region.getByRole("button", { name: "Cancel preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Canceled" }),
  ).toBeVisible();
  expect(previews.cancels).toBe(1);
  await expect(
    region.getByRole("button", { name: "Cancel preview" }),
  ).toHaveCount(0);
});

/** Holds the next request matching `pattern` until released. */
async function hold(page: Page, pattern: string, reply: () => unknown) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrive!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  await page.route(
    pattern,
    async (route) => {
      arrive();
      await gate;
      await route.fulfill({ json: reply() });
    },
    { times: 1 },
  );
  return { release, arrived };
}

test("a preview submitted during a routine refresh waits and sends with the fresh token", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  const fresh = "synthetic-ui-only-fresh-token";
  const context = structuredClone(synthetic.context);
  context.features.preview = true;
  const held = await hold(page, "**/api/v1/catalog/context", () => ({
    ...context,
    requestToken: fresh,
  }));
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await held.arrived;
  try {
    // The form stays as it was; the submission waits for the fresh context.
    await region.getByRole("button", { name: "Run preview" }).click();
    await expect(region.getByLabel("Primary input (JSON)")).toHaveValue("{}");
    await expect(
      region.getByRole("button", { name: "Submitting…" }),
    ).toBeVisible();
    expect(previews.submissions).toHaveLength(0);
    previews.token = fresh;
  } finally {
    held.release();
  }
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);
});

test("status polling waits for a routine check instead of counting it as a failure", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [run("running")];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Running" }),
  ).toBeVisible();
  await expect.poll(() => previews.reads).toBeGreaterThan(0);
  const held = await hold(page, "**/api/auth/session", () => ({
    version: 1,
    mode: "development",
  }));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await held.arrived;
  try {
    // Several poll intervals pass while the check is pending; no status read is sent.
    const reads = previews.reads;
    await page.waitForTimeout(3000);
    expect(previews.reads).toBe(reads);
    previews.states = [succeeded];
  } finally {
    held.release();
  }
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  await expect(region.getByRole("alert")).toHaveCount(0);
});

test("a reload recovers the submitted run and an unconfirmed key is reused", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [succeeded];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();

  await page.reload();
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(
    panel(page).getByText("Showing your last preview of this revision"),
  ).toBeVisible();
  await expect(
    panel(page)
      .getByRole("status")
      .filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);

  // A submission whose answer was lost keeps its key for the next attempt after reload.
  await panel(page)
    .getByRole("button", { name: "Start a new preview" })
    .click();
  previews.submitReply = { status: 503, json: { code: "unavailable" } };
  await panel(page).getByLabel("Primary input (JSON)").fill('{"again":true}');
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  await expect(panel(page).getByRole("alert")).toContainText(
    "Previews are unavailable right now.",
  );
  await page.reload();
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(
    panel(page).getByText(
      "An earlier submission from this tab was not confirmed",
    ),
  ).toBeVisible();
  previews.submitReply = null;
  await panel(page).getByLabel("Primary input (JSON)").fill('{"again":true}');
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  await expect(
    panel(page).getByText("This preview was already submitted"),
  ).toBeVisible();
  expect(previews.submissions[2].key).toBe(previews.submissions[1].key);
});

test("viewers cannot run previews and a disabled feature shows no form", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install("viewer", false);
  await open(page);
  await expect(panel(page)).toContainText(
    "Your project role (viewer) cannot run previews.",
  );
  await expect(
    panel(page).getByRole("button", { name: "Run preview" }),
  ).toHaveCount(0);

  await page.unrouteAll({ behavior: "ignoreErrors" });
  await new Previews(page).install("publisher", true, false);
  await open(page);
  await expect(panel(page)).toHaveCount(0);
});
