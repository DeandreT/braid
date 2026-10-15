import type { BrowserContext, Page, Request, Route } from "@playwright/test";
import { createHash } from "node:crypto";
import { isDeepStrictEqual } from "node:util";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  editPath,
  expect,
  mappingPath,
  requireMetadata,
  revisionPath,
  test,
  type MappingMetadataOwner,
  type Metadata,
  type Profile,
} from "./mapping-metadata.fixture";

type Context = components["schemas"]["CatalogContextV1"];
type Mapping = components["schemas"]["CatalogMappingV1"];
type Draft = {
  readonly name: string;
  readonly description: string;
  readonly acceptedName: string;
};
type Baseline = { readonly body: Metadata; readonly tag: string };
type Read = {
  readonly path: string;
  readonly status: number;
  readonly value: unknown;
  readonly tag?: string;
};
type Patch = {
  readonly request: Request;
  readonly body: string;
  readonly tag: string;
  readonly token: string;
  readonly context: number;
  readonly mappingRead: number;
  readonly baseline: Baseline;
  readonly draft: Draft;
  readonly done: Promise<void>;
  readonly finish: () => void;
  status?: number;
  value?: unknown;
  result?: Baseline;
  changed?: boolean;
};
type Delivery = "normal" | "lose" | "hold-forward";
const sha = (text: string) =>
  createHash("sha256").update(text, "utf8").digest("hex");

