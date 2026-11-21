import {
  catalogApi,
  expect,
  expectRevision,
  mappingPath,
  metadataValue,
  projectPath,
  projectsPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
  test,
} from "./catalog.fixture";
import type { Request } from "@playwright/test";
import {
  organizationContextPath,
  routeOrganizationContext,
} from "./organization-context.fixture";

test("controlled boundary: malformed and foreign organization links dispatch no scoped resource request", async ({
  page,
}) => {
  await syntheticCatalog(page);
  const foreign = "10000000-0000-4000-8000-000000000099";
  const requests: { path: string; method: string }[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/v1/organizations/"))
      requests.push({ path, method: request.method() });
  });
  for (const [path, heading] of [
    ["/catalog/organizations/not-a-uuid/projects", "Invalid link"],
    [
      `/catalog/organizations/${synthetic.org}/projects/00000000-0000-0000-0000-000000000000`,
      "Invalid link",
    ],
    [`/catalog/organizations/${foreign}/projects`, "Not found"],
    [projectsPath(synthetic.org) + "?cursor=A", "Invalid link"],
    [projectsPath(synthetic.org) + "?cursor=YR", "Invalid link"],
    [
      projectsPath(synthetic.org) + "?cursor=" + "A".repeat(684),
      "Invalid link",
    ],
    [projectsPath(synthetic.org) + "?cursor=YQ&cursor=Yg", "Invalid link"],
  ]) {
    await page.goto(path);
    await expect(
      page.getByRole("heading", { name: heading, exact: true }),
    ).toBeVisible();
    await expect(page.getByText("No projects", { exact: true })).toHaveCount(0);
  }
  // A well-formed foreign link can query its own admission, but denial
  // must stop all project and mapping reads. Malformed links do neither.
  expect(requests).toEqual([
    { path: organizationContextPath(foreign), method: "GET" },
  ]);
});

for (const kind of ["project", "mapping", "revision", "page"] as const) {
  test(`controlled boundary: incompatible foreign-shaped ${kind}200 is unavailable, never empty`, async ({
    page,
  }) => {
    const foreignProject = {
      ...synthetic.project,
      scope: {
        ...synthetic.project.scope,
        projectId: "20000000-0000-4000-8000-000000000099",
      },
    };
    const foreignMapping = {
      ...synthetic.mapping,
      scope: {
        ...synthetic.mapping.scope,
        projectId: foreignProject.scope.projectId,
      },
    };
    const foreignRevision = {
      ...synthetic.revision,
      scope: {
        ...synthetic.revision.scope,
        mappingId: "30000000-0000-4000-8000-000000000099",
      },
    };
    const route =
      kind === "project"
        ? projectPath(synthetic.project.scope)
        : kind === "mapping"
          ? mappingPath(synthetic.mapping.scope)
          : kind === "revision"
            ? revisionPath(synthetic.revision.scope)
            : projectsPath(synthetic.org);
    const endpoint = catalogApi(route);
    await syntheticCatalog(page, {
      [endpoint]:
        kind === "project"
          ? foreignProject
          : kind === "mapping"
            ? foreignMapping
            : kind === "revision"
              ? foreignRevision
              : {
                  projects: [
                    {
                      ...foreignProject,
                      scope: {
                        ...foreignProject.scope,
                        organizationId: "10000000-0000-4000-8000-000000000099",
                      },
                    },
                  ],
                  nextCursor: null,
                },
    });
    await page.goto(route);
    await expect(
      page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
    ).toBeVisible();
    await expect(page.getByText("No projects", { exact: true })).toHaveCount(0);
    await expect(metadataValue(page, "Revision ID")).toHaveCount(0);
  });
}

test("controlled boundary: maximum decimal strings preserve every digit and context token is never stored", async ({
  page,
}) => {
  const max = "9223372036854775807";
  const revision = { ...synthetic.revision, sequence: max };
  await syntheticCatalog(page, {
    [catalogApi(revisionPath(revision.scope))]: revision,
    [catalogApi(mappingPath(synthetic.mapping.scope))]: {
      ...synthetic.mapping,
      version: max,
      head: { ...synthetic.mapping.head, sequence: max },
    },
  });
  await page.goto(revisionPath(revision.scope));
  await expectRevision(page, revision, synthetic.mapping.name);
  const stored = await page.evaluate(() =>
    [localStorage, sessionStorage].some((storage) =>
      Object.values(storage).some(
        (value: unknown) =>
          typeof value === "string" &&
          value.includes("synthetic-ui-only-not-a-credential"),
      ),
    ),
  );
  expect(stored).toBe(false);
});

for (const [status, code, heading] of [
  [403, "identityRejected", "Access rejected"],
  [404, "notFound", "Not found"],
  [500, "persistenceFailure", "Catalog unavailable"],
] as const) {
  test(`controlled boundary: context${status} is ${heading} rather than accessible empty`, async ({
    page,
  }) => {
    await syntheticCatalog(page);
    await routeOrganizationContext(page, (route) =>
      route.fulfill({ status, json: { code } }),
    );
    await page.goto(projectsPath(synthetic.org));
    await expect(
      page.getByRole("heading", { name: heading, exact: true }),
    ).toBeVisible();
    await expect(page.getByText("No projects", { exact: true })).toHaveCount(0);
    await expect(
      page.getByRole("list", { name: "Projects", exact: true }),
    ).toHaveCount(0);
  });
}

