import type { Locator, Page, Request, Response } from "@playwright/test";
import type { components } from "../src/api/generated";
import { isSessionStatusRequest } from "./browser-session-status.fixture";
import { organizationContextPath } from "./organization-context.fixture";
import {
  catalogApi,
  catalogWriteAction,
  expect,
  expectRevision,
  mappingPath,
  metadataValue,
  projectPath,
  projectsPath,
  revisionPath,
  test,
  type Revision,
} from "./catalog.fixture";

type ProjectPage = components["schemas"]["CatalogProjectPageV1"];
type MappingPage = components["schemas"]["CatalogMappingPageV1"];
type RevisionPage = components["schemas"]["CatalogRevisionPageV1"];

async function capture<T>(
  page: Page,
  path: string,
  action: () => Promise<unknown>,
  query: Readonly<Record<string, string>> = {},
): Promise<T> {
  const origin = process.env.BRAID_CATALOG_BASE_URL;
  if (!origin) throw new Error("Expected explicit packaged catalog origin");
  const expected = new URL(path, origin);
  const requests = new Set<Request>();
  const pendingRequests = new Set<Request>();
  const responses = new Set<Request>();
  const failedRequests = new Map<Request, string | undefined>();
  const pending = new Set<Promise<void>>();
  const values: { usable: boolean; value?: T }[] = [];
  const diagnostic: string[] = [];
  // Retain only method, path and status, never cursors, bodies, headers or tokens.
  const note = (request: Request, state: string) => {
    const url = new URL(request.url());
    if (url.origin !== expected.origin || !url.pathname.startsWith("/api/"))
      return;
    const label = `${request.method()} ${url.pathname} ${state}`;
    if (!diagnostic.includes(label) && diagnostic.length < 24)
      diagnostic.push(label);
  };
  const stop = Symbol("catalog capture stopped");
  let stopReads!: () => void;
  const stopped = new Promise<typeof stop>((resolve) => {
    stopReads = () => resolve(stop);
  });
  type Outcome = { ok: true; value: T } | { ok: false; error: unknown };
  let finish!: (outcome: Outcome) => void;
  const done = new Promise<Outcome>((resolve) => {
    finish = resolve;
  });
  let closed = false;
  let actionDone = false;
  const close = () => {
    if (closed) return;
    closed = true;
    page.off("request", observe);
    page.off("response", observeResponse);
    page.off("requestfailed", observeFailure);
    stopReads();
  };
  const fail = (error: unknown) => {
    if (closed) return;
    close();
    finish({ ok: false, error });
  };
  const complete = () => {
    if (closed || !actionDone || pendingRequests.size || pending.size) return;
    const first = values.find((value) => value.usable);
    if (!first) return;
    close();
    finish({ ok: true, value: first.value as T });
  };
  const observe = (request: Request) => {
    note(request, "sent");
    const url = new URL(request.url());
    let queryCount = 0;
    url.searchParams.forEach(() => {
      queryCount++;
    });
    if (
      request.method() === "GET" &&
      url.origin === expected.origin &&
      url.pathname === expected.pathname &&
      queryCount === Object.keys(query).length &&
      Object.entries(query).every(
        ([key, value]) =>
          url.searchParams.getAll(key).length === 1 &&
          url.searchParams.get(key) === value,
      )
    ) {
      requests.add(request);
      pendingRequests.add(request);
    }
  };
  const observeFailure = (request: Request) => {
    note(request, "did not complete");
    if (!requests.has(request)) return;
    const errorText = request.failure()?.errorText;
    failedRequests.set(request, errorText);
    if (errorText !== "net::ERR_ABORTED") {
      fail(new Error("Expected successful actual catalog request"));
    } else if (!responses.has(request)) {
      pendingRequests.delete(request);
      complete();
    }
  };
  const read = async (
    reply: Response,
    value: { usable: boolean; value?: T },
  ) => {
    expect(reply.status()).toBe(200);
    let body: Awaited<ReturnType<Response["body"]>>;
    try {
      const received = await Promise.race([reply.body(), stopped]);
      if (received === stop) return;
      body = received;
    } catch {
      if (failedRequests.get(reply.request()) === "net::ERR_ABORTED") return;
      throw new Error("Expected usable actual catalog response body");
    }
    try {
      value.value = JSON.parse(body.toString("utf8")) as T;
    } catch {
      throw new Error("Expected valid actual catalog response JSON");
    }
    value.usable = true;
  };
  const observeResponse = (reply: Response) => {
    const request = reply.request();
    note(request, String(reply.status()));
    if (!requests.has(request)) return;
    responses.add(request);
    const value: { usable: boolean; value?: T } = { usable: false };
    values.push(value);
    const task = read(reply, value);
    pending.add(task);
    void task.then(
      () => {
        pending.delete(task);
        pendingRequests.delete(request);
        complete();
      },
      (error: unknown) => {
        pending.delete(task);
        fail(error);
      },
    );
  };
  page.on("request", observe);
  page.on("response", observeResponse);
  page.on("requestfailed", observeFailure);
  // Use the catalog's existing expectation budget for the whole observation.
  const timer = setTimeout(() => {
    fail(
      new Error(
        `Expected usable actual catalog JSON from ${expected.pathname} within observation deadline; ` +
          `${requests.size} matching requests, ${responses.size} responses. ` +
          `Observed API traffic: ${diagnostic.join("; ") || "none"}`,
      ),
    );
  }, 10_000);
  let actionTask: Promise<void> = Promise.resolve();
  try {
    actionTask = Promise.race([action(), stopped]).then(() => {
      if (closed) return;
      actionDone = true;
      complete();
    }, fail);
    const outcome = await done;
    if (!outcome.ok) throw outcome.error;
    return outcome.value;
  } catch (error) {
    fail(error);
    throw error;
  } finally {
    close();
    clearTimeout(timer);
    await Promise.allSettled([...pending, actionTask]);
  }
}
function nextPageQuery(
  cursor: string | null | undefined,
): Record<string, string> {
  if (typeof cursor !== "string")
    throw new Error("Expected actual emitted next-page cursor");
  return { limit: "10", cursor };
}
async function linkPaths(list: Locator): Promise<string[]> {
  return list
    .getByRole("link")
    .evaluateAll((links) =>
      links.map((link) => new URL((link as HTMLAnchorElement).href).pathname),
    );
}
async function readOnly(page: Page): Promise<void> {
  await expect(
    page.getByRole("button", { name: catalogWriteAction }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("link", { name: catalogWriteAction }),
  ).toHaveCount(0);
  await expect(page.locator("input[type=file],textarea,select")).toHaveCount(0);
}

test.describe.configure({ mode: "serial" });
test("real packaged catalog: roles, all page boundaries, immutable history, restarts and one-way revocations", async ({
  page,
  catalogOwner,
}) => {
  test.setTimeout(600_000);
  const fixture = catalogOwner.manifest;
  const project = fixture.projects.primary;
  const mapping = fixture.mappings.primary;
  const original = fixture.history.original;
  const projects = projectsPath(fixture.organizationId);
  let mappingName: string;
  const forbiddenReads: string[] = [];
  const scopedReads: string[] = [];
  const contextPath = organizationContextPath(fixture.organizationId);
  const foreignContext = organizationContextPath(
    fixture.projects.foreign.organizationId,
  );
  const id = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
  const resourceRead = new RegExp(
    `^/api/v1/organizations/${id}/projects(?:/${id}(?:/mappings(?:/${id}(?:/revisions(?:/${id})?)?)?)?)?$`,
  );
  // The catalog overview also reads lifecycle, capability and check status.
  // Admit only those exact endpoints on the fixture organization.
  const overviewRead = new RegExp(
    `^/api/v1/organizations/${fixture.organizationId}/projects/${id}/(?:creation-capabilities|lifecycle|automation|regression-suites/${id}|mappings/${id}/(?:lifecycle|automation))$`,
  );
  page.context().on("request", (request) => {
    const { origin, pathname: path } = new URL(request.url());
    if (/^\/api\/v1\/organizations\/[^/]+\/projects(?:\/|$)/.test(path))
      scopedReads.push(path);
    if (isSessionStatusRequest(request, fixture.readOrigin)) return;
    if (
      request.method() !== "GET" ||
      (path.startsWith("/api/") &&
        (origin !== fixture.readOrigin ||
          (path !== "/api/v1/catalog/context" &&
            path !== contextPath &&
            path !== foreignContext &&
            !resourceRead.test(path) &&
            !overviewRead.test(path))))
    )
      forbiddenReads.push(`${request.method()} ${path}`);
  });

  await test.step("real viewer projects preserve UUID order, exact emitted cursor and Back/First navigation", async () => {
    const first = await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => page.goto(projects),
      { limit: "10" },
    );
    expect(first.projects.map((p) => p.scope)).toEqual(
      fixture.projects.viewerVisibleUuidOrder.slice(0, 10),
    );
    expect(first.nextCursor).toEqual(expect.any(String));
    const list = page.getByRole("list", { name: "Projects", exact: true });
    await expect(list.getByRole("listitem")).toHaveCount(10);
    expect(await linkPaths(list)).toEqual(
      first.projects.map((p) => projectPath(p.scope)),
    );
    const pages = page.getByRole("navigation", { name: "Project pages" });
    const second = await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => pages.getByRole("link", { name: "Next page" }).click(),
      nextPageQuery(first.nextCursor),
    );
    expect(second.projects.map((p) => p.scope)).toEqual(
      fixture.projects.viewerVisibleUuidOrder.slice(10),
    );
    expect(new URL(page.url()).searchParams.get("cursor")).toBe(
      first.nextCursor,
    );
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await expect(pages.getByRole("link", { name: "Next page" })).toHaveCount(0);
    await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => page.goBack(),
      { limit: "10" },
    );
    await expect(list.getByRole("listitem")).toHaveCount(10);
    await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => pages.getByRole("link", { name: "Next page" }).click(),
      nextPageQuery(first.nextCursor),
    );
    await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => pages.getByRole("link", { name: "First page" }).click(),
      { limit: "10" },
    );
    expect(new URL(page.url()).searchParams.has("cursor")).toBe(false);
    await readOnly(page);
  });

  await test.step("real accessible empty, denied and foreign scopes remain distinct", async () => {
    const empty = await capture<MappingPage>(
      page,
      catalogApi(projectPath(fixture.projects.empty)) + "/mappings",
      () => page.goto(projectPath(fixture.projects.empty)),
      { limit: "10" },
    );
    expect(empty.mappings).toEqual([]);
    await expect(page.getByText("No mappings", { exact: true })).toBeVisible();
    await page.goto(projectPath(fixture.projects.deniedToViewer));
    await expect(
      page.getByRole("heading", { name: "Not found", exact: true }),
    ).toBeVisible();
    await page.goto(projectPath(fixture.projects.foreign));
    await expect(
      page.getByRole("heading", { name: "Not found", exact: true }),
    ).toBeVisible();
    expect(
      scopedReads.filter((path) =>
        path.startsWith(
          `/api/v1/organizations/${fixture.projects.foreign.organizationId}/`,
        ),
      ),
    ).toEqual([]);
    await expect(page.getByText("No mappings", { exact: true })).toHaveCount(0);
  });

  await test.step("real mapping pages retain exact API UUID order and opaque cursor", async () => {
    const endpoint = catalogApi(projectPath(project)) + "/mappings";
    const first = await capture<MappingPage>(
      page,
      endpoint,
      () => page.goto(projectPath(project)),
      { limit: "10" },
    );
    expect(first.mappings.map((m) => m.scope)).toEqual(
      fixture.mappings.uuidOrder.slice(0, 10),
    );
    const list = page.getByRole("list", { name: "Mappings", exact: true });
    await expect(list.getByRole("listitem")).toHaveCount(first.mappings.length);
    expect(await linkPaths(list)).toEqual(
      first.mappings.map((m) => mappingPath(m.scope)),
    );
    const next = page
      .getByRole("navigation", { name: "Mapping pages" })
      .getByRole("link", { name: "Next page" });
    const second = await capture<MappingPage>(
      page,
      endpoint,
      () => next.click(),
      nextPageQuery(first.nextCursor),
    );
    expect(second.mappings.map((m) => m.scope)).toEqual(
      fixture.mappings.uuidOrder.slice(10),
    );
    expect(new URL(page.url()).searchParams.get("cursor")).toBe(
      first.nextCursor,
    );
    await expect(list.getByRole("listitem")).toHaveCount(1);
    const cursor = first.nextCursor;
    if (typeof cursor !== "string")
      throw new Error("Expected actual emitted mappings cursor");
    const emptyMappings =
      catalogApi(projectPath(fixture.projects.empty)) + "/mappings";
    const rejected = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return (
        url.pathname === emptyMappings &&
        url.searchParams.get("cursor") === cursor &&
        response.request().method() === "GET"
      );
    });
    await page.goto(
      projectPath(fixture.projects.empty) +
        "?cursor=" +
        encodeURIComponent(cursor),
    );
    expect((await rejected).status()).toBe(400);
    await expect(
      page.getByRole("heading", { name: "Invalid link", exact: true }),
    ).toBeVisible();
    await expect(page.getByText("No mappings", { exact: true })).toHaveCount(0);
  });

  await test.step("each actual read role is displayed without exposing mutations; admitted no-project actor is empty", async () => {
    for (const profile of [
      "administrator",
      "viewer",
      "runner",
      "publisher",
    ] as const) {
      const ack = await catalogOwner.control("restart-profile", profile);
      expect(ack.outcome).toBe("ready");
      const value = await capture<components["schemas"]["CatalogProjectV1"]>(
        page,
        catalogApi(projectPath(project)),
        () => page.goto(projectPath(project)),
      );
      expect(value.access.role).toBe(profile);
      await expect(metadataValue(page, "Your role")).toHaveText(profile);
      await readOnly(page);
    }
    await catalogOwner.control("restart-profile", "noProjects");
    const empty = await capture<ProjectPage>(
      page,
      catalogApi(projects),
      () => page.goto(projects),
      { limit: "10" },
    );
    expect(empty.projects).toEqual([]);
    await expect(page.getByText("No projects", { exact: true })).toBeVisible();
    await catalogOwner.control("restart-profile", "viewer");
  });

  await test.step("real revision pages are descending strings and retain the original concrete URL", async () => {
    const endpoint = catalogApi(mappingPath(mapping)) + "/revisions";
    const [current, first] = await Promise.all([
      capture<components["schemas"]["CatalogMappingV1"]>(
        page,
        catalogApi(mappingPath(mapping)),
        async () => {},
      ),
      capture<RevisionPage>(
        page,
        endpoint,
        () => page.goto(mappingPath(mapping)),
        { limit: "10" },
      ),
    ]);
    expect(current.scope).toEqual(mapping);
    mappingName = current.name;
    expect(first.revisions).toEqual(
      fixture.history.sequenceDescending.slice(0, 10),
    );
    const list = page.getByRole("list", {
      name: "Revision history",
      exact: true,
    });
    await expect(list.getByRole("listitem")).toHaveCount(
      first.revisions.length,
    );
    expect(await linkPaths(list)).toEqual(
      first.revisions.map((r) => revisionPath(r.scope)),
    );
    const second = await capture<RevisionPage>(
      page,
      endpoint,
      () =>
        page
          .getByRole("navigation", { name: "Revision pages" })
          .getByRole("link", { name: "Next page" })
          .click(),
      nextPageQuery(first.nextCursor),
    );
    expect(second.revisions).toEqual(
      fixture.history.sequenceDescending.slice(10),
    );
    expect(new URL(page.url()).searchParams.get("cursor")).toBe(
      first.nextCursor,
    );
    const saved = await capture<Revision>(
      page,
      catalogApi(revisionPath(original.scope)),
      () =>
        list
          .getByRole("link", {
            name: `Revision ${original.sequence}`,
            exact: true,
          })
          .click(),
    );
    expect(saved).toEqual(original);
    await expectRevision(page, original, mappingName, 2);
  });

  await test.step("ordinary HTTP append advances fresh head while saved original revision remains immutable", async () => {
    const overview = await page.context().newPage();
    try {
      await overview.goto(mappingPath(mapping));
      const ack = await catalogOwner.control("append-primary-revision");
      expect(ack.action).toBe("append-primary-revision");
      if (
        ack.action !== "append-primary-revision" ||
        ack.outcome !== "appended"
      )
        throw new Error("Expected actual committed append acknowledgement");
      expect(ack.appendedRevision.scope.revisionId).not.toBe(
        original.scope.revisionId,
      );
      const fresh = overview.waitForResponse(
        (r) =>
          new URL(r.url()).pathname === catalogApi(mappingPath(mapping)) &&
          r.request().method() === "GET",
      );
      await overview
        .getByRole("button", { name: "View current head", exact: true })
        .click();
      expect((await fresh).status()).toBe(200);
      await expectRevision(overview, ack.appendedRevision, mappingName);
      const saved = await capture<Revision>(
        page,
        catalogApi(revisionPath(original.scope)),
        () => page.reload(),
      );
      expect(saved).toEqual(original);
      await expectRevision(page, original, mappingName, 2);
    } finally {
      await overview.close();
    }
  });

  await test.step("actual API outage, unavailable reader, API restart and same-volume PostgreSQL restart preserve immutable reads", async () => {
    await catalogOwner.control("api-stop");
    await page
      .getByRole("button", { name: "Refresh catalog", exact: true })
      .click();
    // A stopped host cannot confirm session status, so the shell withholds the workspace.
    const workspaceAccess = page.getByRole("alert").filter({
      has: page.getByRole("heading", {
        name: "Workspace access unavailable",
        exact: true,
      }),
    });
    await expect(workspaceAccess).toBeVisible();
    await expect(metadataValue(page, "Revision ID")).toHaveCount(0);
    await catalogOwner.control("api-start", "viewer");
    const restoredAdmission = page.waitForResponse(
      (response) => new URL(response.url()).pathname === contextPath,
    );
    await workspaceAccess
      .getByRole("button", { name: "Retry", exact: true })
      .click();
    expect((await restoredAdmission).status()).toBe(200);
    // Workspace Retry restores status and context only; the revision read stays explicit.
    await expect(
      page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
    ).toBeVisible();
    const apiRestarted = await capture<Revision>(
      page,
      catalogApi(revisionPath(original.scope)),
      () => page.getByRole("button", { name: "Retry", exact: true }).click(),
    );
    expect(apiRestarted).toEqual(original);
    await expectRevision(page, original, mappingName, 2);
    await catalogOwner.control("restart-profile", "readerUnavailable");
    const unavailable = page.waitForResponse(
      (response) => new URL(response.url()).pathname === contextPath,
    );
    await page
      .getByRole("button", { name: "Refresh catalog", exact: true })
      .click();
    expect((await unavailable).status()).toBe(500);
    await expect(
      page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
    ).toBeVisible();
    await expect(page.getByText("No projects", { exact: true })).toHaveCount(0);
    await catalogOwner.control("api-stop");
    await catalogOwner.control("pg-normal-restart");
    await catalogOwner.control("api-start", "viewer");
    const pgRestarted = await capture<Revision>(
      page,
      catalogApi(revisionPath(original.scope)),
      () => page.reload(),
    );
    expect(pgRestarted).toEqual(original);
    await expectRevision(page, original, mappingName, 2);
  });

  await test.step("one-way owner-authored project and organization revocations clear prior content on the next actual read", async () => {
    await catalogOwner.control("revoke-project");
    await page
      .getByRole("button", { name: "Refresh catalog", exact: true })
      .click();
    await expect(
      page.getByRole("heading", { name: "Not found", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText(original.nativeDigest.value, { exact: false }),
    ).toHaveCount(0);
    await catalogOwner.control("revoke-org");
    const beforeOrganizationDenial = scopedReads.length;
    const revokedAdmission = page.waitForResponse(
      (response) => new URL(response.url()).pathname === contextPath,
    );
    await page
      .getByRole("button", { name: "Refresh catalog", exact: true })
      .click();
    const revoked = await revokedAdmission;
    expect(revoked.status()).toBe(404);
    await revoked.finished();
    await expect(
      page.getByRole("heading", { name: "Not found", exact: true }),
    ).toBeVisible();
    await expect(page.locator('[aria-label="Version history"]')).toHaveCount(0);
    expect(scopedReads.slice(beforeOrganizationDenial)).toEqual([]);
    expect(forbiddenReads).toEqual([]);
  });
});
