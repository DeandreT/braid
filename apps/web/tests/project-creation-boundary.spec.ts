import type { Locator, Page, Request, Route } from "@playwright/test";
import type { components } from "../src/api/generated";
import {
  test,
  expect,
  synthetic,
  projectsPath,
  projectPath,
  catalogApi,
  createPath,
} from "./project-creation.fixture";

// These controlled responses exercise browser boundaries, not PostgreSQL, native
// workers, real admission contention, or server commit acknowledgement faults.
// Attempt bodies/keys/tokens stay in closures; assertions expose only booleans.
type Context = components["schemas"]["CatalogContextV1"];
type CreationResult = components["schemas"]["CatalogProjectCreationResultV1"];
type Reply = {
  status?: number;
  json?: unknown;
  headers?: Record<string, string>;
  abort?: boolean;
};
type Wire = {
  correctHeaders: boolean;
  closedBody: boolean;
  exactDraft: boolean;
  sameOriginalBody: boolean;
  sameOriginalKey: boolean;
  freshToken: boolean;
};
const anotherActor = "10000000-0000-4000-8000-000000000099";
const foreignOrganization = "10000000-0000-4000-8000-000000000098";
const publicProjectId = "20000000-0000-4000-8000-000000000081";
const contextPath = "/api/v1/catalog/context";
const postPath = `/api/v1/organizations/${synthetic.org}/projects`;
const clone = <T>(value: T): T => structuredClone(value);
const refusal = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
const canonicalId = (value: unknown): boolean =>
  typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    value,
  ) &&
  value !== "00000000-0000-0000-0000-000000000000";

class ControlledCreation {
  readonly context: Context = {
    ...clone(synthetic.context),
    features: { ...synthetic.context.features, createProjects: true },
    organizationActions: { createProjects: true },
  };
  readonly project = {
    ...clone(synthetic.project),
    scope: { organizationId: synthetic.org, projectId: publicProjectId },
    name: "Controlled current project",
    createdAtUtc: "2026-10-15T12:00:00.1234567Z",
    updatedAtUtc: "2026-10-15T12:00:00.1234567Z",
  };
  readonly wires: Wire[] = [];
  contextReads = 0;
  projectReads = 0;
  unexpectedRequests = 0;
  contextReply: Reply | null = null;
  projectReply: Reply | null = null;
  postReply: Reply | (() => Reply | Promise<Reply>) = refusal(
    503,
    "projectCreationAdmissionBusy",
  );
  #rawName = "Controlled creation";
  #description = "Controlled browser evidence only.";
  #acceptedName = this.#rawName;
  #originalBody: string | null = null;
  #originalKey: string | null = null;
  #originalToken: string | null = null;
  #lastToken = "";
  #releases: (() => void)[] = [];

  constructor(readonly page: Page) {}

  draft(rawName: string, description = "", acceptedName = rawName): void {
    this.#rawName = rawName;
    this.#description = description;
    this.#acceptedName = acceptedName;
  }

  success(status: 200 | 201 = 201): Reply {
    const projectUrl = catalogApi(projectPath(this.project.scope));
    const json: CreationResult = {
      outcome: status === 201 ? "created" : "replayed",
      projectUrl,
      receipt: {
        project: clone(this.project.scope),
        name: this.#acceptedName,
        description: this.#description,
        creatorPrincipalId: synthetic.actor,
        createdAtUtc: "2026-10-15T12:00:00.1234567Z",
        initialProjectVersion: "1",
        initialMembershipRole: "administrator",
        initialMembershipActive: true,
        initialMembershipVersion: "1",
      },
    };
    return { status, json, headers: { Location: projectUrl } };
  }