/** Private envelope comparisons; every PATCH forwards the original actual browser request exactly once. */
class MetadataWire {
  private baseline: Baseline | null = null;
  private readonly reads: Read[] = [];
  private readonly tasks: Promise<void>[] = [];
  private readonly contexts: Request[] = [];
  private readonly contextValues = new Map<Request, Context>();
  private readonly mappings: Request[] = [];
  private readonly mappingValues = new Map<Request, Mapping>();
  private readonly patches: Patch[] = [];
  private readonly releases: (() => void)[] = [];
  private plan: {
    draft: Draft;
    profile: Profile;
    delivery: Delivery;
    entered: () => void;
    wait: Promise<void>;
  } | null = null;
  private failed = false;
  private unexpected = false;
  readonly metadataPath: string;
  readonly mappingApi: string;
  constructor(
    private readonly page: Page,
    private readonly owner: MappingMetadataOwner,
  ) {
    this.mappingApi = catalogApi(mappingPath(owner.manifest.primary.scope));
    this.metadataPath = this.mappingApi + "/metadata";
  }
  get patchCount(): number {
    return this.patches.length;
  }
  get contextCount(): number {
    return this.contexts.length;
  }
  get actionReadCount(): number {
    return this.mappings.length;
  }
  get metadataReadCount(): number {
    return this.reads.filter((r) => r.path === this.metadataPath).length;
  }
  async settle(): Promise<void> {
    await Promise.all(this.tasks);
    requireMetadata(
      !this.failed && !this.unexpected,
      "actual HTTP observation or closed request boundary failed",
    );
  }
  private admitted(value: unknown, tag: string | undefined): Baseline {
    requireMetadata(
      value !== null && typeof value === "object" && !Array.isArray(value),
      "actual metadata body is not an object",
    );
    const body = value as Metadata,
      scope = this.owner.manifest.primary.scope;
    requireMetadata(
      Object.keys(body).sort().join("|") === "description|name|scope|version" &&
        body.scope &&
        Object.keys(body.scope).sort().join("|") ===
          "mappingId|organizationId|projectId" &&
        isDeepStrictEqual(body.scope, scope) &&
        typeof body.name === "string" &&
        typeof body.description === "string" &&
        typeof body.version === "string" &&
        /^[1-9][0-9]*$/.test(body.version) &&
        BigInt(body.version) <= 9223372036854775807n,
      "actual metadata scope/shape/version differs",
    );
    requireMetadata(
      tag ===
        `"metadata-v1:${scope.organizationId}:${scope.projectId}:${scope.mappingId}:${body.version}"`,
      "actual strong metadata tag differs",
    );
    return { body, tag };
  }
  async install(): Promise<void> {
    const org = this.owner.manifest.organizationId,
      origin = this.owner.manifest.readOrigin,
      id = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
    const projectApi = `/api/v1/organizations/${org}/projects/${this.owner.manifest.project.scope.projectId}`;
    const exactGets = new Set([
      projectApi,
      this.mappingApi,
      catalogApi(mappingPath(this.owner.manifest.unrelated.scope)),
      this.metadataPath,
    ]);
    const listGets = new Set([
      projectApi + "/mappings",
      this.mappingApi + "/revisions",
    ]);
    const revisionGet = new RegExp(`^${this.mappingApi}/revisions/${id}$`);
    this.page.on("response", (reply) => {
      const request = reply.request(),
        url = new URL(reply.url());
      if (
        request.method() !== "GET" ||
        url.origin !== origin ||
        !url.pathname.startsWith("/api/")
      )
        return;
      const task = (async () => {
        try {
          const value: unknown = await reply.json(),
            tag = reply.headers()["etag"];
          this.reads.push({
            path: url.pathname,
            status: reply.status(),
            value,
            tag,
          });
          if (
            url.pathname === "/api/v1/catalog/context" &&
            reply.status() === 200
          )
            this.contextValues.set(request, value as Context);
          if (url.pathname === this.mappingApi && reply.status() === 200) {
            requireMetadata(
              tag === undefined,
              "ordinary mapping GET unexpectedly supplied metadata validator",
            );
            this.mappingValues.set(request, value as Mapping);
          }
          if (url.pathname === this.metadataPath && reply.status() === 200)
            this.baseline = this.admitted(value, tag);
        } catch {
          this.failed = true;
        }
      })();
      this.tasks.push(task);
    });
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request(),
        url = new URL(request.url()),
        headers = request.headers();
      const names = [...url.searchParams.keys()],
        list =
          names.length > 0 &&
          names.every((n) => n === "limit" || n === "cursor") &&
          url.searchParams.getAll("limit").length === 1 &&
          url.searchParams.get("limit") === "10" &&
          url.searchParams.getAll("cursor").length <= 1;
      const allowedGet =
        ((url.pathname === "/api/v1/catalog/context" ||
          exactGets.has(url.pathname) ||
          revisionGet.test(url.pathname)) &&
          names.length === 0) ||
        (listGets.has(url.pathname) && list);
      if (
        url.origin !== origin ||
        (request.method() === "GET"
          ? !allowedGet
          : request.method() !== "PATCH" ||
            url.pathname !== this.metadataPath ||
            names.length !== 0)
      ) {
        this.unexpected = true;
        await route.abort("blockedbyclient");
        return;
      }
      if (request.method() === "GET") {
        requireMetadata(
          ![
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
          ].some((n) => n in headers),
          "metadata/catalog read unexpectedly became conditional",
        );
        if (url.pathname === "/api/v1/catalog/context")
          this.contexts.push(request);
        if (url.pathname === this.mappingApi) this.mappings.push(request);
        await route.continue();
        return;
      }
      const plan = this.plan;
      this.plan = null;
      requireMetadata(
        plan && this.baseline,
        "unplanned metadata PATCH or absent actual baseline",
      );
      const body = request.postData(),
        tag = headers["if-match"],
        token = headers["x-xsrf-token"];
      requireMetadata(
        body !== null &&
          Buffer.byteLength(body) <= 65536 &&
          tag === this.baseline.tag &&
          typeof token === "string" &&
          token.length > 0 &&
          headers["content-type"] ===
            "application/vnd.weft.mapping-metadata-edit.v1+json" &&
          headers["origin"] === origin &&
          ![
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "idempotency-key",
            "content-encoding",
          ].some((n) => n in headers),
        "actual PATCH envelope differs from received validator/transport",
      );
      let finish!: () => void;
      const done = new Promise<void>((resolve) => {
        finish = resolve;
      });
      const patch: Patch = {
        request,
        body,
        tag,
        token,
        context: this.contexts.length - 1,
        mappingRead: this.mappings.length - 1,
        baseline: this.baseline,
        draft: plan.draft,
        done,
        finish,
      };
      this.patches.push(patch);
      try {
        if (plan.delivery === "hold-forward") {
          plan.entered();
          await plan.wait;
        }
        const reply = await route.fetch({
          maxRetries: 0,
          maxRedirects: 0,
          timeout: 30000,
        });
        patch.status = reply.status();
        patch.value = await reply.json();
        await this.settle();
        const context = this.contextValues.get(this.contexts[patch.context]),
          mapping = this.mappingValues.get(this.mappings[patch.mappingRead]);
        requireMetadata(
          context?.actorPrincipalId ===
            this.owner.manifest.profiles[plan.profile].actorPrincipalId &&
            context.allowedOrganizationId === org &&
            context.features.editMetadata &&
            context.requestToken === token &&
            mapping?.access.actions.editMetadata,
          "PATCH lacked fresh same-actor context/action/token",
        );
        let raw: unknown;
        try {
          raw = JSON.parse(body);
        } catch {
          throw new Error(
            "Mapping metadata browser fixture: invalid raw PATCH JSON",
          );
        }
        const input = raw as Record<string, unknown>;
        requireMetadata(
          raw !== null &&
            typeof raw === "object" &&
            !Array.isArray(raw) &&
            Object.keys(input).sort().join("|") === "description|name" &&
            input.name === plan.draft.name &&
            input.description === plan.draft.description,
          "exact raw replacement fields changed",
        );
        if (reply.status() === 200) {
          const result = this.admitted(patch.value, reply.headers()["etag"]),
            changed =
              plan.draft.acceptedName !== patch.baseline.body.name ||
              plan.draft.description !== patch.baseline.body.description;
          requireMetadata(
            result.body.name === plan.draft.acceptedName &&
              result.body.description === plan.draft.description &&
              result.body.version ===
                (changed
                  ? (BigInt(patch.baseline.body.version) + 1n).toString()
                  : patch.baseline.body.version),
            "actual successful replacement/version differs",
          );
          const ack = await this.owner.control({
            action: "capture-edit",
            previousVersion: patch.baseline.body.version,
            version: result.body.version,
            nameSha256: sha(result.body.name),
            descriptionSha256: sha(result.body.description),
          });
          requireMetadata(
            ack.action === "capture-edit" && ack.changed === changed,
            "independent captured edit delta differs",
          );
          patch.result = result;
          patch.changed = changed;
          this.baseline = result;
        } else {
          requireMetadata(
            reply.headers()["etag"] === undefined &&
              patch.value !== null &&
              typeof patch.value === "object" &&
              !Array.isArray(patch.value) &&
              Object.keys(patch.value).join("|") === "code",
            "actual refusal disclosed metadata or validator",
          );
        }
        patch.finish();
        if (plan.delivery !== "hold-forward") plan.entered();
        if (plan.delivery === "lose") await route.abort("failed");
        else await route.fulfill({ response: reply });
      } catch {
        if (!request.failure()) this.failed = true;
        patch.finish();
      }
    });
  }
  async arm(
    draft: Draft,
    profile: Profile = "publisher",
    delivery: Delivery = "normal",
  ): Promise<{ entered: () => boolean; release: () => void }> {
    await this.settle();
    requireMetadata(
      !this.plan && this.baseline,
      "save requires one actual metadata baseline and no pending plan",
    );
    let entered = false,
      release!: () => void;
    const wait = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.releases.push(release);
    this.plan = {
      draft,
      profile,
      delivery,
      entered: () => {
        entered = true;
      },
      wait,
    };
    return { entered: () => entered, release };
  }
  async complete(index: number, status: number): Promise<void> {
    const patch = this.patches[index];
    requireMetadata(patch, "expected deliberate PATCH");
    await Promise.race([
      patch.done,
      new Promise<never>((_, reject) => {
        const timer = setTimeout(
          () =>
            reject(
              new Error(
                "Mapping metadata browser fixture: actual PATCH observation deadline exceeded",
              ),
            ),
          50000,
        );
        void patch.done.finally(() => clearTimeout(timer));
      }),
    ]);
    await this.settle();
    requireMetadata(patch.status === status, "actual PATCH status differs");
    if (status === 412)
      requireMetadata(
        (patch.value as Record<string, unknown>).code === "preconditionFailed",
        "actual CAS refusal differs",
      );
  }
  async finalRefusal(index: number): Promise<void> {
    await this.patches[index].done;
    await this.settle();
    const patch = this.patches[index],
      code = (patch.value as Record<string, unknown>).code;
    requireMetadata(
      (patch.status === 404 &&
        (code === "notFound" || code === "unavailable")) ||
        (patch.status === 403 && code === "identityRejected"),
      "actual final authority refusal differs",
    );
  }
  async fresh(
    index: number,
    contexts: number,
    mappings: number,
  ): Promise<void> {
    await this.settle();
    const patch = this.patches[index];
    requireMetadata(
      patch.context === contexts && patch.mappingRead === mappings,
      "Save did not refresh context/action exactly once",
    );
  }
  async sameBaseline(other: MetadataWire): Promise<void> {
    await this.settle();
    await other.settle();
    requireMetadata(
      this.baseline &&
        other.baseline &&
        this.baseline.tag === other.baseline.tag &&
        isDeepStrictEqual(this.baseline.body, other.baseline.body),
      "actual metadata validator changed across independent reads",
    );
  }
  async latestMapping(): Promise<Mapping> {
    await this.settle();
    const value = this.mappingValues.get(this.mappings.at(-1)!);
    requireMetadata(value, "expected ordinary mapping view");
    return value;
  }
  async version(): Promise<string> {
    await this.settle();
    requireMetadata(this.baseline, "expected real metadata baseline");
    return this.baseline.body.version;
  }
  release(): void {
    for (const release of this.releases) release();
  }
  async closed(): Promise<void> {
    await this.settle();
    requireMetadata(!this.plan, "unused metadata delivery control");
  }
}

