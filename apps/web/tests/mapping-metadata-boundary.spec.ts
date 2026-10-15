import type { Locator, Page, Request, Route } from "@playwright/test";
import type { components } from "../src/api/generated";
import {
  test,
  expect,
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  catalogApi,
  editPath,
} from "./mapping-metadata.fixture";

// Controlled browser responses qualify UI boundaries only, never database races,
// real permission gates, durable writes, native execution or commit acknowledgements.
// Validators, request bodies and tokens remain private; diagnostics contain booleans/counts.
type Metadata = components["schemas"]["CatalogMappingMetadataV1"];
type Scope = Metadata["scope"];
type Reply = {
  status?: number;
  json?: unknown;
  headers?: Record<string, string>;
  abort?: boolean;
};
type Handler = Reply | (() => Reply | Promise<Reply>);
type Wire = {
  correctHeaders: boolean;
  closedBody: boolean;
  exactDraft: boolean;
  receivedValidator: boolean;
  ordinaryMappingValidator: boolean;
  sameFirstBody: boolean;
  sameFirstValidator: boolean;
  freshToken: boolean;
};
const clone = <T>(value: T): T => structuredClone(value);
const refusal = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
const contextPath = "/api/v1/catalog/context";
const otherActor = "10000000-0000-4000-8000-000000000083";
const foreignOrg = "90000000-0000-4000-8000-000000000083";
const baseVersion = "9007199254740993";

class ControlledMetadata {
  readonly context = {
    ...clone(synthetic.context),
    features: { ...synthetic.context.features, editMetadata: true },
  };
  readonly mapping = {
    ...clone(synthetic.mapping),
    version: "17",
    access: {
      role: "publisher" as const,
      actions: { ...synthetic.mapping.access.actions, editMetadata: true },
    },
  };
  readonly otherMapping = {
    ...clone(this.mapping),
    name: "Controlled second mapping",
    scope: {
      ...this.mapping.scope,
      mappingId: "30000000-0000-4000-8000-000000000083",
    },
    head: {
      ...this.mapping.head,
      scope: {
        ...this.mapping.head.scope,
        mappingId: "30000000-0000-4000-8000-000000000083",
      },
    },
  };
  metadata: Metadata = {
    scope: clone(this.mapping.scope),
    name: "Controlled baseline metadata",
    description: "Controlled baseline description.",
    version: baseVersion,
  };
  readonly otherMetadata: Metadata = {
    scope: clone(this.otherMapping.scope),
    name: "Controlled other metadata",
    description: "A different selected mapping.",
    version: "3",
  };
  contextReply: Handler | null = null;
  mappingReply: Handler | null = null;
  metadataReply: Handler | null = null;
  patchReply: Handler = refusal(400, "invalidRequest");
  readonly wires: Wire[] = [];
  contextReads = 0;
  mappingReads = 0;
  metadataReads = 0;
  unexpected = 0;
  #rawName = "Controlled replacement";
  #description = "";
  #acceptedName = this.#rawName;
  #token = "";
  #firstToken: string | null = null;
  #expectedTag = "";
  #firstTag: string | null = null;
  #firstBody: string | null = null;
  #releases: (() => void)[] = [];
  #holds = new Map<string, (request: Request) => void>();

  constructor(readonly page: Page) {}
  get browserPath(): string {
    return editPath(this.mapping.scope);
  }
  get metadataPath(): string {
    return catalogApi(mappingPath(this.mapping.scope)) + "/metadata";
  }

  draft(name: string, description = "", acceptedName = name): void {
    this.#rawName = name;
    this.#description = description;
    this.#acceptedName = acceptedName;
  }