  hold(reply: Reply) {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let entered = false;
    let cancelled = false;
    let held: Request | null = null;
    this.page.on("requestfailed", (request) => {
      if (request === held) cancelled = true;
    });
    this.postReply = async () => {
      entered = true;
      await gate;
      return reply;
    };
    this.#heldRequest = (request) => {
      held = request;
    };
    this.#releases.push(release);
    return { release, entered: () => entered, cancelled: () => cancelled };
  }

  #heldRequest: ((request: Request) => void) | null = null;

  async install(): Promise<void> {
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request();
      const url = new URL(request.url());
      const method = request.method();
      // Explicit bootstrap does not consume any existing context/resource/write plan.
      if (url.pathname === "/api/auth/session") {
        const headers = request.headers();
        const admitted =
          url.origin === new URL(this.page.url()).origin &&
          method === "GET" &&
          !url.search &&
          (request.postDataBuffer()?.length ?? 0) === 0 &&
          headers["x-braid-session"] === "1" &&
          ![
            "content-type",
            "authorization",
            "x-xsrf-token",
            "x-braid-auth-token",
            "idempotency-key",
            "if-match",
            "if-none-match",
            "identitycontext",
            "identity-context",
            "x-braid-identity-context",
            "x-braid-actor",
            "x-braid-role",
            "x-braid-login",
            "x-braid-logout",
            "x-braid-forget-local",
          ].some((name) => name in headers);
        if (!admitted) ++this.unexpectedRequests;
        await route.fulfill(
          admitted
            ? { json: { version: 1, mode: "development" } }
            : { status: 404, json: { code: "notFound" } },
        );
        return;
      }
      let reply: Reply;
      if (method === "GET" && url.pathname === contextPath && !url.search) {
        ++this.contextReads;
        this.#lastToken = `controlled-creation-admission-${this.contextReads}`;
        reply = this.contextReply ?? {
          json: { ...this.context, requestToken: this.#lastToken },
        };
      } else if (
        method === "POST" &&
        url.pathname === postPath &&
        !url.search
      ) {
        this.observe(request);
        this.#heldRequest?.(request);
        reply =
          typeof this.postReply === "function"
            ? await this.postReply()
            : this.postReply;
      } else if (
        method === "GET" &&
        url.pathname === catalogApi(projectPath(this.project.scope)) &&
        !url.search
      ) {
        ++this.projectReads;
        reply = this.projectReply ?? { json: this.project };
      } else if (
        method === "GET" &&
        url.pathname === catalogApi(projectsPath(synthetic.org)) &&
        url.search === "?limit=10"
      ) {
        reply = { json: { projects: [], nextCursor: null } };
      } else if (
        method === "GET" &&
        url.pathname ===
          catalogApi(projectPath(this.project.scope)) + "/mappings" &&
        url.search === "?limit=10"
      ) {
        reply = { json: { mappings: [], nextCursor: null } };
      } else {
        ++this.unexpectedRequests;
        reply = refusal(404, "notFound");
      }
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
            "The controlled creation reply could not be delivered.",
          );
      }
    });
  }

  private observe(request: Request): void {
    const headers = request.headers();
    const body = request.postData() ?? "";
    const key = headers["idempotency-key"] ?? "";
    const token = headers["x-xsrf-token"] ?? "";
    this.#originalBody ??= body;
    this.#originalKey ??= key;
    let closedBody = false;
    try {
      const value: unknown = JSON.parse(body);
      closedBody =
        typeof value === "object" &&
        value !== null &&
        !Array.isArray(value) &&
        Object.keys(value).sort().join("|") === "description|name" &&
        typeof (value as Record<string, unknown>)["name"] === "string" &&
        typeof (value as Record<string, unknown>)["description"] === "string";
    } catch {
      /* Keep payloads out of diagnostics. */
    }
    this.wires.push({
      correctHeaders:
        headers["content-type"] ===
          "application/vnd.braid.project-create.v1+json" &&
        canonicalId(key) &&
        token === this.#lastToken &&
        ![
          "authorization",
          "if-match",
          "if-none-match",
          "x-braid-actor",
          "x-braid-role",
          "content-encoding",
        ].some((header) => header in headers),
      closedBody,
      exactDraft:
        body ===
        JSON.stringify({ name: this.#rawName, description: this.#description }),
      sameOriginalBody: body === this.#originalBody,
      sameOriginalKey: key === this.#originalKey,
      freshToken: this.#originalToken === null || token !== this.#originalToken,
    });
    this.#originalToken ??= token;
  }

  release(): void {
    for (const release of this.#releases) release();
    this.#originalBody = this.#originalKey = this.#originalToken = null;
  }
}

async function controlled(
  page: Page,
  run: (fixture: ControlledCreation) => Promise<void>,
): Promise<void> {
  const fixture = new ControlledCreation(page);
  await fixture.install();
  try {
    await run(fixture);
  } finally {
    fixture.release();
    expect(fixture.unexpectedRequests).toBe(0);
  }
}

async function open(page: Page): Promise<void> {
  await page.goto(createPath(synthetic.org));
  await expect(
    page.getByRole("heading", {
      level: 1,
      name: "Create project",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Create project", exact: true }),
  ).toBeEnabled();
}
const name = (page: Page) =>
  page.getByRole("textbox", { name: "Name", exact: true });
const description = (page: Page) =>
  page.getByRole("textbox", { name: "Description (optional)", exact: true });
const create = (page: Page) =>
  page.getByRole("button", { name: "Create project", exact: true });
const recover = (page: Page) =>
  page.getByRole("button", { name: "Recover creation", exact: true });
async function fill(
  page: Page,
  rawName: string,
  rawDescription = "",
): Promise<void> {
  await name(page).fill(rawName);
  await description(page).fill(rawDescription);
}
async function unknown(page: Page): Promise<void> {
  await expect(
    page.getByRole("heading", {
      name: "Creation outcome is unknown",
      exact: true,
    }),
  ).toBeVisible();
  await expect(recover(page)).toBeEnabled();
}
async function confirmed(page: Page): Promise<void> {
  await expect(
    page.getByRole("heading", {
      name: "Project creation confirmed",
      exact: true,
    }),
  ).toBeVisible();
}
function expectedWires(fixture: ControlledCreation, count: number): void {
  expect(fixture.wires.length).toBe(count);
  expect(
    fixture.wires.every(
      (wire) => wire.correctHeaders && wire.closedBody && wire.exactDraft,
    ),
  ).toBe(true);
}
async function privateValueMatches(
  field: Locator,
  value: string,
): Promise<boolean> {
  return field.evaluate(
    (element, expected) => (element as HTMLInputElement).value === expected,
    value,
  );
}

async function isolated(
  parent: Page,
  run: (page: Page, fixture: ControlledCreation) => Promise<void>,
): Promise<void> {
  const page = await parent.context().newPage();
  try {
    await controlled(page, (fixture) => run(page, fixture));
  } finally {
    await page.close();
  }
}

test("controlled creation: malformed full-scope links and fresh organization capability gate all sends", async ({
  page,
}) => {
  for (const path of [
    createPath("not-an-organization"),
    createPath("00000000-0000-0000-0000-000000000000"),
    createPath("A0000000-0000-4000-8000-000000000001"),
    createPath(synthetic.org) + "?cursor=QQ",
  ])
    await test.step("invalid route never admits or posts", () =>
      isolated(page, async (current, fixture) => {
        await current.goto(path);
        await expect(
          current.getByRole("heading", { name: "Creation form unavailable" }),
        ).toBeVisible();
        await expect(name(current)).toHaveCount(0);
        expect(fixture.contextReads).toBe(0);
        expect(fixture.wires.length).toBe(0);
      }));
  await test.step("foreign organization never loads form or sends", () =>
    isolated(page, async (current, fixture) => {
      await current.goto(createPath(foreignOrganization));
      await expect(
        current.getByRole("heading", { name: "Creation form unavailable" }),
      ).toBeVisible();
      await expect(name(current)).toHaveCount(0);
      expect(fixture.contextReads).toBeGreaterThan(0);
      expect(fixture.wires.length).toBe(0);
    }));
  await test.step("an invalid SPA selection clears previously admitted shell identity", () =>
    isolated(page, async (current, fixture) => {
      await open(current);
      await fill(current, "Controlled draft before an invalid SPA link");
      await current.getByText("Development identity", { exact: true }).click();
      await expect(current.locator(".catalog-context")).toBeVisible();
      const reads = fixture.contextReads;
      const admittedDocument = await current.evaluateHandle(() => document);
      try {
        // Exercise the router's normal history listener without replacing the
        // document, which would discard memory and hide a stale-context defect.
        await current.evaluate((path) => {
          history.pushState(null, "", path);
          window.dispatchEvent(new PopStateEvent("popstate", { state: null }));
        }, createPath("not-an-organization"));
        await expect(
          current.getByRole("heading", { name: "Creation form unavailable" }),
        ).toBeVisible();
        expect(
          await admittedDocument.evaluate((value) => value === document),
        ).toBe(true);
        await expect(current.locator(".catalog-context")).toHaveCount(0);
        await expect(
          current.getByText("Development identity", { exact: true }),
        ).toHaveCount(0);
        await expect(name(current)).toHaveCount(0);
        await expect(create(current)).toHaveCount(0);
        await expect(
          current.getByRole("button", {
            name: "Refresh admission",
            exact: true,
          }),
        ).toBeDisabled();
        expect(fixture.contextReads).toBe(reads);
        expect(fixture.wires.length).toBe(0);
      } finally {
        await admittedDocument.dispose();
      }
    }));
  for (const capability of ["feature", "organization"] as const)
    await test.step(`missing ${capability} capability`, () =>
      isolated(page, async (current, fixture) => {
        if (capability === "feature") {
          fixture.context.features.createProjects = false;
          fixture.context.organizationActions.createProjects = false;
        } else fixture.context.organizationActions.createProjects = false;
        await current.goto(createPath(synthetic.org));
        await expect(create(current)).toBeDisabled();
        expect(fixture.wires.length).toBe(0);
      }));
});

test("controlled creation: every deliberate send rechecks capability, actor and usable token before POST", async ({
  page,
}) => {
  for (const change of ["capability", "actor", "token"] as const)
    await test.step(change, () =>
      isolated(page, async (current, fixture) => {
        fixture.draft("Admission-bound controlled request");
        await open(current);
        await fill(current, "Admission-bound controlled request");
        if (change === "capability")
          fixture.context.organizationActions.createProjects = false;
        if (change === "actor") fixture.context.actorPrincipalId = anotherActor;
        if (change === "token")
          fixture.contextReply = {
            json: { ...fixture.context, requestToken: "invalid\r\ncontrolled" },
          };
        const reads = fixture.contextReads;
        await create(current).click();
        await expect.poll(() => fixture.contextReads).toBeGreaterThan(reads);
        await expect(current.getByRole("alert")).toBeVisible();
        expect(fixture.wires.length).toBe(0);
        if (change === "actor")
          expect(await privateValueMatches(name(current), "")).toBe(true);
      }),
    );
});

test("controlled creation: field errors count Unicode scalars and never send invalid NUL or surrogate input", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await open(page);
    expect((await name(page).getAttribute("maxlength")) === "200").toBe(false);
    expect((await description(page).getAttribute("maxlength")) === "4000").toBe(
      false,
    );
    const vectors = [
      { field: "name", value: "\u0085 \u0085" },
      { field: "name", value: "😀".repeat(201) },
      { field: "description", value: "織".repeat(4001) },
      { field: "name", codes: [0] },
      { field: "name", codes: [0xd800] },
      { field: "description", codes: [0xdc00] },
    ] as const;
    for (const vector of vectors)
      await test.step(`invalid ${vector.field} input`, async () => {
        await fill(page, "Valid controlled name");
        const field = vector.field === "name" ? name(page) : description(page);
        if ("value" in vector) await field.fill(vector.value);
        else
          await field.evaluate(
            (element, codes) => {
              (element as HTMLInputElement).value = String.fromCharCode(
                ...codes,
              );
              element.dispatchEvent(new Event("input", { bubbles: true }));
            },
            [...vector.codes],
          );
        await create(page).click();
        const summary = page.getByRole("alert", {
          name: "Project fields need attention",
        });
        await expect(summary).toBeVisible();
        await expect(summary).toBeFocused();
        await expect(field).toHaveAttribute("aria-invalid", "true");
        const described = await field.getAttribute("aria-describedby");
        expect(Boolean(described?.includes("-error"))).toBe(true);
        expect(fixture.wires.length).toBe(0);
      });
    // A small accepted name can still have an oversized raw serialized intent.
    await fill(page, "Valid controlled name");
    await name(page).evaluate((element) => {
      (element as HTMLInputElement).value = "\u0085".repeat(33_000) + "Name";
      element.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await create(page).click();
    await expect(
      page.getByRole("alert", { name: "Project fields need attention" }),
    ).toContainText("complete request is too large");
    expect(fixture.wires.length).toBe(0);
    const rawName = "\u0085" + "😀".repeat(200) + "\u0085";
    const rawDescription = "😀".repeat(4000);
    fixture.draft(rawName, rawDescription, "😀".repeat(200));
    fixture.postReply = fixture.success();
    await fill(page, rawName, rawDescription);
    expect(await privateValueMatches(name(page), rawName)).toBe(true);
    expect(await privateValueMatches(description(page), rawDescription)).toBe(
      true,
    );
    await create(page).click();
    await confirmed(page);
    expectedWires(fixture, 1);
  });
});

test("controlled creation: .NET NEL trimming and preserved BOM differ from JavaScript trim", async ({
  page,
}) => {
  for (const vector of [
    { raw: "\u0085 Controlled NEL \u0085", accepted: "Controlled NEL" },
    { raw: "\ufeff", accepted: "\ufeff" },
  ])
    await isolated(page, async (current, fixture) => {
      const preservedDescription =
        "\u0085  Description\n\twith whitespace \ufeff";
      fixture.draft(vector.raw, preservedDescription, vector.accepted);
      fixture.postReply = fixture.success();
      await open(current);
      await fill(current, vector.raw, preservedDescription);
      await create(current).click();
      await confirmed(current);
      expectedWires(fixture, 1);
      expect(
        await current.locator(".creation-facts").evaluate(
          (element, expected) => {
            const values = Array.from(element.querySelectorAll("dd"));
            return (
              values[0]?.textContent === expected.name &&
              values[1]?.textContent === expected.description
            );
          },
          { name: vector.accepted, description: preservedDescription },
        ),
      ).toBe(true);
    });
});

test("controlled creation: only matched 201 created and 200 replayed receipts confirm and current GET supplies role", async ({
  page,
}) => {
  for (const status of [201, 200] as const)
    await test.step(`matched ${status} response`, () =>
      isolated(page, async (current, fixture) => {
        fixture.draft(
          "Controlled acknowledged project",
          "Exact controlled description",
        );
        fixture.postReply = fixture.success(status);
        await open(current);
        await fill(
          current,
          "Controlled acknowledged project",
          "Exact controlled description",
        );
        await create(current).click();
        await confirmed(current);
        await expect(
          current.getByText("Current project role: viewer.", { exact: true }),
        ).toBeVisible();
        await expect(
          current.getByText("Administrator · active · version 1", {
            exact: true,
          }),
        ).toBeVisible();
        await expect(
          current.getByRole("link", { name: "Open project", exact: true }),
        ).toHaveAttribute("href", projectPath(fixture.project.scope));
        expect(fixture.projectReads).toBe(1);
        expectedWires(fixture, 1);
      }));
});

test("controlled creation: unverified success bindings, historical facts, Location and indeterminate errors remain uncertain", async ({
  page,
}) => {
  const variants: { label: string; change: (reply: Reply) => void }[] = [
    {
      label: "unsupported 202",
      change: (reply) => {
        reply.status = 202;
      },
    },
    {
      label: "201 replayed contradiction",
      change: (reply) => {
        (reply.json as CreationResult).outcome = "replayed";
      },
    },
    {
      label: "200 created contradiction",
      change: (reply) => {
        reply.status = 200;
      },
    },
    {
      label: "foreign original actor",
      change: (reply) => {
        (reply.json as CreationResult).receipt.creatorPrincipalId =
          anotherActor;
      },
    },
    {
      label: "foreign organization",
      change: (reply) => {
        (reply.json as CreationResult).receipt.project.organizationId =
          foreignOrganization;
      },
    },
    {
      label: "unmatched Location",
      change: (reply) => {
        reply.headers = { Location: "/catalog" };
      },
    },
    {
      label: "missing Location",
      change: (reply) => {
        reply.headers = {};
      },
    },
    {
      label: "nonhistorical membership",
      change: (reply) => {
        (reply.json as CreationResult).receipt.initialMembershipRole = "viewer";
      },
    },
    {
      label: "nonhistorical version type",
      change: (reply) => {
        reply.json = {
          ...(reply.json as CreationResult),
          receipt: {
            ...(reply.json as CreationResult).receipt,
            initialProjectVersion: 1,
          },
        };
      },
    },
    {
      label: "different accepted name",
      change: (reply) => {
        (reply.json as CreationResult).receipt.name =
          "A different controlled request";
      },
    },
    {
      label: "different exact description",
      change: (reply) => {
        (reply.json as CreationResult).receipt.description =
          "Changed controlled description";
      },
    },
    {
      label: "receipt extra property",
      change: (reply) => {
        reply.json = {
          ...(reply.json as CreationResult),
          receipt: { ...(reply.json as CreationResult).receipt, extra: true },
        };
      },
    },
    {
      label: "project URL outside exact ordinary endpoint",
      change: (reply) => {
        (reply.json as CreationResult).projectUrl =
          "/api/v1/organizations/foreign/projects/other";
      },
    },
    {
      label: "closed error body extension",
      change: (reply) => {
        reply.status = 503;
        reply.json = { code: "projectCreationAdmissionBusy", retry: true };
      },
    },
    {
      label: "wrong status for a closed error code",
      change: (reply) => {
        reply.status = 409;
        reply.json = { code: "permissionDenied" };
      },
    },
    {
      label: "initial server outcome unconfirmed",
      change: (reply) => {
        reply.status = 500;
        reply.json = { code: "internalFailure" };
      },
    },
  ];
  for (const variant of variants)
    await test.step(variant.label, () =>
      isolated(page, async (current, fixture) => {
        fixture.draft("Controlled incompatible reply");
        const reply = fixture.success();
        variant.change(reply);
        fixture.postReply = reply;
        await open(current);
        await fill(current, "Controlled incompatible reply");
        await create(current).click();
        await unknown(current);
        await expect(
          current.getByRole("heading", {
            name: "Project creation confirmed",
            exact: true,
          }),
        ).toHaveCount(0);
        await expect(
          current.getByRole("button", {
            name: "Edit refused request",
            exact: true,
          }),
        ).toHaveCount(0);
        await expect(current).toHaveURL(
          new RegExp(createPath(synthetic.org) + "$"),
        );
        expect(fixture.projectReads).toBe(0);
        expectedWires(fixture, 1);
      }),
    );
});

test("controlled creation: recognized first refusals remain distinct and never silently rotate or resend", async ({
  page,
}) => {
  const errors = [
    [400, "invalidRequest", "The service refused this request as invalid."],
    [
      403,
      "identityRejected",
      "Your current identity could not be admitted. Refresh admission before trying again.",
    ],
    [
      403,
      "permissionDenied",
      "You do not currently have permission to create projects in this organization.",
    ],
    [
      404,
      "unavailable",
      "This organization or creation request is unavailable to your current identity.",
    ],
    [
      409,
      "idempotencyConflict",
      "The original request identity conflicts with a saved request. No different project was adopted.",
    ],
    [413, "bodyLimit", "The service refused the size of this request."],
    [
      415,
      "unsupportedMediaType",
      "The service did not accept the request format.",
    ],
    [
      503,
      "projectCreationAdmissionBusy",
      "Project creation is busy. No automatic retry was made.",
    ],
  ] as const;
  for (const [status, code, copy] of errors)
    await test.step(code, () =>
      isolated(page, async (current, fixture) => {
        fixture.draft("Controlled refusal");
        fixture.postReply = refusal(status, code);
        await open(current);
        await fill(current, "Controlled refusal");
        await create(current).click();
        await expect(
          current.getByRole("heading", {
            name: "Creation request refused",
            exact: true,
          }),
        ).toBeVisible();
        await expect(current.getByRole("alert")).toHaveText(copy);
        await expect(
          current.getByRole("heading", {
            name: "Creation outcome is unknown",
            exact: true,
          }),
        ).toHaveCount(0);
        await expect(
          current.getByRole("button", {
            name:
              code === "idempotencyConflict"
                ? "Abandon original request"
                : "Edit refused request",
            exact: true,
          }),
        ).toBeVisible();
        expectedWires(fixture, 1);
      }),
    );
});

test("controlled creation: uncertainty survives later busy, forbidden, absent, invalid and conflicting recovery replies", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    fixture.draft(
      "Controlled sticky request",
      "Original description remains frozen.",
    );
    fixture.postReply = { abort: true };
    await open(page);
    await fill(
      page,
      "Controlled sticky request",
      "Original description remains frozen.",
    );
    await create(page).click();
    await unknown(page);
    expect(
      await page.evaluate(() => {
        const event = new Event("beforeunload", { cancelable: true });
        window.dispatchEvent(event);
        return event.defaultPrevented;
      }),
    ).toBe(true);
    await expect(name(page)).toBeDisabled();
    await expect(description(page)).toBeDisabled();
    const responses = [
      [
        refusal(503, "projectCreationAdmissionBusy"),
        "Project creation is busy. No automatic retry was made.",
      ],
      [
        refusal(403, "permissionDenied"),
        "You do not currently have permission to create projects in this organization.",
      ],
      [
        refusal(404, "notFound"),
        "This organization or creation request is unavailable to your current identity.",
      ],
      [
        refusal(400, "invalidRequest"),
        "The service refused this request as invalid.",
      ],
      [
        refusal(409, "idempotencyConflict"),
        "The original request identity conflicts with a saved request. No different project was adopted.",
      ],
      [
        refusal(500, "persistenceFailure"),
        "The service could not confirm the creation outcome.",
      ],
    ] as const;
    for (const [index, [reply, message]] of responses.entries()) {
      fixture.postReply = reply;
      const refusedResponse = page.waitForResponse((response) => {
        const request = response.request();
        const url = new URL(response.url());
        return (
          request.method() === "POST" &&
          url.pathname === postPath &&
          !url.search &&
          response.status() === reply.status
        );
      });
      await recover(page).click();
      expect(await (await refusedResponse).finished()).toBeNull();
      await expect(page.getByRole("alert")).toHaveText(message);
      await unknown(page);
      await expect(
        page.getByRole("button", { name: "Edit refused request", exact: true }),
      ).toHaveCount(0);
      expect(
        fixture.wires.every(
          (wire) =>
            wire.sameOriginalBody && wire.sameOriginalKey && wire.freshToken,
        ),
      ).toBe(true);
      expectedWires(fixture, index + 2);
    }
    fixture.postReply = fixture.success(200);
    const replayResponse = page.waitForResponse((response) => {
      const request = response.request();
      const url = new URL(response.url());
      return (
        request.method() === "POST" &&
        url.pathname === postPath &&
        !url.search &&
        response.status() === 200
      );
    });
    await recover(page).click();
    expect(await (await replayResponse).finished()).toBeNull();
    await confirmed(page);
    expectedWires(fixture, responses.length + 2);
    expect(
      fixture.wires.every(
        (wire) =>
          wire.sameOriginalBody && wire.sameOriginalKey && wire.freshToken,
      ),
    ).toBe(true);
    expect(
      await page.evaluate(() => {
        const event = new Event("beforeunload", { cancelable: true });
        window.dispatchEvent(event);
        return event.defaultPrevented;
      }),
    ).toBe(false);
  });
});