const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
async function ready(page: Page): Promise<void> {
  await expect(heading(page, "Edit mapping metadata")).toBeVisible();
  await expect(button(page, "Save metadata")).toBeEnabled();
}
async function fill(page: Page, draft: Draft): Promise<void> {
  await page.getByLabel("Name", { exact: true }).fill(draft.name);
  await page
    .getByLabel("Description (optional)", { exact: true })
    .fill(draft.description);
}
async function refresh(page: Page): Promise<void> {
  await button(page, "Refresh admission").click();
  await expect(button(page, "Refresh admission")).toBeEnabled();
}
async function saved(page: Page): Promise<void> {
  await expect(heading(page, "Metadata save confirmed")).toBeVisible();
  await expect(button(page, "Save metadata")).toBeEnabled();
}
async function save(
  page: Page,
  wire: MetadataWire,
  draft: Draft,
  profile: Profile = "publisher",
): Promise<number> {
  await fill(page, draft);
  await wire.arm(draft, profile);
  const index = wire.patchCount,
    contexts = wire.contextCount,
    mappings = wire.actionReadCount;
  await button(page, "Save metadata").click();
  await saved(page);
  await wire.complete(index, 200);
  await wire.fresh(index, contexts, mappings);
  return index;
}

test.describe.configure({ mode: "serial" });
test("real packaged mapping metadata: strong-validator CAS, preserved native history and GET-only uncertain-save reconciliation", async ({
  page,
  browser,
  mappingMetadataOwner: owner,
}) => {
  test.setTimeout(600000);
  const fixture = owner.manifest,
    path = editPath(fixture.primary.scope),
    wire = new MetadataWire(page, owner);
  await wire.install();
  let secondContext: BrowserContext | undefined,
    other: MetadataWire | undefined;
  try {
    const initial = await owner.checkpoint();
    expect(initial.counts.workersCreated).toBeGreaterThan(0);
    await page.setViewportSize({ width: 390, height: 844 });
    await test.step("actual Viewer, Runner and feature-off direct routes cannot expose or submit the editor", async () => {
      for (const profile of ["viewer", "runner"] as const) {
        await owner.control({
          action: "restart-profile",
          profile,
          mode: "edit",
        });
        await page.goto(path);
        await expect(
          heading(page, "Metadata editor unavailable"),
        ).toBeVisible();
        await expect(page.getByLabel("Name", { exact: true })).toHaveCount(0);
        expect(wire.patchCount).toBe(0);
        expect((await owner.checkpoint()).state).toEqual(initial.state);
      }
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "featureOff",
      });
      await page.goto(path);
      await expect(heading(page, "Metadata editor unavailable")).toBeVisible();
      await expect(page.getByLabel("Name", { exact: true })).toHaveCount(0);
      expect(wire.patchCount).toBe(0);
    });
    await test.step("actual Administrator saves and exact trimmed no-op preserve timestamps; BOM-only remains ordinarily readable", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "edit",
      });
      await page.goto(path);
      await ready(page);
      const before = wire.patchCount;
      await page.getByLabel("Name", { exact: true }).fill("\u0085 \u0085");
      await page.getByLabel("Name", { exact: true }).press("Enter");
      await expect(
        page.getByRole("alert", {
          name: "Metadata fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      expect(wire.patchCount).toBe(before);
      const admin: Draft = {
        name: "Administrator metadata",
        acceptedName: "Administrator metadata",
        description: " Administrator exact description. ",
      };
      await save(page, wire, admin, "administrator");
      const state = await owner.checkpoint(),
        version = await wire.version();
      await save(
        page,
        wire,
        { ...admin, name: " \u0085" + admin.name + "\u0085 " },
        "administrator",
      );
      expect(await wire.version()).toBe(version);
      expect(await owner.checkpoint()).toEqual(state);
      await save(
        page,
        wire,
        { name: "\uFEFF", acceptedName: "\uFEFF", description: "" },
        "administrator",
      );
      const mappingResponse = page.waitForResponse((response) => {
        const request = response.request(),
          url = new URL(response.url());
        return (
          request.method() === "GET" &&
          url.origin === fixture.readOrigin &&
          url.pathname === wire.mappingApi &&
          !url.search &&
          response.status() === 200
        );
      });
      await page
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(page).toHaveURL(
        new RegExp(mappingPath(fixture.primary.scope) + "$"),
      );
      await expect(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
      ).toBeVisible();
      expect(await (await mappingResponse).finished()).toBeNull();
      await wire.settle();
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      requireMetadata(
        (await wire.latestMapping()).name === "\uFEFF",
        "ordinary read rejected the accepted BOM-only name",
      );
    });
    const unicode: Draft = {
      name: " \u0085" + "😀".repeat(200) + "\u0085 ",
      acceptedName: "😀".repeat(200),
      description:
        " \uFEFFe\u0301\n<img src=controlled-invalid onerror=document.documentElement.dataset.metadataMarkup=1> ",
    };
    let active!: Page;
    await test.step("two actual independent Publisher sessions receive one tag, then stale unchanged CAS412 preserves raw mine until explicit review/save", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "edit",
      });
      await page.goto(path);
      await ready(page);
      secondContext = await browser.newContext({
        baseURL: fixture.readOrigin,
        viewport: { width: 390, height: 844 },
      });
      active = await secondContext.newPage();
      const contender = new MetadataWire(active, owner);
      other = contender;
      await contender.install();
      await active.goto(path);
      await ready(active);
      await wire.sameBaseline(contender);
      const mine: Draft = {
        name: " \u0085\uFEFF\u0085 ",
        acceptedName: "\uFEFF",
        description: "",
      };
      await fill(active, mine);
      await save(page, wire, unicode);
      const trusted = await owner.checkpoint();
      const index = contender.patchCount,
        contexts = contender.contextCount,
        mappings = contender.actionReadCount;
      await contender.arm(mine);
      await button(active, "Save metadata").click();
      await expect(heading(active, "Review before another save")).toBeVisible();
      await contender.complete(index, 412);
      await contender.fresh(index, contexts, mappings);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          mine.name &&
          (await active
            .getByLabel("Description (optional)", { exact: true })
            .inputValue()) === mine.description,
        "actual conflict replaced the raw draft",
      );
      await expect(button(active, "Save metadata")).toBeDisabled();
      expect(await owner.checkpoint()).toEqual(trusted);
      await button(active, "Review latest").click();
      await expect(heading(active, "Compare mapping metadata")).toBeVisible();
      for (const region of ["Base metadata", "Current metadata", "My metadata"])
        await expect(
          active.getByRole("region", { name: region, exact: true }),
        ).toBeVisible();
      expect(contender.patchCount).toBe(index + 1);
      await button(active, "Keep mine").click();
      await save(active, contender, mine);
      await expect(active.locator(".metadata-editor img")).toHaveCount(0);
      expect(
        await active.locator("html").getAttribute("data-metadata-markup"),
      ).toBeNull();
    });
    requireMetadata(other, "expected second actual session");
    const editorWire = other;
    await test.step("dirty navigation is deliberate; a genuine native append leaves the received metadata tag valid and the next edit preserves its new head", async () => {
      const draft: Draft = {
        name: "After the native append",
        acceptedName: "After the native append",
        description: " Exact metadata survives the new native head. ",
      };
      await fill(active, draft);
      await active
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(heading(active, "Leave this metadata edit?")).toBeVisible();
      await button(active, "Keep editing").click();
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "leave decision discarded retained draft",
      );
      const before = await owner.checkpoint(),
        patches = editorWire.patchCount;
      expect(before.counts).toEqual(initial.counts);
      const append = await owner.control({ action: "append-primary-revision" });
      requireMetadata(
        append.action === "append-primary-revision",
        "expected genuine append",
      );
      const after = await owner.checkpoint();
      expect(after.state.version).toBe(before.state.version);
      expect(after.state.headRevisionId).toBe(
        append.appendedRevision.scope.revisionId,
      );
      expect(after.counts.revisions).toBe(before.counts.revisions + 1);
      expect(after.counts.reportAttempts).toBe(before.counts.reportAttempts);
      expect(after.counts.reportResults).toBe(before.counts.reportResults);
      expect(after.counts.workersCreated).toBe(
        before.counts.workersCreated + 1,
      );
      await page.goto(path);
      await ready(page);
      await wire.sameBaseline(editorWire);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "append reset dirty metadata",
      );
      await refresh(active);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "authority refresh reset dirty metadata",
      );
      expect(editorWire.patchCount).toBe(patches);
      await save(active, editorWire, draft);
      const edited = await owner.checkpoint();
      expect(edited.state.headRevisionId).toBe(after.state.headRevisionId);
      expect(edited.state.headSequence).toBe(after.state.headSequence);
      expect(edited.counts).toEqual(after.counts);
    });
    await test.step("fresh Publisher revocation suppresses PATCH and the final actual server authority check refuses a previously admitted request", async () => {
      const draft: Draft = {
        name: "Retained after authority refusal",
        acceptedName: "Retained after authority refusal",
        description: " No automatic write on restoration. ",
      };
      await fill(active, draft);
      let count = editorWire.patchCount;
      await owner.control({ action: "revoke-publisher" });
      await button(active, "Save metadata").click();
      await expect(
        heading(active, "Metadata editor unavailable"),
      ).toBeVisible();
      await expect(active.getByLabel("Name", { exact: true })).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count);
      await owner.checkpoint();
      await owner.control({ action: "restore-publisher" });
      await refresh(active);
      await ready(active);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "same-actor restoration lost retained draft",
      );
      expect(editorWire.patchCount).toBe(count);
      const contexts = editorWire.contextCount,
        mappings = editorWire.actionReadCount,
        held = await editorWire.arm(draft, "publisher", "hold-forward");
      await button(active, "Save metadata").click();
      await expect.poll(held.entered).toBe(true);
      await editorWire.fresh(count, contexts, mappings);
      await owner.control({ action: "revoke-publisher" });
      held.release();
      await editorWire.finalRefusal(count);
      expect(editorWire.patchCount).toBe(count + 1);
      await owner.checkpoint();
      await owner.control({ action: "restore-publisher" });
      await refresh(active);
      await ready(active);
      expect(editorWire.patchCount).toBe(count + 1);
    });
    await test.step("a real completed200 is independently captured before delivery loss; explicit matching GET never claims that save was acknowledged", async () => {
      const draft: Draft = {
        name: "Unacknowledged actual metadata",
        acceptedName: "Unacknowledged actual metadata",
        description: " Exact lost-response description. ",
      };
      await fill(active, draft);
      const count = editorWire.patchCount,
        contexts = editorWire.contextCount,
        mappings = editorWire.actionReadCount;
      await editorWire.arm(draft, "publisher", "lose");
      await button(active, "Save metadata").click();
      await editorWire.complete(count, 200);
      await editorWire.fresh(count, contexts, mappings);
      await expect(heading(active, "Save outcome is unknown")).toBeVisible();
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      const trusted = await owner.checkpoint(),
        reads = editorWire.metadataReadCount;
      await button(active, "Check current metadata").click();
      await expect(
        active.getByText("Current values match the attempted values.", {
          exact: false,
        }),
      ).toBeVisible();
      await expect(heading(active, "Save outcome is unknown")).toBeVisible();
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count + 1);
      await editorWire.settle();
      expect(editorWire.metadataReadCount).toBe(reads + 1);
      expect(await owner.checkpoint()).toEqual(trusted);
      await button(active, "Review latest").click();
      await expect(heading(active, "Compare mapping metadata")).toBeVisible();
      await button(active, "Use current").click();
      await expect(heading(active, "Save outcome is unknown")).toHaveCount(0);
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count + 1);
    });
    await test.step("ordinary API and same-owned PostgreSQL restart preserve current metadata plus original immutable revision and saved report facts", async () => {
      const before = await owner.checkpoint();
      const mappingResponse = active.waitForResponse((response) => {
        const request = response.request(),
          url = new URL(response.url());
        return (
          request.method() === "GET" &&
          url.origin === fixture.readOrigin &&
          url.pathname === editorWire.mappingApi &&
          !url.search &&
          response.status() === 200
        );
      });
      await active
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(active).toHaveURL(
        new RegExp(mappingPath(fixture.primary.scope) + "$"),
      );
      await expect(
        active.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
      ).toBeVisible();
      expect(await (await mappingResponse).finished()).toBeNull();
      await editorWire.settle();
      await active.goto(revisionPath(fixture.primary.initialRevision.scope));
      await expect(active.getByRole("heading", { level: 1 })).toHaveText(
        `Revision ${fixture.primary.initialRevision.sequence}`,
      );
      await owner.control({ action: "api-stop" });
      await owner.control({
        action: "api-start",
        profile: "publisher",
        mode: "edit",
      });
      await owner.control({ action: "pg-normal-restart" });
      await page.goto(path);
      await ready(page);
      await wire.sameBaseline(editorWire);
      await active.reload();
      await expect(active.getByRole("heading", { level: 1 })).toHaveText(
        `Revision ${fixture.primary.initialRevision.sequence}`,
      );
      const revisionId = active
        .locator("dt", { hasText: /^Revision ID$/ })
        .locator("xpath=following-sibling::dd[1]");
      await expect(revisionId).toHaveText(
        fixture.primary.initialRevision.scope.revisionId,
      );
      const digest = active
        .locator("dt", { hasText: /^Native digest$/ })
        .locator("xpath=following-sibling::dd[1]");
      requireMetadata(
        (await digest.textContent())?.includes(
          fixture.primary.initialRevision.nativeDigest.value,
        ),
        "original native digest changed after metadata edits/append/restart",
      );
      const after = await owner.checkpoint();
      expect(after).toEqual(before);
      expect(after.counts).toEqual({
        ...initial.counts,
        revisions: initial.counts.revisions + 1,
        workersCreated: initial.counts.workersCreated + 1,
      });
      expect(after.counts.reportResults).toBeGreaterThanOrEqual(2);
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width + 1);
    });
    await wire.closed();
    await editorWire.closed();
  } finally {
    wire.release();
    other?.release();
    await secondContext?.close();
  }
});