  #tag(metadata: Metadata): string {
    const scope = metadata.scope;
    return `"metadata-v1:${scope.organizationId}:${scope.projectId}:${scope.mappingId}:${metadata.version}"`;
  }

  readReply(
    value: unknown = this.metadata,
    tag: "valid" | "missing" | "weak" | "foreign" | "version" = "valid",
  ): Reply {
    let received = this.#tag(this.metadata);
    if (tag === "weak") received = "W/" + received;
    if (tag === "foreign")
      received = this.#tag({
        ...this.metadata,
        scope: { ...this.metadata.scope, organizationId: foreignOrg },
      });
    if (tag === "version")
      received = this.#tag({
        ...this.metadata,
        version: (BigInt(this.metadata.version) + 1n).toString(),
      });
    return {
      json: value,
      headers: tag === "missing" ? {} : { ETag: received },
    };
  }

  success(): Reply {
    const changed =
      this.#acceptedName !== this.metadata.name ||
      this.#description !== this.metadata.description;
    const metadata: Metadata = {
      scope: clone(this.metadata.scope),
      name: this.#acceptedName,
      description: this.#description,
      version: changed
        ? (BigInt(this.metadata.version) + 1n).toString()
        : this.metadata.version,
    };
    return { json: metadata, headers: { ETag: this.#tag(metadata) } };
  }

  hold(kind: "context" | "mapping" | "metadata" | "patch", reply?: Reply) {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let entered = false;
    let cancelled = false;
    let resumed = false;
    let held: Request | null = null;
    this.page.on("requestfailed", (request) => {
      if (request === held) cancelled = true;
    });
    this.#holds.set(kind, (request) => {
      entered = true;
      held = request;
    });
    const handler = async () => {
      await gate;
      resumed = true;
      return (
        reply ??
        (kind === "context"
          ? { json: { ...this.context, requestToken: this.#token } }
          : kind === "mapping"
            ? { json: this.mapping }
            : this.readReply())
      );
    };
    if (kind === "context") this.contextReply = handler;
    if (kind === "mapping") this.mappingReply = handler;
    if (kind === "metadata") this.metadataReply = handler;
    if (kind === "patch") this.patchReply = handler;
    this.#releases.push(release);
    return {
      release,
      entered: () => entered,
      cancelled: () => cancelled,
      resumed: () => resumed,
    };
  }

  async install(): Promise<void> {
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request(),
        url = new URL(request.url());
      const method = request.method(),
        path = url.pathname;
      let handler: Handler;
      let kind: string | null = null;
      const primary = catalogApi(mappingPath(this.mapping.scope));
      const secondary = catalogApi(mappingPath(this.otherMapping.scope));
      if (method === "GET" && path === contextPath && !url.search) {
        kind = "context";
        ++this.contextReads;
        this.#token = `controlled-metadata-admission-${this.contextReads}`;
        handler = this.contextReply ?? {
          json: { ...this.context, requestToken: this.#token },
        };
      } else if (method === "GET" && path === primary && !url.search) {
        kind = "mapping";
        ++this.mappingReads;
        handler = this.mappingReply ?? { json: this.mapping };
      } else if (method === "GET" && path === secondary && !url.search) {
        handler = { json: this.otherMapping };
      } else if (
        method === "GET" &&
        path === this.metadataPath &&
        !url.search
      ) {
        kind = "metadata";
        ++this.metadataReads;
        handler = this.metadataReply ?? this.readReply();
      } else if (
        method === "GET" &&
        path === secondary + "/metadata" &&
        !url.search
      ) {
        handler = {
          json: this.otherMetadata,
          headers: { ETag: this.#tag(this.otherMetadata) },
        };
      } else if (
        method === "PATCH" &&
        path === this.metadataPath &&
        !url.search
      ) {
        kind = "patch";
        this.observe(request);
        handler = this.patchReply;
      } else if (
        method === "GET" &&
        path === catalogApi(projectPath(synthetic.project.scope)) &&
        !url.search
      ) {
        handler = { json: synthetic.project };
      } else if (
        method === "GET" &&
        path === primary + "/revisions" &&
        url.search === "?limit=10"
      ) {
        handler = {
          json: { revisions: [synthetic.revision], nextCursor: null },
        };
      } else if (
        method === "GET" &&
        path === catalogApi(projectsPath(synthetic.org)) &&
        url.search === "?limit=10"
      ) {
        handler = { json: { projects: [synthetic.project], nextCursor: null } };
      } else {
        ++this.unexpected;
        handler = refusal(404, "notFound");
      }
      if (kind) this.#holds.get(kind)?.(request);
      const reply = typeof handler === "function" ? await handler() : handler;
      if (
        method === "GET" &&
        path.endsWith("/metadata") &&
        (reply.status ?? 200) === 200
      )
        this.#expectedTag = reply.headers?.["ETag"] ?? "";
      try {
        if (reply.abort) await route.abort("failed");
        else
          await route.fulfill({
            status: reply.status ?? 200,
            json: reply.json ?? {},
            headers: reply.headers,
          });
      } catch {
        if (!request.failure() && !this.page.isClosed())
          throw new Error(
            "A controlled metadata response could not be delivered.",
          );
      }
    });
  }

  private observe(request: Request): void {
    const headers = request.headers(),
      body = request.postData() ?? "",
      tag = headers["if-match"] ?? "";
    this.#firstTag ??= tag;
    this.#firstBody ??= body;
    let closedBody = false;
    try {
      const value = JSON.parse(body) as Record<string, unknown>;
      closedBody =
        value !== null &&
        !Array.isArray(value) &&
        Object.keys(value).sort().join("|") === "description|name" &&
        typeof value["name"] === "string" &&
        typeof value["description"] === "string";
    } catch {
      /* No raw payload diagnostics. */
    }
    this.wires.push({
      correctHeaders:
        headers["content-type"] ===
          "application/vnd.weft.mapping-metadata-edit.v1+json" &&
        headers["x-xsrf-token"] === this.#token &&
        ![
          "authorization",
          "idempotency-key",
          "if-none-match",
          "if-modified-since",
          "if-unmodified-since",
          "if-range",
          "content-encoding",
          "x-weft-actor",
          "x-weft-role",
        ].some((header) => header in headers),
      closedBody,
      exactDraft:
        body ===
        JSON.stringify({ name: this.#rawName, description: this.#description }),
      receivedValidator: tag === this.#expectedTag,
      ordinaryMappingValidator:
        tag === this.#tag({ ...this.metadata, version: this.mapping.version }),
      sameFirstBody: body === this.#firstBody,
      sameFirstValidator: tag === this.#firstTag,
      freshToken:
        this.#firstToken === null ||
        headers["x-xsrf-token"] !== this.#firstToken,
    });
    this.#firstToken ??= headers["x-xsrf-token"] ?? "";
  }

  release(): void {
    for (const release of this.#releases) release();
    this.#firstTag = this.#firstBody = this.#firstToken = null;
    this.#token = this.#expectedTag = "";
  }
}

async function controlled(
  page: Page,
  run: (fixture: ControlledMetadata) => Promise<void>,
): Promise<void> {
  const fixture = new ControlledMetadata(page);
  await fixture.install();
  try {
    await run(fixture);
  } finally {
    fixture.release();
    expect(fixture.unexpected).toBe(0);
  }
}
async function isolated(
  parent: Page,
  run: (page: Page, fixture: ControlledMetadata) => Promise<void>,
): Promise<void> {
  const page = await parent.context().newPage();
  try {
    await controlled(page, (fixture) => run(page, fixture));
  } finally {
    await page.close();
  }
}
const name = (page: Page) =>
  page.getByRole("textbox", { name: "Name", exact: true });
const description = (page: Page) =>
  page.getByRole("textbox", { name: "Description (optional)", exact: true });
const save = (page: Page) =>
  page.getByRole("button", { name: "Save metadata", exact: true });
const action = (page: Page, label: string) =>
  page.getByRole("button", { name: label, exact: true });
async function open(page: Page, fixture: ControlledMetadata): Promise<void> {
  await page.goto(fixture.browserPath);
  await expect(
    page.getByRole("heading", { name: "Edit mapping metadata", exact: true }),
  ).toBeVisible();
  await expect(save(page)).toBeEnabled();
}
async function fill(
  page: Page,
  rawName: string,
  rawDescription = "",
): Promise<void> {
  await name(page).fill(rawName);
  await description(page).fill(rawDescription);
}
async function valueMatches(
  field: Locator,
  expected: string,
): Promise<boolean> {
  return field.evaluate(
    (element, value) => (element as HTMLInputElement).value === value,
    expected,
  );
}
async function unknown(page: Page): Promise<void> {
  await expect(
    page.getByRole("heading", { name: "Save outcome is unknown", exact: true }),
  ).toBeVisible();
  await expect(save(page)).toBeDisabled();
}
async function confirmed(page: Page): Promise<void> {
  await expect(
    page.getByRole("heading", { name: "Metadata save confirmed", exact: true }),
  ).toBeVisible();
}
function wires(fixture: ControlledMetadata, count: number): void {
  expect(fixture.wires.length).toBe(count);
  expect(
    fixture.wires.every(
      (wire) =>
        wire.correctHeaders &&
        wire.closedBody &&
        wire.exactDraft &&
        wire.receivedValidator &&
        !wire.ordinaryMappingValidator &&
        wire.freshToken,
    ),
  ).toBe(true);
}
async function warnsOnUnload(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
}

test("controlled metadata: complete route scope and fresh capability gate the editor without PATCH", async ({
  page,
}) => {
  for (const scope of [
    { ...synthetic.mapping.scope, organizationId: "not-an-organization" },
    {
      ...synthetic.mapping.scope,
      projectId: "00000000-0000-0000-0000-000000000000",
    },
    {
      ...synthetic.mapping.scope,
      mappingId: "A0000000-0000-4000-8000-000000000083",
    },
  ])
    await isolated(page, async (current, fixture) => {
      // Build an intentionally invalid URL without the canonical production helper.
      await current.goto(
        `/catalog/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/edit`,
      );
      await expect(
        current.getByRole("heading", {
          name: "Metadata editor unavailable",
          exact: true,
        }),
      ).toBeVisible();
      await expect(name(current)).toHaveCount(0);
      expect(fixture.contextReads).toBe(0);
      expect(fixture.wires.length).toBe(0);
    });
  for (const gate of [
    "query",
    "foreign organization",
    "feature",
    "action",
    "cross-scope mapping",
  ] as const)
    await test.step(gate, () =>
      isolated(page, async (current, fixture) => {
        if (gate === "feature") fixture.context.features.editMetadata = false;
        if (gate === "action")
          fixture.mapping.access.actions.editMetadata = false;
        if (gate === "cross-scope mapping")
          fixture.mappingReply = {
            json: {
              ...fixture.mapping,
              scope: { ...fixture.mapping.scope, projectId: synthetic.org },
            },
          };
        const path =
          gate === "foreign organization"
            ? editPath({ ...fixture.mapping.scope, organizationId: foreignOrg })
            : fixture.browserPath + (gate === "query" ? "?cursor=QQ" : "");
        // Accept-Patch cannot substitute for current feature/action admission.
        fixture.metadataReply = {
          ...fixture.readReply(),
          headers: {
            ...fixture.readReply().headers,
            "Accept-Patch":
              "application/vnd.weft.mapping-metadata-edit.v1+json",
          },
        };
        await current.goto(path);
        await expect(
          current.getByRole("heading", {
            name: "Metadata editor unavailable",
            exact: true,
          }),
        ).toBeVisible();
        await expect(name(current)).toHaveCount(0);
        expect(fixture.metadataReads).toBe(0);
        expect(fixture.wires.length).toBe(0);
      }),
    );
});

test("controlled metadata: initial response requires exact metadata scope, scalar strings and an actual bound strong ETag", async ({
  page,
}) => {
  const variants: {
    label: string;
    reply: (fixture: ControlledMetadata) => Reply;
  }[] = [
    {
      label: "missing validator",
      reply: (fixture) => fixture.readReply(fixture.metadata, "missing"),
    },
    {
      label: "weak validator",
      reply: (fixture) => fixture.readReply(fixture.metadata, "weak"),
    },
    {
      label: "foreign validator",
      reply: (fixture) => fixture.readReply(fixture.metadata, "foreign"),
    },
    {
      label: "version mismatch",
      reply: (fixture) => fixture.readReply(fixture.metadata, "version"),
    },
    {
      label: "foreign body scope",
      reply: (fixture) =>
        fixture.readReply({
          ...fixture.metadata,
          scope: { ...fixture.metadata.scope, mappingId: synthetic.org },
        }),
    },
    {
      label: "numeric version",
      reply: (fixture) =>
        fixture.readReply({ ...fixture.metadata, version: 9007199254740992 }),
    },
    {
      label: "extra metadata property",
      reply: (fixture) =>
        fixture.readReply({ ...fixture.metadata, extra: true }),
    },
    {
      label: "unaccepted surrounding whitespace",
      reply: (fixture) =>
        fixture.readReply({
          ...fixture.metadata,
          name: "\u0085Untrimmed\u0085",
        }),
    },
  ];
  for (const variant of variants)
    await test.step(variant.label, () =>
      isolated(page, async (current, fixture) => {
        fixture.metadataReply = variant.reply(fixture);
        await current.goto(fixture.browserPath);
        await expect(current.getByRole("alert")).toContainText(
          "could not be verified",
        );
        await expect(name(current)).toHaveCount(0);
        await expect(save(current)).toHaveCount(0);
        expect(fixture.wires.length).toBe(0);
      }),
    );
});

test("controlled metadata: changed and normalized no-op saves use the received validator and exact bigint version", async ({
  page,
}) => {
  for (const changed of [true, false])
    await test.step(changed ? "changed" : "normalized no-op", () =>
      isolated(page, async (current, fixture) => {
        const rawName = changed
          ? "\u0085Controlled changed metadata\u0085"
          : "\u0085" + fixture.metadata.name + "\u0085";
        const accepted = changed
          ? "Controlled changed metadata"
          : fixture.metadata.name;
        const exactDescription = changed
          ? "\nPreserved description \t"
          : fixture.metadata.description;
        fixture.draft(rawName, exactDescription, accepted);
        fixture.patchReply = fixture.success();
        await open(current, fixture);
        await expect(current.locator(".metadata-snapshot")).toContainText(
          baseVersion,
        );
        await fill(current, rawName, exactDescription);
        await save(current).click();
        await confirmed(current);
        await expect(current.locator(".metadata-snapshot")).toContainText(
          changed ? "9007199254740994" : baseVersion,
        );
        if (!changed)
          await expect(
            current.getByText(/accepted values were unchanged/),
          ).toBeVisible();
        expect(await valueMatches(name(current), accepted)).toBe(true);
        expect(await valueMatches(description(current), exactDescription)).toBe(
          true,
        );
        expect(await warnsOnUnload(current)).toBe(false);
        wires(fixture, 1);
      }),
    );
});

test("controlled metadata: Unicode scalar and raw serialized-body limits reject invalid input without PATCH", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await open(page, fixture);
    expect((await name(page).getAttribute("maxlength")) === "200").toBe(false);
    expect((await description(page).getAttribute("maxlength")) === "4000").toBe(
      false,
    );
    const invalid = [
      { field: "name", text: "\u0085 \u0085" },
      { field: "name", text: "🧶".repeat(201) },
      { field: "description", text: "織".repeat(4001) },
      { field: "name", codes: [0] },
      { field: "name", codes: [0xd800] },
      { field: "description", codes: [0xdc00] },
    ] as const;
    for (const vector of invalid) {
      await fill(page, "Controlled valid name");
      const field = vector.field === "name" ? name(page) : description(page);
      if ("text" in vector) await field.fill(vector.text);
      else
        await field.evaluate(
          (element, codes) => {
            (element as HTMLInputElement).value = String.fromCharCode(...codes);
            element.dispatchEvent(new Event("input", { bubbles: true }));
          },
          [...vector.codes],
        );
      await save(page).click();
      await expect(
        page.getByRole("alert", {
          name: "Metadata fields need attention",
          exact: true,
        }),
      ).toBeFocused();
      await expect(field).toHaveAttribute("aria-invalid", "true");
      expect(fixture.wires.length).toBe(0);
    }
    await fill(page, "Controlled valid name");
    await name(page).evaluate((element) => {
      (element as HTMLInputElement).value = "\u0085".repeat(33_000) + "Name";
      element.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await save(page).click();
    await expect(
      page.getByRole("alert", {
        name: "Metadata fields need attention",
        exact: true,
      }),
    ).toContainText("complete request is too large");
    expect(fixture.wires.length).toBe(0);
    const rawName = "\u0085" + "🧶".repeat(200) + "\u0085",
      rawDescription = "😀".repeat(4000);
    fixture.draft(rawName, rawDescription, "🧶".repeat(200));
    fixture.patchReply = fixture.success();
    await fill(page, rawName, rawDescription);
    await save(page).click();
    await confirmed(page);
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    fixture.draft("\ufeff", "", "\ufeff");
    fixture.patchReply = fixture.success();
    await open(current, fixture);
    await fill(current, "\ufeff");
    await save(current).click();
    await confirmed(current);
    expect(await valueMatches(name(current), "\ufeff")).toBe(true);
    wires(fixture, 1);
  });
});

test("controlled metadata: focus admission preserves raw dirty fields, DOM identity and caret without rereading metadata", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.clock.install();
    await open(page, fixture);
    const rawName = "\u0085" + fixture.metadata.name + "\u0085";
    await fill(page, rawName, fixture.metadata.description);
    await name(page).focus();
    await name(page).evaluate((element) =>
      (element as HTMLInputElement).setSelectionRange(2, 5),
    );
    const field = await name(page).elementHandle();
    const held = fixture.hold("context");
    const reads = fixture.metadataReads;
    fixture.metadata = {
      ...fixture.metadata,
      name: "Controlled server-side newer metadata",
      version: "9007199254740994",
    };
    await page.evaluate(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await page.clock.runFor(150);
    await expect.poll(held.entered).toBe(true);
    await expect(name(page)).toHaveJSProperty("readOnly", true);
    await expect(save(page)).toBeDisabled();
    expect(
      await field!.evaluate(
        (element) =>
          element === document.getElementById("metadata-name") &&
          element.isConnected,
      ),
    ).toBe(true);
    expect(await valueMatches(name(page), rawName)).toBe(true);
    await expect(page.locator(".metadata-snapshot")).not.toContainText(
      "Role from the latest admitted view",
    );
    held.release();
    await expect(save(page)).toBeEnabled();
    expect(
      await name(page).evaluate((element) => {
        const field = element as HTMLInputElement;
        return field.selectionStart === 2 && field.selectionEnd === 5;
      }),
    ).toBe(true);
    expect(await valueMatches(name(page), rawName)).toBe(true);
    expect(fixture.metadataReads).toBe(reads);
    expect(fixture.wires.length).toBe(0);
    expect(await warnsOnUnload(page)).toBe(true);
    await field!.dispose();
  });
});

test("controlled metadata: dirty leave decision keeps editing or explicitly discards without claiming undo", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await open(page, fixture);
    const rawName = "\u0085" + fixture.metadata.name + "\u0085";
    await fill(page, rawName, fixture.metadata.description);
    expect(await warnsOnUnload(page)).toBe(true);
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    const leave = page.getByRole("heading", {
      name: "Leave this metadata edit?",
      exact: true,
    });
    await expect(leave).toBeFocused();
    await expect(page.getByText(/leaving is not an undo/)).toBeVisible();
    await expect(save(page)).toBeDisabled();
    await action(page, "Keep editing").click();
    await expect(leave).toHaveCount(0);
    await expect(
      page.getByRole("heading", { name: "Edit mapping metadata", exact: true }),
    ).toBeFocused();
    expect(await valueMatches(name(page), rawName)).toBe(true);
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await action(page, "Discard changes and leave").click();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      fixture.mapping.name,
    );
    expect(await warnsOnUnload(page)).toBe(false);
    expect(fixture.wires.length).toBe(0);
  });
});