test("controlled creation: focus and refresh never POST and duplicate submit events retain one frozen attempt", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.clock.install();
    fixture.draft("Controlled duplicate guard");
    const held = fixture.hold(fixture.success());
    await open(page);
    await fill(page, "Controlled duplicate guard");
    await create(page).evaluate((element) => {
      (element as HTMLButtonElement).click();
      (element as HTMLButtonElement).click();
    });
    await expect.poll(held.entered).toBe(true);
    expect(fixture.wires.length).toBe(1);
    await page
      .getByRole("button", { name: "Stop waiting", exact: true })
      .click();
    await unknown(page);
    await expect.poll(held.cancelled).toBe(true);
    held.release();
    await page.clock.runFor(250);
    await unknown(page);
    const reads = fixture.contextReads;
    await page
      .getByRole("button", { name: "Refresh admission", exact: true })
      .click();
    await expect.poll(() => fixture.contextReads).toBeGreaterThan(reads);
    await unknown(page);
    await page.evaluate(() => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await page.clock.runFor(250);
    await unknown(page);
    expect(fixture.wires.length).toBe(1);
    expect(
      await page.evaluate(
        () =>
          Object.keys(localStorage).length === 0 &&
          Object.keys(sessionStorage).length === 0,
      ),
    ).toBe(true);
    fixture.postReply = fixture.success(200);
    await recover(page).click();
    await confirmed(page);
    expectedWires(fixture, 2);
    expect(
      fixture.wires.every(
        (wire) => wire.sameOriginalBody && wire.sameOriginalKey,
      ),
    ).toBe(true);
  });
});

