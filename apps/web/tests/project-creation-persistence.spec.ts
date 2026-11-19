import type {
  APIResponse,
  Page,
  Request,
  Response,
  Route,
} from "@playwright/test";
import { isDeepStrictEqual } from "node:util";
import type { components } from "../src/api/generated";
import { isSessionStatusRequest } from "./browser-session-status.fixture";
import {
  canonicalId,
  catalogApi,
  expect,
  projectPath,
  projectsPath,
  requireCreation,
  test,
  type CreationCounts,
  type CreationReceipt,
  type CreationResult,
  type ProjectCreationOwner,
} from "./project-creation.fixture";

// A browser session never receives the bearer arm.
type Context = Exclude<
  components["schemas"]["CatalogContextV1"],
  { mode: "bearer" }
>;
type Project = components["schemas"]["CatalogProjectV1"];
type Draft = {
  readonly name: string;
  readonly description: string;
  readonly acceptedName: string;
};
type Read = {
  readonly request: Request;
  readonly path: string;
  readonly status: number;
  readonly body: unknown;
};
type PrivatePost = {
  readonly request: Request;
  readonly key: string;
  readonly body: string;
  readonly token: string;
  readonly contextRead: number;
  readonly done: Promise<void>;
  readonly finish: () => void;
  status?: number;
  location?: string;
  value?: unknown;
};
type Delivery = "normal" | "lose" | "hold-delivery" | "hold-forward";
type Gate = { readonly entered: () => boolean; readonly release: () => void };