test("controlled metadata: Save refreshes feature, mapping action, identity and token before any PATCH", async ({
  page,
}) => {
  for (const gate of ["feature", "action", "actor", "token"] as const)
    await test.step(gate, () =>
      isolated(page, async (current, fixture) => {
        await open(current, fixture);
        const mine = "Controlled draft behind fresh admission";
        await fill(current, mine);
        if (gate === "feature") fixture.context.features.editMetadata = false;
        if (gate === "action")
          fixture.mapping.access.actions.editMetadata = false;
        if (gate === "actor") fixture.context.actorPrincipalId = otherActor;
        if (gate === "token")
          fixture.contextReply = {
            json: { ...fixture.context, requestToken: "invalid\r\ncontrolled" },
          };
        const contextReads = fixture.contextReads;
        await save(current).click();
        await expect(
          current.getByRole("heading", {
            name: "Metadata editor unavailable",
            exact: true,
          }),
        ).toBeVisible();
        await expect(name(current)).toHaveCount(0);
        expect(fixture.contextReads).toBeGreaterThan(contextReads);
        expect(fixture.wires.length).toBe(0);
        if (gate === "actor") expect(await warnsOnUnload(current)).toBe(false);
      }),
    );
});

test("controlled metadata: conflicts retain base and mine until reviewed explicit choice, including a second conflict", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    const original = clone(fixture.metadata),
      mine = "Controlled first mine",
      mineDescription = "Original draft description.";
    fixture.draft(mine, mineDescription);
    fixture.patchReply = refusal(412, "preconditionFailed");
    await open(page, fixture);
    await fill(page, mine, mineDescription);
    await save(page).click();
    await expect(
      page.getByRole("heading", {
        name: "Review before another save",
        exact: true,
      }),
    ).toBeVisible();
    await expect(save(page)).toBeDisabled();
    expect(await valueMatches(name(page), mine)).toBe(true);
    fixture.metadata = {
      ...fixture.metadata,
      name: "Controlled concurrent metadata",
      version: "9007199254740994",
    };
    await action(page, "Review latest").click();
    await expect(
      page.getByRole("heading", {
        name: "Compare mapping metadata",
        exact: true,
      }),
    ).toBeFocused();
    for (const [label, expected] of [
      ["Base metadata", original.name],
      ["Current metadata", fixture.metadata.name],
      ["My metadata", mine],
    ]) {
      expect(
        await page
          .getByRole("region", { name: label, exact: true })
          .locator("dd")
          .first()
          .evaluate(
            (element, value) => element.textContent === value,
            expected,
          ),
      ).toBe(true);
    }
    await expect(save(page)).toBeDisabled();
    expect(fixture.wires.length).toBe(1);
    await action(page, "Keep mine").click();
    await expect(save(page)).toBeEnabled();
    expect(fixture.wires.length).toBe(1);
    await save(page).click();
    await expect(save(page)).toBeDisabled();
    await expect(
      page.getByRole("heading", {
        name: "Review before another save",
        exact: true,
      }),
    ).toBeVisible();
    wires(fixture, 2);
    expect(
      fixture.wires[1].sameFirstBody && !fixture.wires[1].sameFirstValidator,
    ).toBe(true);
    fixture.metadata = {
      ...fixture.metadata,
      name: "Controlled second concurrent metadata",
      version: "9007199254740995",
    };
    await action(page, "Review latest").click();
    await expect(
      page.getByRole("region", { name: "Current metadata", exact: true }),
    ).toContainText(fixture.metadata.version);
    await page.setViewportSize({ width: 390, height: 844 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await action(page, "Edit mine").click();
    await expect(name(page)).toBeFocused();
    fixture.draft("Controlled second mine", mineDescription);
    fixture.patchReply = fixture.success();
    await fill(page, "Controlled second mine", mineDescription);
    await save(page).click();
    await confirmed(page);
    await expect(page.locator(".metadata-snapshot")).toContainText(
      "9007199254740996",
    );
    wires(fixture, 3);
    expect(await warnsOnUnload(page)).toBe(false);
  });
});