test("controlled creation: bounded POST timeout keeps the original recovery intent without automatic replay", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.clock.install();
    fixture.draft("Controlled timeout request");
    const held = fixture.hold(fixture.success());
    await open(page);
    await fill(page, "Controlled timeout request");
    await create(page).click();
    await expect.poll(held.entered).toBe(true);
    await page.clock.runFor(30_001);
    await unknown(page);
    expect(fixture.wires.length).toBe(1);
    held.release();
    await page.clock.runFor(500);
    await unknown(page);
    fixture.postReply = fixture.success(200);
    await recover(page).click();
    await confirmed(page);
    expectedWires(fixture, 2);
    expect(
      fixture.wires[1].sameOriginalBody && fixture.wires[1].sameOriginalKey,
    ).toBe(true);
  });
});

test("controlled creation: SPA cancellation quarantines the original attempt from another actor and ignores its late receipt", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    const rawName = "Controlled private-to-original-actor draft";
    fixture.draft(rawName);
    const held = fixture.hold(fixture.success());
    await open(page);
    await fill(page, rawName);
    await create(page).click();
    await expect.poll(held.entered).toBe(true);
    await page
      .getByRole("link", { name: "Back to projects", exact: true })
      .click();
    await expect(
      page.getByRole("heading", { level: 1, name: "Projects", exact: true }),
    ).toBeVisible();
    await expect.poll(held.cancelled).toBe(true);
    fixture.context.actorPrincipalId = anotherActor;
    await page
      .getByRole("link", { name: "Create project", exact: true })
      .click();
    await expect(
      page.getByRole("heading", {
        name: "Creation information is retained for another identity",
        exact: true,
      }),
    ).toBeVisible();
    held.release();
    await expect(name(page)).toHaveCount(0);
    await expect(recover(page)).toHaveCount(0);
    await expect(
      page.getByRole("heading", {
        name: "Project creation confirmed",
        exact: true,
      }),
    ).toHaveCount(0);
    expect(fixture.projectReads).toBe(0);
    expect(
      await page
        .locator("main")
        .evaluate(
          (element, expected) => !element.textContent?.includes(expected),
          rawName,
        ),
    ).toBe(true);
    expect(fixture.wires.length).toBe(1);
    fixture.context.actorPrincipalId = synthetic.actor;
    await page
      .getByRole("button", { name: "Refresh admission", exact: true })
      .click();
    await unknown(page);
    fixture.postReply = fixture.success(200);
    await recover(page).click();
    await confirmed(page);
    expectedWires(fixture, 2);
    expect(
      fixture.wires[1].sameOriginalBody && fixture.wires[1].sameOriginalKey,
    ).toBe(true);
  });
});