/** Every product request remains real. Original envelopes and receipts stay only in fixture memory. */
class ActualCreationWire {
  private readonly posts: PrivatePost[] = [];
  private readonly readTasks: Promise<void>[] = [];
  private readonly reads: Read[] = [];
  private readonly requestContexts: Request[] = [];
  private readonly answeredContexts = new Set<Request>();
  private readonly projectRequests = new Map<Request, number>();
  private readonly contexts = new Map<Request, Context>();
  private readonly pending = new Map<Request, PrivatePost>();
  private readonly releases: (() => void)[] = [];
  private mode: {
    delivery: Delivery;
    entered: () => void;
    wait: Promise<void>;
  } | null = null;
  private failure: Error | null = null;
  private readonly unexpected: string[] = [];
  constructor(
    private readonly page: Page,
    private readonly owner: ProjectCreationOwner,
  ) {}
  get postCount(): number {
    return this.posts.length;
  }
  get contextCount(): number {
    return this.requestContexts.length;
  }
  get projectRequestCount(): number {
    return this.projectRequests.size;
  }
  get projectReadCount(): number {
    return this.reads.filter((r) =>
      /^\/api\/v1\/organizations\/[^/]+\/projects\/[^/]+$/.test(r.path),
    ).length;
  }
  arm(delivery: Exclude<Delivery, "normal">): Gate {
    requireCreation(
      this.mode === null,
      "response delivery controls must be serialized",
    );
    let release!: () => void;
    let entered = false;
    const wait = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.releases.push(release);
    this.mode = {
      delivery,
      entered: () => {
        entered = true;
      },
      wait,
    };
    return { entered: () => entered, release };
  }
  private async response(
    post: PrivatePost,
    reply: APIResponse | Response,
  ): Promise<void> {
    try {
      post.status = reply.status();
      post.location = reply.headers()["location"];
      post.value = await reply.json();
    } catch {
      this.failure ??= new Error(
        "Project creation browser fixture: actual POST response observation failed",
      );
    } finally {
      post.finish();
    }
  }
  async install(): Promise<void> {
    const manifest = this.owner.manifest;
    const origin = manifest.readOrigin;
    const projects = `/api/v1/organizations/${manifest.organizationId}/projects`;
    const id = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
    const projectGet = new RegExp(`^${projects}/${id}$`);
    const mappingList = new RegExp(`^${projects}/${id}/mappings$`);
    this.page.on("response", (reply) => {
      const request = reply.request();
      const url = new URL(reply.url());
      if (url.origin !== origin || !url.pathname.startsWith("/api/")) return;
      if (request.method() === "GET") {
        const task = (async () => {
          let bytes: Buffer;
          try {
            bytes = await reply.body();
          } catch {
            if (request.failure()?.errorText !== "net::ERR_ABORTED")
              this.failure ??= new Error(
                "Project creation browser fixture: actual GET body observation failed",
              );
            return;
          }
          try {
            const body: unknown = JSON.parse(bytes.toString("utf8"));
            this.reads.push({
              request,
              path: url.pathname,
              status: reply.status(),
              body,
            });
            if (url.pathname === "/api/v1/catalog/context") {
              if (reply.status() === 200)
                this.contexts.set(request, body as Context);
              this.answeredContexts.add(request);
            }
          } catch {
            this.failure ??= new Error(
              "Project creation browser fixture: actual GET JSON or admission failed",
            );
          }
        })();
        this.readTasks.push(task);
      } else {
        const post = this.pending.get(request);
        if (post) {
          this.pending.delete(request);
          void this.response(post, reply);
        }
      }
    });
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request(),
        url = new URL(request.url());
      const keys = [...url.searchParams.keys()];
      const listQuery =
        keys.every((name) => name === "limit" || name === "cursor") &&
        url.searchParams.getAll("limit").length === 1 &&
        url.searchParams.get("limit") === "10" &&
        url.searchParams.getAll("cursor").length <= 1;
      const context =
        url.pathname === "/api/v1/catalog/context" && keys.length === 0;
      const allowedRead =
        isSessionStatusRequest(request, origin) ||
        context ||
        (projectGet.test(url.pathname) && keys.length === 0) ||
        ((url.pathname === projects || mappingList.test(url.pathname)) &&
          listQuery);
      if (
        url.origin !== origin ||
        (request.method() === "GET"
          ? !allowedRead
          : request.method() !== "POST" ||
            url.pathname !== projects ||
            keys.length !== 0)
      ) {
        this.unexpected.push(`${request.method()} ${url.pathname}`);
        await route.abort("blockedbyclient");
        return;
      }
      if (request.method() === "GET") {
        if (context) this.requestContexts.push(request);
        if (projectGet.test(url.pathname) && !this.projectRequests.has(request))
          this.projectRequests.set(request, this.projectRequests.size);
        await route.continue();
        return;
      }
      const headers = request.headers(),
        body = request.postData(),
        key = headers["idempotency-key"],
        token = headers["x-xsrf-token"];
      requireCreation(
        body !== null &&
          Buffer.byteLength(body) <= 65536 &&
          canonicalId(key) &&
          typeof token === "string" &&
          token.length > 0,
        "creation envelope was not bounded or complete",
      );
      requireCreation(
        headers["content-type"] ===
          "application/vnd.braid.project-create.v1+json" &&
          headers["origin"] === origin &&
          ![
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "content-encoding",
          ].some((name) => name in headers),
        "creation transport headers differ",
      );
      let finish!: () => void;
      const done = new Promise<void>((resolve) => {
        finish = resolve;
      });
      const post: PrivatePost = {
        request,
        key,
        body,
        token,
        contextRead: this.requestContexts.length - 1,
        done,
        finish,
      };
      this.posts.push(post);
      const mode = this.mode;
      this.mode = null;
      if (!mode) {
        this.pending.set(request, post);
        await route.continue();
        return;
      }
      try {
        if (mode.delivery === "hold-forward") {
          mode.entered();
          await mode.wait;
        }
        // This forwards the original browser request once. It never manufactures a successful response.
        const reply = await route.fetch({
          maxRetries: 0,
          maxRedirects: 0,
          timeout: 30000,
        });
        await this.response(post, reply);
        if (mode.delivery !== "hold-forward") mode.entered();
        if (mode.delivery === "hold-delivery") await mode.wait;
        if (mode.delivery === "lose") await route.abort("failed");
        else await route.fulfill({ response: reply });
      } catch {
        // A real request canceled by SPA departure cannot receive a late committed response.
        if (!request.failure())
          this.failure ??= new Error(
            "Project creation browser fixture: actual POST delivery observation failed",
          );
        post.finish();
      }
    });
  }
  async settle(): Promise<void> {
    await Promise.all(this.readTasks);
    if (this.failure) throw this.failure;
  }
  async original(
    index: number,
    draft: Draft,
    status: 200 | 201,
  ): Promise<CreationResult> {
    const post = this.posts[index];
    requireCreation(post, "expected one explicit creation request");
    await Promise.race([
      post.done,
      new Promise<never>((_, reject) => {
        const timer = setTimeout(
          () =>
            reject(
              new Error(
                "Project creation browser fixture: actual response deadline exceeded",
              ),
            ),
          45000,
        );
        void post.done.finally(() => clearTimeout(timer));
      }),
    ]);
    await this.settle();
    requireCreation(post.status === status, "actual creation status differs");
    const input: unknown = JSON.parse(post.body);
    requireCreation(
      input !== null && typeof input === "object" && !Array.isArray(input),
      "invalid original request shape",
    );
    const fields = input as Record<string, unknown>;
    requireCreation(
      Object.keys(fields).sort().join("|") === "description|name" &&
        fields.name === draft.name &&
        fields.description === draft.description,
      "original exact raw fields changed",
    );
    const admitted = this.contexts.get(this.requestContexts[post.contextRead]);
    requireCreation(
      admitted?.actorPrincipalId ===
        this.owner.manifest.profiles.creator.actorPrincipalId &&
        admitted.allowedOrganizationId === this.owner.manifest.organizationId &&
        admitted.features.createProjects &&
        admitted.organizationActions.createProjects &&
        admitted.requestToken === post.token,
      "POST did not use its fresh actual creator admission",
    );
    return this.result(post, draft, status);
  }
  private result(
    post: PrivatePost,
    draft: Draft,
    status: 200 | 201,
  ): CreationResult {
    const value = post.value as CreationResult | undefined;
    requireCreation(
      value &&
        Object.keys(value).sort().join("|") === "outcome|projectUrl|receipt" &&
        value.outcome === (status === 201 ? "created" : "replayed"),
      "actual status/outcome wrapper differs",
    );
    const receipt = value.receipt;
    requireCreation(
      receipt &&
        Object.keys(receipt).sort().join("|") ===
          [
            "project",
            "name",
            "description",
            "creatorPrincipalId",
            "createdAtUtc",
            "initialProjectVersion",
            "initialMembershipRole",
            "initialMembershipActive",
            "initialMembershipVersion",
          ]
            .sort()
            .join("|"),
      "actual receipt shape differs",
    );
    requireCreation(
      receipt.project &&
        Object.keys(receipt.project).sort().join("|") ===
          "organizationId|projectId" &&
        receipt.project.organizationId === this.owner.manifest.organizationId &&
        canonicalId(receipt.project.projectId),
      "actual receipt scope differs",
    );
    requireCreation(
      receipt.creatorPrincipalId ===
        this.owner.manifest.profiles.creator.actorPrincipalId &&
        receipt.name === draft.acceptedName &&
        receipt.description === draft.description &&
        receipt.initialProjectVersion === "1" &&
        receipt.initialMembershipRole === "administrator" &&
        receipt.initialMembershipActive === true &&
        receipt.initialMembershipVersion === "1",
      "actual immutable creation facts differ",
    );
    requireCreation(
      typeof receipt.createdAtUtc === "string" &&
        /^(?!0000)\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
          receipt.createdAtUtc,
        ) &&
        Number.isFinite(Date.parse(receipt.createdAtUtc)),
      "actual receipt UTC timestamp differs",
    );
    requireCreation(
      value.projectUrl === catalogApi(projectPath(receipt.project)) &&
        post.location === value.projectUrl,
      "actual relative location differs",
    );
    return value;
  }
  sameOriginal(left: number, right: number): void {
    const a = this.posts[left],
      b = this.posts[right];
    requireCreation(
      a && b && a.key === b.key && a.body === b.body,
      "recovery rotated the original key or exact body",
    );
  }
  async freshAdmission(index: number, before: number): Promise<void> {
    await this.settle();
    const post = this.posts[index];
    requireCreation(
      post && post.contextRead === before,
      "deliberate send did not refresh admission exactly once",
    );
    const context = this.contexts.get(this.requestContexts[post.contextRead]);
    requireCreation(
      context?.actorPrincipalId ===
        this.owner.manifest.profiles.creator.actorPrincipalId &&
        context.allowedOrganizationId === this.owner.manifest.organizationId &&
        context.features.createProjects &&
        context.organizationActions.createProjects &&
        context.requestToken === post.token,
      "send did not use fresh positive creator admission",
    );
  }
  sameReceipt(left: CreationReceipt, right: CreationReceipt): void {
    requireCreation(
      isDeepStrictEqual(left, right),
      "replay did not return identical immutable history",
    );
  }
  async refused(index: number, status: number, code: string): Promise<void> {
    const post = this.posts[index];
    requireCreation(post, "expected explicit refused POST");
    await post.done;
    await this.settle();
    const value = post.value as Record<string, unknown> | undefined;
    requireCreation(
      post.status === status &&
        value &&
        Object.keys(value).join("|") === "code" &&
        value.code === code,
      "actual categorical server refusal differs",
    );
  }
  /** Busy controls render asynchronously, so admission completion is observed on the wire. */
  async admitted(before: number): Promise<void> {
    await expect
      .poll(
        () =>
          this.failure !== null ||
          (this.requestContexts.length === before + 1 &&
            this.answeredContexts.has(this.requestContexts[before]!)),
        {
          message: "Expected exactly one answered actual admission read",
          timeout: 10000,
        },
      )
      .toBe(true);
    await this.settle();
  }
  async latestContext(): Promise<Context> {
    await this.settle();
    const value = this.contexts.get(this.requestContexts.at(-1)!);
    requireCreation(value, "expected actual admitted context");
    return value;
  }
  async latestProject(
    projectId: string,
    after: number,
  ): Promise<{ status: number; project: Project | null }> {
    const path = catalogApi(
      projectPath({
        organizationId: this.owner.manifest.organizationId,
        projectId,
      }),
    );
    const latest = () =>
      this.reads
        .filter(
          (r) =>
            r.path === path &&
            (this.projectRequests.get(r.request) ?? -1) >= after,
        )
        .at(-1);
    const observe = async () => {
      await expect
        .poll(() => this.failure !== null || latest() !== undefined, {
          message: "Expected fresh actual scoped project GET was not observed",
          timeout: 10000,
        })
        .toBe(true);
      if (this.failure) throw this.failure;
      await this.settle();
      const row = latest();
      requireCreation(row, "expected actual current project GET");
      return {
        status: row.status,
        project: row.status === 200 ? (row.body as Project) : null,
      };
    };
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([
        observe(),
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(
                this.failure ??
                  new Error(
                    "Project creation browser fixture: actual scoped project observation deadline exceeded",
                  ),
              ),
            10000,
          );
        }),
      ]);
    } catch (error: unknown) {
      if (this.failure) throw this.failure;
      throw error;
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }
  }
  async initialListIsEmpty(): Promise<void> {
    await this.settle();
    const list = this.reads.find(
      (r) =>
        r.path ===
        `/api/v1/organizations/${this.owner.manifest.organizationId}/projects`,
    );
    const body = list?.body as
      components["schemas"]["CatalogProjectPageV1"] | undefined;
    requireCreation(
      list?.status === 200 &&
        body?.projects.length === 0 &&
        body.nextCursor === null,
      "creator already had visible project membership",
    );
  }
  release(): void {
    for (const release of this.releases) release();
  }
  async verifyClosed(): Promise<void> {
    await this.settle();
    requireCreation(
      this.unexpected.length === 0 && this.mode === null,
      "unapproved API call or unused delivery control",
    );
  }
}