test("controlled metadata: recognized first refusals remain closed, distinct and never automatically save again", async ({
  page,
}) => {
  const errors = [
    [400, "invalidRequest", "refused this metadata request as invalid"],
    [403, "identityRejected", "Private editor details are hidden"],
    [404, "notFound", "Private editor details are hidden"],
    [412, "preconditionFailed", "Review latest before deciding"],
    [428, "preconditionRequired", "validator is required"],
    [409, "versionExhausted", "cannot advance further"],
    [413, "bodyLimit", "refused the size"],
    [415, "unsupportedMediaType", "did not accept the metadata request format"],
  ] as const;
  for (const [status, code, text] of errors)
    await test.step(code, () =>
      isolated(page, async (current, fixture) => {
        if (code === "versionExhausted")
          fixture.metadata = {
            ...fixture.metadata,
            version: "9223372036854775807",
          };
        fixture.draft("Controlled refused metadata");
        fixture.patchReply = refusal(status, code);
        await open(current, fixture);
        await fill(current, "Controlled refused metadata");
        await save(current).click();
        await expect(current.getByRole("alert")).toContainText(text);
        await expect(
          current.getByRole("heading", {
            name: "Save outcome is unknown",
            exact: true,
          }),
        ).toHaveCount(0);
        if (status === 403 || status === 404) {
          await expect(
            current.getByRole("heading", {
              name: "Metadata editor unavailable",
              exact: true,
            }),
          ).toBeVisible();
          await expect(name(current)).toHaveCount(0);
        } else {
          expect(
            await valueMatches(name(current), "Controlled refused metadata"),
          ).toBe(true);
          if (status === 412 || status === 428)
            await expect(save(current)).toBeDisabled();
        }
        wires(fixture, 1);
      }),
    );
});