test("controlled creation: acknowledged receipt survives failed current access and retries only fresh GETs", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    fixture.draft("Controlled confirmed access failure");
    fixture.postReply = fixture.success();
    fixture.projectReply = refusal(500, "persistenceFailure");
    await open(page);
    await fill(page, "Controlled confirmed access failure");
    await create(page).click();
    await confirmed(page);
    await expect(
      page.getByText(
        /Creation remains confirmed, but current project access could not be loaded/,
      ),
    ).toBeVisible();
    await expect(
      page.getByRole("link", { name: "Open project", exact: true }),
    ).toHaveCount(0);
    expectedWires(fixture, 1);
    fixture.projectReply = refusal(404, "notFound");
    await page
      .getByRole("button", { name: "Retry project access", exact: true })
      .click();
    await expect.poll(() => fixture.projectReads).toBe(2);
    await confirmed(page);
    expectedWires(fixture, 1);
    fixture.projectReply = { json: fixture.project };
    await expect(
      page.getByRole("button", { name: "Retry project access", exact: true }),
    ).toBeEnabled();
    await page
      .getByRole("button", { name: "Retry project access", exact: true })
      .click();
    await expect(
      page.getByText("Current project role: viewer.", { exact: true }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Open project", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      fixture.project.name,
    );
    await expect(
      page.getByRole("heading", { name: "No mappings", exact: true }),
    ).toBeVisible();
    expectedWires(fixture, 1);
  });
  await test.step("confirmed history stays private across failed admission and a latched foreign identity", () =>
    isolated(page, async (current, fixture) => {
      const originalName =
        "Controlled receipt private to its original identity";
      const originalDescription =
        "Controlled description hidden during admission failure.";
      fixture.draft(originalName, originalDescription);
      fixture.postReply = () => {
        // Deliver a strict, matching synthetic acknowledgement, then fail the
        // independent admission GET. This is not a database fault simulation.
        fixture.contextReply = refusal(500, "persistenceFailure");
        return fixture.success();
      };
      const privateContentHidden = async () => {
        expect(
          await current
            .locator("main")
            .evaluate(
              (element, hidden) =>
                hidden.every((text) => !element.textContent?.includes(text)),
              [
                originalName,
                originalDescription,
                fixture.project.scope.projectId,
                synthetic.actor,
              ],
            ),
        ).toBe(true);
        await expect(current.locator(".creation-facts")).toHaveCount(0);
        await expect(name(current)).toHaveCount(0);
        await expect(
          current.getByRole("link", { name: "Open project", exact: true }),
        ).toHaveCount(0);
        await expect(recover(current)).toHaveCount(0);
      };
      await open(current);
      await fill(current, originalName, originalDescription);
      await create(current).click();
      await confirmed(current);
      await expect(
        current.getByRole("heading", {
          name: "Creation form unavailable",
          exact: true,
        }),
      ).toBeVisible();
      await expect(current.locator(".catalog-context")).toHaveCount(0);
      await expect(
        current.getByRole("button", {
          name: "Retry project access",
          exact: true,
        }),
      ).toBeEnabled();
      await privateContentHidden();
      expect(fixture.projectReads).toBe(0);
      expectedWires(fixture, 1);

      // A receipt access retry needs its original owner, not creation capability.
      fixture.contextReply = null;
      fixture.context.features.createProjects = false;
      fixture.context.organizationActions.createProjects = false;
      const readsBeforeRetry = fixture.contextReads;
      await current
        .getByRole("button", { name: "Retry project access", exact: true })
        .click();
      await expect(
        current.getByText("Current project role: viewer.", { exact: true }),
      ).toBeVisible();
      expect(fixture.contextReads).toBeGreaterThan(readsBeforeRetry);
      expect(fixture.projectReads).toBe(1);
      await confirmed(current);
      await expect(current.locator(".creation-facts")).toBeVisible();
      expectedWires(fixture, 1);

      fixture.context.actorPrincipalId = anotherActor;
      await current
        .getByRole("button", { name: "Refresh admission", exact: true })
        .click();
      await expect(
        current.getByRole("heading", {
          name: "Creation information is retained for another identity",
          exact: true,
        }),
      ).toBeVisible();
      await expect(
        current.getByRole("heading", {
          name: "Project creation confirmed",
          exact: true,
        }),
      ).toHaveCount(0);
      await privateContentHidden();
      expect(fixture.projectReads).toBe(1);

      // Failure after a positively observed foreign identity must not reveal
      // even the generic historical acknowledgement until its owner returns.
      fixture.contextReply = refusal(500, "persistenceFailure");
      await current
        .getByRole("button", { name: "Refresh admission", exact: true })
        .click();
      await expect(
        current.getByRole("heading", {
          name: "Creation form unavailable",
          exact: true,
        }),
      ).toBeVisible();
      await expect(
        current.getByRole("heading", {
          name: "Project creation confirmed",
          exact: true,
        }),
      ).toHaveCount(0);
      await expect(
        current.getByRole("button", {
          name: "Retry project access",
          exact: true,
        }),
      ).toHaveCount(0);
      await expect(current.locator(".catalog-context")).toHaveCount(0);
      await privateContentHidden();
      expect(fixture.projectReads).toBe(1);
      expectedWires(fixture, 1);

      fixture.context.actorPrincipalId = synthetic.actor;
      fixture.contextReply = null;
      await current
        .getByRole("button", { name: "Refresh admission", exact: true })
        .click();
      await confirmed(current);
      await expect(
        current.getByText("Current project role: viewer.", { exact: true }),
      ).toBeVisible();
      await expect(current.locator(".creation-facts")).toBeVisible();
      expect(
        await current
          .locator(".creation-facts")
          .evaluate(
            (element, expected) =>
              element.querySelector("dd")?.textContent === expected,
            originalName,
          ),
      ).toBe(true);
      expect(fixture.projectReads).toBe(2);
      expectedWires(fixture, 1);
    }));
});