test("controlled freshness: focus and visibility coalesce into one fresh context and clear obsolete authority", async ({
  page,
}) => {
  await syntheticCatalog(page);
  let contextRequests = 0;
  let admitted = true;
  await routeOrganizationContext(page, async (route) => {
    contextRequests++;
    await route.fulfill(
      admitted
        ? { json: synthetic.context }
        : { status: 403, json: { code: "identityRejected" } },
    );
  });
  await page.goto(projectsPath(synthetic.org));
  await expect(
    page.getByRole("list", { name: "Projects", exact: true }),
  ).toBeVisible();
  const initial = contextRequests;
  admitted = false;
  await page.evaluate(() => {
    window.dispatchEvent(new Event("focus"));
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(
    page.getByRole("heading", { name: "Access rejected", exact: true }),
  ).toBeVisible();
  expect(contextRequests).toBe(initial + 1);
  await expect(
    page.getByRole("list", { name: "Projects", exact: true }),
  ).toHaveCount(0);
});

for (const kind of ["project", "mapping", "revision", "page"] as const) {
  test(`controlled cancellation: obsolete ${kind} request is aborted and late data cannot replace a newer scope`, async ({
    page,
  }) => {
    const other = {
      ...synthetic.project,
      scope: {
        ...synthetic.project.scope,
        projectId: "20000000-0000-4000-8000-000000000002",
      },
      name: "Synthetic newer project",
    };
    await syntheticCatalog(page, {
      [catalogApi(projectsPath(synthetic.org))]: {
        projects: [synthetic.project, other],
        nextCursor: null,
      },
      [catalogApi(projectPath(other.scope))]: other,
      [catalogApi(projectPath(other.scope)) + "/mappings"]: {
        mappings: [],
        nextCursor: null,
      },
    });
    let release!: () => void;
    let finished!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const settled = new Promise<void>((resolve) => {
      finished = resolve;
    });
    const endpoint =
      kind === "project"
        ? catalogApi(projectPath(synthetic.project.scope))
        : kind === "mapping"
          ? catalogApi(mappingPath(synthetic.mapping.scope))
          : kind === "revision"
            ? catalogApi(revisionPath(synthetic.revision.scope))
            : catalogApi(projectsPath(synthetic.org));
    let armed = false;
    let cancelled = false;
    let delayedRequest: Request | undefined;
    page.on("requestfailed", (request) => {
      if (request === delayedRequest) cancelled = true;
    });
    await page.route(`**${endpoint}*`, async (route) => {
      if (!armed || new URL(route.request().url()).pathname !== endpoint) {
        await route.fallback();
        return;
      }
      armed = false;
      delayedRequest = route.request();
      try {
        await gate;
        const json =
          kind === "project"
            ? { ...synthetic.project, name: "Synthetic obsolete project" }
            : kind === "mapping"
              ? { ...synthetic.mapping, name: "Synthetic obsolete mapping" }
              : kind === "revision"
                ? {
                    ...synthetic.revision,
                    originalFilename: "Synthetic obsolete revision.json",
                  }
                : {
                    projects: [
                      { ...synthetic.project, name: "Synthetic obsolete page" },
                    ],
                    nextCursor: null,
                  };
        await route.fulfill({ json });
      } catch {
        /* A cancelled routed request cannot deliver its held body. */
      } finally {
        finished();
      }
    });
    if (kind === "project") {
      await page.goto(projectsPath(synthetic.org));
      armed = true;
      await page
        .getByRole("list", { name: "Projects", exact: true })
        .getByRole("link", { name: synthetic.project.name, exact: true })
        .click();
    } else if (kind === "mapping") {
      await page.goto(projectPath(synthetic.project.scope));
      armed = true;
      await page
        .getByRole("list", { name: "Mappings", exact: true })
        .getByRole("link", { name: synthetic.mapping.name, exact: true })
        .click();
    } else if (kind === "revision") {
      await page.goto(mappingPath(synthetic.mapping.scope));
      armed = true;
      await page
        .getByRole("list", { name: "Revision history", exact: true })
        .getByRole("link", { name: "Revision 1", exact: true })
        .click();
    } else {
      await page.goto(projectsPath(synthetic.org));
      // The initial page read must settle so the hold captures only the refresh read.
      await expect(
        page
          .getByRole("list", { name: "Projects", exact: true })
          .getByRole("link", { name: synthetic.project.name, exact: true }),
      ).toBeVisible();
      armed = true;
      await page
        .getByRole("button", { name: "Refresh catalog", exact: true })
        .click();
    }
    try {
      await expect.poll(() => Boolean(delayedRequest)).toBe(true);
      await expect(page.getByRole("heading", { level: 1 })).not.toHaveText(
        synthetic.project.name,
      );
      await expect(metadataValue(page, "Revision ID")).toHaveCount(0);
      await page.getByRole("link", { name: "Catalog", exact: true }).click();
      await page
        .getByRole("list", { name: "Projects", exact: true })
        .getByRole("link", { name: other.name, exact: true })
        .click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(
        other.name,
      );
      release();
      await settled;
      await expect(page.getByText(/Synthetic obsolete/)).toHaveCount(0);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(
        other.name,
      );
      await expect.poll(() => cancelled).toBe(true);
    } finally {
      release();
      if (delayedRequest) await settled;
    }
  });
}