test("controlled metadata: incompatible PATCH status, scope, text, version, ETag and error shape retain uncertainty", async ({
  page,
}) => {
  const variants: { label: string; change: (reply: Reply) => void }[] = [
    {
      label: "201 is not a metadata acknowledgement",
      change: (reply) => {
        reply.status = 201;
      },
    },
    {
      label: "foreign success scope",
      change: (reply) => {
        (reply.json as Metadata).scope.organizationId = foreignOrg;
      },
    },
    {
      label: "different accepted name",
      change: (reply) => {
        (reply.json as Metadata).name = "Another controlled request";
      },
    },
    {
      label: "different preserved description",
      change: (reply) => {
        (reply.json as Metadata).description = "Unexpected description";
      },
    },
    {
      label: "stale changed version",
      change: (reply) => {
        (reply.json as Metadata).version = baseVersion;
      },
    },
    {
      label: "missing response tag",
      change: (reply) => {
        reply.headers = {};
      },
    },
    {
      label: "weak response tag",
      change: (reply) => {
        reply.headers = { ETag: "W/" + reply.headers!["ETag"] };
      },
    },
    {
      label: "extra success property",
      change: (reply) => {
        reply.json = { ...(reply.json as Metadata), changed: true };
      },
    },
    {
      label: "closed error extended",
      change: (reply) => {
        reply.status = 412;
        reply.json = { code: "preconditionFailed", current: {} };
      },
    },
    {
      label: "wrong status for error code",
      change: (reply) => {
        reply.status = 409;
        reply.json = { code: "preconditionFailed" };
      },
    },
    {
      label: "server outcome unconfirmed",
      change: (reply) => {
        reply.status = 500;
        reply.json = { code: "persistenceFailure" };
      },
    },
  ];
  for (const variant of variants)
    await test.step(variant.label, () =>
      isolated(page, async (current, fixture) => {
        fixture.draft("Controlled uncertain response");
        const reply = fixture.success();
        variant.change(reply);
        fixture.patchReply = reply;
        await open(current, fixture);
        await fill(current, "Controlled uncertain response");
        await save(current).click();
        await unknown(current);
        expect(
          await valueMatches(name(current), "Controlled uncertain response"),
        ).toBe(true);
        await expect(
          current.getByRole("heading", {
            name: "Metadata save confirmed",
            exact: true,
          }),
        ).toHaveCount(0);
        expect(await warnsOnUnload(current)).toBe(true);
        wires(fixture, 1);
      }),
    );
});