test("controlled creation: abandoning uncertainty requires an explicit duplicate-risk decision before a new key", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    fixture.draft("Controlled abandoned request");
    fixture.postReply = { abort: true };
    await open(page);
    await fill(page, "Controlled abandoned request");
    await create(page).click();
    await unknown(page);
    await page
      .getByRole("button", { name: "Abandon original request", exact: true })
      .click();
    const abandon = page.getByRole("button", {
      name: "Abandon and start a new request",
      exact: true,
    });
    await expect(abandon).toBeDisabled();
    await expect(
      page.getByText(/may create a duplicate project/),
    ).toBeVisible();
    await page
      .getByRole("button", { name: "Keep original request", exact: true })
      .click();
    await unknown(page);
    expect(fixture.wires.length).toBe(1);
    await page
      .getByRole("button", { name: "Abandon original request", exact: true })
      .click();
    await page
      .getByRole("checkbox", {
        name: "I understand a project may already exist.",
        exact: true,
      })
      .check();
    await abandon.click();
    await expect(create(page)).toBeEnabled();
    fixture.draft("Controlled explicitly new request");
    fixture.postReply = fixture.success();
    await fill(page, "Controlled explicitly new request");
    await create(page).click();
    await confirmed(page);
    expectedWires(fixture, 2);
    expect(fixture.wires[1].sameOriginalKey).toBe(false);
    expect(fixture.wires[1].sameOriginalBody).toBe(false);
  });
});