const named = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
async function accessResponseBody(response: Promise<Response>): Promise<void> {
  let nonempty: boolean;
  try {
    nonempty = (await (await response).body()).length > 0;
  } catch {
    throw new Error(
      "Project creation browser fixture: actual access response body could not be consumed",
    );
  }
  requireCreation(nonempty, "actual access response body was empty");
}
async function fill(page: Page, draft: Draft): Promise<void> {
  await page.getByLabel("Name", { exact: true }).fill(draft.name);
  await page
    .getByLabel("Description (optional)", { exact: true })
    .fill(draft.description);
}
async function refresh(page: Page, wire: ActualCreationWire): Promise<void> {
  const before = wire.contextCount;
  await named(page, "Refresh admission").click();
  await wire.admitted(before);
  await expect(named(page, "Refresh admission")).toBeEnabled();
}
async function confirmed(page: Page): Promise<void> {
  await expect(heading(page, "Project creation confirmed")).toBeVisible();
  await expect(named(page, "Retry project access")).toBeEnabled();
}
async function unknown(page: Page): Promise<void> {
  await expect(heading(page, "Creation outcome is unknown")).toBeVisible();
}
function createdDelta(before: CreationCounts, after: CreationCounts): void {
  expect(after).toEqual({
    projects: before.projects + 1,
    projectMemberships: before.projectMemberships + 1,
    creationFacts: before.creationFacts + 1,
    creatorProjects: before.creatorProjects + 1,
    workersCreated: 0,
  });
}
async function capture(
  owner: ProjectCreationOwner,
  result: CreationResult,
  before: CreationCounts,
): Promise<CreationCounts> {
  const ack = await owner.control({
    action: "capture-created",
    projectId: result.receipt.project.projectId,
  });
  requireCreation(
    ack.action === "capture-created",
    "expected creation capture",
  );
  createdDelta(before, ack.counts);
  return ack.counts;
}