test("controlled metadata: stopped or timed-out saves remain unknown and GET matches never acknowledge an edit", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.clock.install();
    const attempted = "Controlled original uncertain save",
      later = "Controlled later local draft";
    fixture.draft(attempted);
    const held = fixture.hold("patch", fixture.success());
    await open(page, fixture);
    await fill(page, attempted);
    await save(page).evaluate((element) => {
      (element as HTMLButtonElement).click();
      (element as HTMLButtonElement).click();
    });
    await expect.poll(held.entered).toBe(true);
    expect(fixture.wires.length).toBe(1);
    await action(page, "Stop waiting").click();
    await unknown(page);
    await expect.poll(held.cancelled).toBe(true);
    held.release();
    await expect.poll(held.resumed).toBe(true);
    await page.clock.runFor(250);
    await unknown(page);
    await fill(page, later);
    fixture.metadata = {
      ...fixture.metadata,
      name: attempted,
      description: "",
      version: "9007199254740994",
    };
    await action(page, "Check current metadata").click();
    await expect(
      page.getByText(/Current values match the attempted values/),
    ).toBeVisible();
    await expect(
      page.getByText(/This read does not confirm which request wrote them/),
    ).toBeVisible();
    expect(await valueMatches(name(page), later)).toBe(true);
    await unknown(page);
    fixture.metadata = {
      ...fixture.metadata,
      name: "Controlled different current metadata",
      version: "9007199254740995",
    };
    await action(page, "Check current metadata").click();
    await expect(
      page.getByText(/Current values differ from the attempted values/),
    ).toBeVisible();
    await unknown(page);
    await page.evaluate(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await page.clock.runFor(200);
    await unknown(page);
    expect(fixture.wires.length).toBe(1);
    const reads = fixture.contextReads;
    await action(page, "Refresh admission").click();
    await expect.poll(() => fixture.contextReads).toBeGreaterThan(reads);
    await action(page, "Review latest").click();
    await expect(
      page.getByRole("heading", {
        name: "Compare mapping metadata",
        exact: true,
      }),
    ).toBeVisible();
    await action(page, "Use current").click();
    await expect(save(page)).toBeEnabled();
    expect(await valueMatches(name(page), fixture.metadata.name)).toBe(true);
    await expect(
      page.getByRole("heading", {
        name: "Save outcome is unknown",
        exact: true,
      }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("heading", {
        name: "Metadata save confirmed",
        exact: true,
      }),
    ).toHaveCount(0);
    expect(await warnsOnUnload(page)).toBe(false);
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    await current.clock.install();
    fixture.draft("Controlled bounded wait");
    const held = fixture.hold("patch", fixture.success());
    await open(current, fixture);
    await fill(current, "Controlled bounded wait");
    await save(current).click();
    await expect.poll(held.entered).toBe(true);
    await current.clock.runFor(30_001);
    await unknown(current);
    held.release();
    await current.clock.runFor(1000);
    await unknown(current);
    wires(fixture, 1);
  });
});