test("controlled creation: keyboard form and literal Unicode receipt remain usable at 390 pixels", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const literal =
      '<img src=x onerror="document.documentElement.dataset.creationExecuted=1"> 🧶';
    const preserved = "  Controlled literal text\n" + "織".repeat(100);
    fixture.draft(literal, preserved);
    fixture.postReply = fixture.success();
    await open(page);
    await expect(name(page)).toBeFocused();
    await page.keyboard.press("Enter");
    const errors = page.getByRole("alert", {
      name: "Project fields need attention",
    });
    await expect(errors).toBeFocused();
    await errors.getByRole("button").press("Enter");
    await expect(name(page)).toBeFocused();
    await name(page).fill(literal);
    await page.keyboard.press("Tab");
    await expect(description(page)).toBeFocused();
    await description(page).fill(preserved);
    await page.keyboard.press("Tab");
    await expect(create(page)).toBeFocused();
    await page.keyboard.press("Enter");
    await confirmed(page);
    await expect(
      page.getByRole("heading", {
        name: "Project creation confirmed",
        exact: true,
      }),
    ).toBeFocused();
    expect(
      await page
        .locator(".creation-facts")
        .evaluate(
          (element, expected) =>
            element.querySelector("dd")?.textContent === expected,
          literal,
        ),
    ).toBe(true);
    expect(
      await page.evaluate(
        () => !document.documentElement.dataset["creationExecuted"],
      ),
    ).toBe(true);
    await expect(page.locator("main img")).toHaveCount(0);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    expectedWires(fixture, 1);
  });
});