test.describe.configure({ mode: "serial" });
test("real packaged project creation: exact Unicode, same-request recovery, current authority and zero native workers", async ({
  page,
  projectCreationOwner: owner,
}) => {
  test.setTimeout(600000);
  const fixture = owner.manifest;
  const wire = new ActualCreationWire(page, owner);
  await wire.install();
  const bom: Draft = {
    name: "\uFEFF",
    acceptedName: "\uFEFF",
    description:
      " \u0085\uFEFF e\u0301 😀 <img src=controlled-invalid onerror=document.documentElement.dataset.creationMarkup=1>\n ",
  };
  const unicode: Draft = {
    name: " \u0085" + "😀".repeat(200) + "\u0085 ",
    acceptedName: "😀".repeat(200),
    description: "\u0085  Recovery keeps this exact description.\n\uFEFF ",
  };
  const deleted: Draft = {
    name: "\u0085\uFEFFCreation after deleted access\u0085",
    acceptedName: "\uFEFFCreation after deleted access",
    description: " Historical receipt; no current membership. ",
  };
  try {
    let baseline = await owner.counts();
    expect(baseline.creatorProjects).toBe(0);
    expect(baseline.workersCreated).toBe(0);
    await page.setViewportSize({ width: 390, height: 844 });
    await test.step("actual capable creator starts without project membership and creates a BOM-only name using keyboard submission", async () => {
      await page.goto(projectsPath(fixture.organizationId));
      await expect(heading(page, "No projects")).toBeVisible();
      await wire.initialListIsEmpty();
      const context = await wire.latestContext();
      requireCreation(
        context.actorPrincipalId ===
          fixture.profiles.creator.actorPrincipalId &&
          context.features.createProjects &&
          context.organizationActions.createProjects,
        "actual organization creation capability was not admitted",
      );
      await page
        .getByRole("link", { name: "Create project", exact: true })
        .click();
      await expect(heading(page, "Create project")).toBeVisible();
      await expect(page.getByLabel("Name", { exact: true })).toHaveAttribute(
        "aria-describedby",
        /project-name-help/,
      );
      await page.getByLabel("Name", { exact: true }).fill("\u0085 \u0085");
      await page.getByLabel("Name", { exact: true }).press("Enter");
      await expect(
        page.getByRole("alert", {
          name: "Project fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      expect(wire.postCount).toBe(0);
      await fill(page, bom);
      const contextBefore = wire.contextCount;
      const projectRequestsBefore = wire.projectRequestCount;
      await page.getByLabel("Name", { exact: true }).press("Enter");
      await confirmed(page);
      expect(wire.postCount).toBe(1);
      const result = await wire.original(0, bom, 201);
      await wire.freshAdmission(0, contextBefore);
      baseline = await capture(owner, result, baseline);
      const current = await wire.latestProject(
        result.receipt.project.projectId,
        projectRequestsBefore,
      );
      requireCreation(
        current.status === 200 &&
          current.project?.name === bom.acceptedName &&
          current.project.description === bom.description &&
          current.project.access.role === "administrator",
        "fresh ordinary GET rejected or changed the server-legal BOM project",
      );
      await expect(page.locator(".create-project img")).toHaveCount(0);
      expect(
        await page.locator("html").getAttribute("data-creation-markup"),
      ).toBeNull();
      await page
        .getByRole("link", { name: "Open project", exact: true })
        .click();
      await expect(page).toHaveURL(
        new RegExp(projectPath(result.receipt.project) + "$"),
      );
      await expect(
        page.getByRole("heading", { name: "No mappings", exact: true }),
      ).toBeVisible();
      requireCreation(
        (await page.getByRole("heading", { level: 1 }).textContent())?.includes(
          bom.acceptedName,
        ),
        "BOM-only ordinary project heading changed",
      );
      await page.goBack();
      await confirmed(page);
      await named(page, "Start another project").click();
    });

    await test.step("actual project Administrator cannot create without the independent organization capability", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "projectAdministrator",
      });
      await refresh(page, wire);
      await expect(named(page, "Create project")).toBeDisabled();
      const context = await wire.latestContext();
      requireCreation(
        context.actorPrincipalId ===
          fixture.profiles.projectAdministrator.actorPrincipalId &&
          context.features.createProjects &&
          !context.organizationActions.createProjects,
        "project role incorrectly granted organization capability",
      );
      await page
        .getByRole("link", { name: "Back to projects", exact: true })
        .click();
      await expect(
        page.getByRole("list", { name: "Projects", exact: true }),
      ).toBeVisible();
      await expect(
        page.getByRole("link", { name: "Create project", exact: true }),
      ).toHaveCount(0);
      const projectRequestsBefore = wire.projectRequestCount;
      await page
        .locator(`a[href="${projectPath(fixture.seedProject.scope)}"]`)
        .click();
      await expect(
        page
          .locator("dt", { hasText: /^Your role$/ })
          .locator("xpath=following-sibling::dd[1]"),
      ).toHaveText("administrator");
      const seed = await wire.latestProject(
        fixture.seedProject.scope.projectId,
        projectRequestsBefore,
      );
      requireCreation(
        seed.status === 200 &&
          seed.project?.access.role === "administrator" &&
          !seed.project.access.actions.createProjects,
        "seed role/action evidence differs",
      );
      await page.goBack();
      await expect(
        page.getByRole("list", { name: "Projects", exact: true }),
      ).toBeVisible();
      await page.goBack();
      await expect(named(page, "Create project")).toBeDisabled();
      expect(wire.postCount).toBe(1);
      await owner.control({ action: "restart-profile", profile: "creator" });
      await refresh(page, wire);
      await expect(named(page, "Create project")).toBeEnabled();
    });

    let original!: CreationResult;
    await test.step("actual committed response held across SPA actor change is quarantined; double click sends one frozen request", async () => {
      await fill(page, unicode);
      const held = wire.arm("hold-delivery");
      const contextBefore = wire.contextCount;
      await named(page, "Create project").dblclick();
      await expect.poll(held.entered).toBe(true);
      expect(wire.postCount).toBe(2);
      original = await wire.original(1, unicode, 201);
      await wire.freshAdmission(1, contextBefore);
      baseline = await capture(owner, original, baseline);
      await owner.control({
        action: "restart-profile",
        profile: "projectAdministrator",
      });
      await page
        .getByRole("link", { name: "Back to projects", exact: true })
        .click();
      await expect(heading(page, "Projects")).toBeVisible();
      await expect(
        page.getByRole("list", { name: "Projects", exact: true }),
      ).toBeVisible();
      await page.goBack();
      await expect(
        heading(page, "Creation information is retained for another identity"),
      ).toBeVisible();
      await expect(page.getByLabel("Name", { exact: true })).toHaveCount(0);
      await expect(named(page, "Recover creation")).toHaveCount(0);
      held.release();
      await refresh(page, wire);
      await expect(heading(page, "Project creation confirmed")).toHaveCount(0);
      expect(wire.postCount).toBe(2);
      await owner.control({ action: "restart-profile", profile: "creator" });
      await refresh(page, wire);
      await unknown(page);
      await expect(named(page, "Recover creation")).toBeEnabled();
      requireCreation(
        (await page.getByLabel("Name", { exact: true }).inputValue()) ===
          unicode.name,
        "original Unicode draft changed across SPA quarantine",
      );
      await expect(page.getByLabel("Name", { exact: true })).toBeDisabled();
    });

    await test.step("actual capability refusals preserve previous uncertainty and never rotate or automatically replay the request", async () => {
      const before = wire.postCount;
      await owner.control({ action: "revoke-capability" });
      const refusedContexts = wire.contextCount;
      await named(page, "Recover creation").click();
      await wire.admitted(refusedContexts);
      await unknown(page);
      await expect(named(page, "Recover creation")).toBeDisabled();
      expect(wire.postCount).toBe(before);
      expect(wire.contextCount).toBe(refusedContexts + 1);
      expect(await owner.counts()).toEqual(baseline);
      await owner.control({ action: "restore-capability" });
      await refresh(page, wire);
      await expect(named(page, "Recover creation")).toBeEnabled();
      expect(wire.postCount).toBe(before);
      const held = wire.arm("hold-forward");
      const boundaryContexts = wire.contextCount;
      await named(page, "Recover creation").click();
      await expect.poll(held.entered).toBe(true);
      await wire.freshAdmission(before, boundaryContexts);
      await owner.control({ action: "revoke-capability" });
      held.release();
      await wire.refused(before, 403, "permissionDenied");
      wire.sameOriginal(1, before);
      await unknown(page);
      expect(wire.postCount).toBe(before + 1);
      expect(await owner.counts()).toEqual(baseline);
      await owner.control({ action: "restore-capability" });
      await refresh(page, wire);
      await expect(named(page, "Recover creation")).toBeEnabled();
      expect(wire.postCount).toBe(before + 1);
    });

    await test.step("same request survives ordinary API and same-cluster PostgreSQL restart; replay history does not restore a demoted membership", async () => {
      const before = wire.postCount;
      await owner.control({ action: "api-stop" });
      await owner.control({ action: "api-start", profile: "creator" });
      await owner.control({ action: "pg-normal-restart" });
      await refresh(page, wire);
      await unknown(page);
      expect(wire.postCount).toBe(before);
      await owner.control({
        action: "membership-viewer",
        projectId: original.receipt.project.projectId,
      });
      const demoted = await owner.counts();
      expect(demoted).toEqual(baseline);
      const replayContexts = wire.contextCount;
      const projectRequestsBefore = wire.projectRequestCount;
      await named(page, "Recover creation").click();
      await confirmed(page);
      const replay = await wire.original(before, unicode, 200);
      wire.sameOriginal(1, before);
      wire.sameReceipt(original.receipt, replay.receipt);
      await wire.freshAdmission(before, replayContexts);
      expect(wire.postCount).toBe(before + 1);
      expect(await owner.counts()).toEqual(demoted);
      const current = await wire.latestProject(
        original.receipt.project.projectId,
        projectRequestsBefore,
      );
      requireCreation(
        current.status === 200 && current.project?.access.role === "viewer",
        "historical receipt incorrectly restored current role",
      );
      await expect(
        page.getByText("Current project role: viewer.", { exact: true }),
      ).toBeVisible();
      await expect(
        page.getByText("Administrator · active · version 1", { exact: true }),
      ).toBeVisible();
      const posts = wire.postCount,
        contexts = wire.contextCount,
        reads = wire.projectReadCount;
      const accessReply = page.waitForResponse((reply) => {
        const url = new URL(reply.url());
        return (
          reply.request().method() === "GET" &&
          url.origin === fixture.readOrigin &&
          url.pathname === catalogApi(projectPath(original.receipt.project)) &&
          url.search === "" &&
          reply.status() === 200
        );
      });
      await named(page, "Retry project access").click();
      await accessResponseBody(accessReply);
      await confirmed(page);
      await wire.settle();
      expect(wire.postCount).toBe(posts);
      expect(wire.contextCount).toBe(contexts + 1);
      expect(wire.projectReadCount).toBe(reads + 1);
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width + 1);
      await named(page, "Start another project").click();
    });

    await test.step("actual response loss followed by membership deletion recovers one historical receipt while access retry remains GET-only", async () => {
      await fill(page, deleted);
      const lost = wire.arm("lose");
      const initial = wire.postCount;
      const createContexts = wire.contextCount;
      await named(page, "Create project").click();
      await expect.poll(lost.entered).toBe(true);
      await unknown(page);
      const first = await wire.original(initial, deleted, 201);
      baseline = await capture(owner, first, baseline);
      await wire.freshAdmission(initial, createContexts);
      await owner.control({
        action: "membership-delete",
        projectId: first.receipt.project.projectId,
      });
      const removed = await owner.counts();
      expect(removed).toEqual({
        ...baseline,
        projectMemberships: baseline.projectMemberships - 1,
      });
      const replayIndex = wire.postCount;
      const replayContexts = wire.contextCount;
      const projectRequestsBefore = wire.projectRequestCount;
      await named(page, "Recover creation").click();
      await confirmed(page);
      const replay = await wire.original(replayIndex, deleted, 200);
      wire.sameOriginal(initial, replayIndex);
      wire.sameReceipt(first.receipt, replay.receipt);
      await wire.freshAdmission(replayIndex, replayContexts);
      expect(await owner.counts()).toEqual(removed);
      requireCreation(
        (
          await wire.latestProject(
            first.receipt.project.projectId,
            projectRequestsBefore,
          )
        ).status === 404,
        "deleted membership unexpectedly granted project access",
      );
      await expect(
        page.getByRole("link", { name: "Open project", exact: true }),
      ).toHaveCount(0);
      await expect(
        page.getByText(
          "Creation remains confirmed, but current project access could not be loaded.",
          { exact: false },
        ),
      ).toBeVisible();
      const posts = wire.postCount,
        contexts = wire.contextCount,
        reads = wire.projectReadCount;
      const accessReply = page.waitForResponse((reply) => {
        const url = new URL(reply.url());
        return (
          reply.request().method() === "GET" &&
          url.origin === fixture.readOrigin &&
          url.pathname === catalogApi(projectPath(first.receipt.project)) &&
          url.search === "" &&
          reply.status() === 404
        );
      });
      await named(page, "Retry project access").click();
      await accessResponseBody(accessReply);
      await confirmed(page);
      await wire.settle();
      expect(wire.postCount).toBe(posts);
      expect(wire.contextCount).toBe(contexts + 1);
      expect(wire.projectReadCount).toBe(reads + 1);
      await expect(named(page, "Recover creation")).toHaveCount(0);
      expect(await owner.counts()).toEqual(removed);
      expect(removed.workersCreated).toBe(0);
    });
    await wire.verifyClosed();
  } finally {
    wire.release();
  }
});