test("controlled metadata: a later refusal preserves prior uncertainty while a verified latest save can become leaveable", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    const mine = "Controlled latest deliberate save";
    fixture.draft(mine);
    fixture.patchReply = { abort: true };
    await open(page, fixture);
    await fill(page, mine);
    await save(page).click();
    await unknown(page);
    fixture.metadata = {
      ...fixture.metadata,
      name: "Controlled current before another attempt",
      version: "9007199254740994",
    };
    await action(page, "Review latest").click();
    await expect(action(page, "Keep mine")).toBeEnabled();
    await action(page, "Keep mine").click();
    fixture.patchReply = refusal(400, "invalidRequest");
    const refusedResponse = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return (
        response.request().method() === "PATCH" &&
        url.pathname === fixture.metadataPath &&
        url.search === "" &&
        response.status() === 400
      );
    });
    await save(page).click();
    expect(await (await refusedResponse).finished()).toBeNull();
    await expect(page.getByRole("alert")).toHaveText(
      "The service refused this metadata request as invalid.",
    );
    await unknown(page);
    wires(fixture, 2);
    expect(await warnsOnUnload(page)).toBe(true);
    fixture.metadata = { ...fixture.metadata, version: "9007199254740995" };
    await action(page, "Review latest").click();
    await expect(
      page.getByRole("region", { name: "Current metadata", exact: true }),
    ).toContainText(fixture.metadata.version);
    await action(page, "Keep mine").click();
    fixture.patchReply = fixture.success();
    await save(page).click();
    await confirmed(page);
    await expect(
      page.getByText(/An earlier save was not acknowledged/),
    ).toBeVisible();
    await expect(
      page.getByText(/does not prove what that earlier request did/),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", {
        name: "Save outcome is unknown",
        exact: true,
      }),
    ).toHaveCount(0);
    expect(await warnsOnUnload(page)).toBe(false);
    wires(fixture, 3);
    await page
      .getByRole("link", { name: "Back to mapping", exact: true })
      .click();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      fixture.mapping.name,
    );
    await expect(
      page.getByRole("heading", {
        name: "Leave this metadata edit?",
        exact: true,
      }),
    ).toHaveCount(0);
  });
});

test("controlled metadata: revocation hides same-owner work while a confirmed new actor purges it", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await open(page, fixture);
    const mine = "Controlled draft never inherited by another actor";
    await fill(page, mine);
    fixture.mapping.access.actions.editMetadata = false;
    await action(page, "Refresh admission").click();
    await expect(
      page.getByRole("heading", {
        name: "Metadata editor unavailable",
        exact: true,
      }),
    ).toBeVisible();
    await expect(name(page)).toHaveCount(0);
    fixture.mapping.access.actions.editMetadata = true;
    await action(page, "Refresh admission").click();
    await expect(save(page)).toBeEnabled();
    expect(await valueMatches(name(page), mine)).toBe(true);
    fixture.mappingReply = refusal(404, "notFound");
    await action(page, "Refresh admission").click();
    await expect(
      page.getByRole("heading", {
        name: "Metadata editor unavailable",
        exact: true,
      }),
    ).toBeVisible();
    await expect(name(page)).toHaveCount(0);
    fixture.mappingReply = null;
    await action(page, "Refresh admission").click();
    await expect(save(page)).toBeEnabled();
    expect(await valueMatches(name(page), mine)).toBe(true);
    fixture.context.actorPrincipalId = otherActor;
    await action(page, "Refresh admission").click();
    await expect(
      page.getByRole("heading", {
        name: "Metadata editor unavailable",
        exact: true,
      }),
    ).toBeVisible();
    await expect(name(page)).toHaveCount(0);
    expect(
      await page
        .locator("main")
        .evaluate(
          (element, value) => !element.textContent?.includes(value),
          mine,
        ),
    ).toBe(true);
    expect(await warnsOnUnload(page)).toBe(false);
    await action(page, "Refresh admission").click();
    await expect(save(page)).toBeEnabled();
    expect(await valueMatches(name(page), fixture.metadata.name)).toBe(true);
    expect(await valueMatches(name(page), mine)).toBe(false);
    expect(fixture.wires.length).toBe(0);
  });
});

test("controlled metadata: discarding an in-flight save cancels waiting and a late response cannot enter another mapping", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    const mine = "Controlled obsolete scoped draft";
    fixture.draft(mine);
    const held = fixture.hold("patch", fixture.success());
    await open(page, fixture);
    await fill(page, mine);
    await save(page).click();
    await expect.poll(held.entered).toBe(true);
    await page.evaluate((path) => {
      history.pushState(null, "", path);
      window.dispatchEvent(new PopStateEvent("popstate", { state: null }));
    }, editPath(fixture.otherMapping.scope));
    await expect(
      page.getByRole("heading", {
        name: "Leave this metadata edit?",
        exact: true,
      }),
    ).toBeVisible();
    await expect(page.getByText(/leaving is not an undo/)).toBeVisible();
    await action(page, "Discard changes and leave").click();
    await expect(page).toHaveURL(
      new RegExp(editPath(fixture.otherMapping.scope) + "$"),
    );
    await expect(save(page)).toBeEnabled();
    expect(await valueMatches(name(page), fixture.otherMetadata.name)).toBe(
      true,
    );
    await expect.poll(held.cancelled).toBe(true);
    held.release();
    await expect.poll(held.resumed).toBe(true);
    await expect(
      page.getByRole("heading", {
        name: "Metadata save confirmed",
        exact: true,
      }),
    ).toHaveCount(0);
    expect(await valueMatches(name(page), fixture.otherMetadata.name)).toBe(
      true,
    );
    expect(await valueMatches(name(page), mine)).toBe(false);
    expect(await warnsOnUnload(page)).toBe(false);
    wires(fixture, 1);
  });
});

test("controlled metadata: keyboard errors and literal Unicode editing remain usable at 390 pixels", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await open(page, fixture);
    await expect(name(page)).toBeFocused();
    await name(page).fill("");
    await page.keyboard.press("Enter");
    const errors = page.getByRole("alert", {
      name: "Metadata fields need attention",
      exact: true,
    });
    await expect(errors).toBeFocused();
    await errors.getByRole("button").press("Enter");
    await expect(name(page)).toBeFocused();
    const literal =
      '<img src=x onerror="document.documentElement.dataset.metadataExecuted=1"> 🧶';
    const exactDescription =
      "  Controlled literal description\n" + "織".repeat(100);
    fixture.draft(literal, exactDescription);
    fixture.patchReply = fixture.success();
    await name(page).fill(literal);
    await page.keyboard.press("Tab");
    await expect(description(page)).toBeFocused();
    await description(page).fill(exactDescription);
    await page.keyboard.press("Tab");
    await expect(save(page)).toBeFocused();
    await page.keyboard.press("Enter");
    await confirmed(page);
    await expect(
      page.getByRole("heading", {
        name: "Metadata save confirmed",
        exact: true,
      }),
    ).toBeFocused();
    expect(await valueMatches(name(page), literal)).toBe(true);
    expect(await valueMatches(description(page), exactDescription)).toBe(true);
    expect(
      await page.evaluate(
        () => !document.documentElement.dataset["metadataExecuted"],
      ),
    ).toBe(true);
    await expect(page.locator("main img")).toHaveCount(0);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    expect(
      await page.evaluate(
        () =>
          Object.keys(localStorage).length === 0 &&
          Object.keys(sessionStorage).length === 0,
      ),
    ).toBe(true);
    wires(fixture, 1);
  });
});
