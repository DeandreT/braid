import { expect, test, type Page, type Route } from "@playwright/test";
import { ControlledReports } from "./revision-reports-controlled.fixture";
import type { HelpSection, HelpSectionList } from "../src/help/help-api";

// Deliberately synthetic guide content: these checks qualify the page, not the server's guide.
const contents: HelpSectionList = {
  sections: [
    {
      id: "overview",
      title: "Getting started",
      summary: "What braid does and where to begin.",
    },
    {
      id: "reports",
      title: "Reports, coverage and diagnostics",
      summary: "Read a revision's explanation and validation.",
    },
    {
      id: "chat",
      title: "Chat about a revision",
      summary: "Ask questions about a revision.",
    },
  ],
};

const text = (value: string) => ({ kind: "text" as const, text: value });

const sections: Record<string, HelpSection> = {
  overview: {
    id: "overview",
    title: "Getting started",
    summary: "What braid does and where to begin.",
    blocks: [
      {
        kind: "paragraph",
        content: [
          text("braid keeps "),
          { kind: "strong", text: "every revision" },
          text(" and shows its "),
          { kind: "emphasis", text: "explanation" },
          text(". Run "),
          { kind: "code", text: "example()" },
          text(" or read about "),
          {
            kind: "link",
            text: "coverage",
            sectionId: "reports",
            anchor: "coverage",
          },
          text("."),
        ],
      },
      { kind: "heading", level: 2, anchor: "words", text: "Words braid uses" },
      {
        kind: "list",
        ordered: false,
        items: [
          {
            content: [{ kind: "strong", text: "Project" }, text(": a group.")],
          },
          { content: [text("Revision: one uploaded file.")] },
        ],
      },
      { kind: "heading", level: 2, anchor: "start", text: "Where to start" },
      {
        kind: "list",
        ordered: true,
        items: [
          { content: [text("Sign in.")] },
          { content: [text("Open the catalog.")] },
        ],
      },
      { kind: "heading", level: 3, anchor: "roles", text: "Roles" },
      {
        kind: "table",
        header: [{ content: [text("Role")] }, { content: [text("Allows")] }],
        rows: [
          {
            cells: [
              { content: [text("Viewer")] },
              { content: [text("Browse | read")] },
            ],
          },
        ],
      },
      { kind: "heading", level: 2, anchor: "commands", text: "Commands" },
      { kind: "codeBlock", text: "example()\n  indented" },
    ],
  },
  reports: {
    id: "reports",
    title: "Reports, coverage and diagnostics",
    summary: "Read a revision's explanation and validation.",
    blocks: [
      { kind: "paragraph", content: [text("Reports belong to one revision.")] },
      ...Array.from({ length: 12 }, () => ({
        kind: "paragraph" as const,
        content: [text("Filler paragraph that makes the page scroll.")],
      })),
      { kind: "heading", level: 2, anchor: "coverage", text: "Coverage" },
      {
        kind: "paragraph",
        content: [
          text("Coverage says how much is explained. Back to "),
          {
            kind: "link",
            text: "the start",
            sectionId: "overview",
            anchor: null,
          },
          text("."),
        ],
      },
      ...Array.from({ length: 12 }, () => ({
        kind: "paragraph" as const,
        content: [text("More filler after the heading.")],
      })),
    ],
  },
  chat: {
    id: "chat",
    title: "Chat about a revision",
    summary: "Ask questions about a revision.",
    blocks: [
      {
        kind: "paragraph",
        content: [
          text(
            '<script>window.helpInjected = true</script><img src=x onerror="window.helpInjected = true">',
          ),
          { kind: "strong", text: "<b>not bold markup</b>" },
        ],
      },
    ],
  },
};

interface Fixture {
  requests: string[];
  failList: { status: number; code: string } | null;
  failSection: { status: number; code: string } | null;
  sectionOverride: unknown;
}

async function install(page: Page): Promise<Fixture> {
  const fixture: Fixture = {
    requests: [],
    failList: null,
    failSection: null,
    sectionOverride: undefined,
  };
  await page.route("**/api/**", async (route: Route) => {
    const request = route.request(),
      url = new URL(request.url());
    if (url.pathname === "/api/auth/session") {
      await route.fulfill({ json: { version: 1, mode: "development" } });
      return;
    }
    if (url.pathname === "/api/v1/health") {
      expect(request.method()).toBe("GET");
      await route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      });
      return;
    }
    fixture.requests.push(`${request.method()} ${url.pathname}${url.search}`);
    expect(request.method()).toBe("GET");
    expect(request.headers()["x-xsrf-token"]).toBeUndefined();
    if (url.pathname === "/api/v1/help/sections") {
      if (fixture.failList) {
        const failure = fixture.failList;
        await route.fulfill({
          status: failure.status,
          json: { code: failure.code },
        });
        return;
      }
      await route.fulfill({
        json: contents,
        headers: { "cache-control": "no-store" },
      });
      return;
    }
    const match = /^\/api\/v1\/help\/sections\/([^/]+)$/.exec(url.pathname);
    if (match) {
      if (fixture.failSection) {
        await route.fulfill({
          status: fixture.failSection.status,
          json: { code: fixture.failSection.code },
        });
        return;
      }
      if (fixture.sectionOverride !== undefined) {
        await route.fulfill({ json: fixture.sectionOverride });
        return;
      }
      const section = sections[match[1]!];
      await route.fulfill(
        section
          ? { json: section }
          : { status: 404, json: { code: "notFound" } },
      );
      return;
    }
    await route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  return fixture;
}

const article = (page: Page) => page.locator("article.help-article");
const contentsNav = (page: Page) =>
  page.getByRole("navigation", { name: "User guide" });

test("the shell uses lowercase braid branding without changing technical API names", async ({
  page,
}) => {
  await install(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/help");
  const home = page.getByRole("link", { name: "braid home", exact: true });
  await expect(home).toBeVisible();
  await expect(home.locator(".brand-name")).toHaveText("braid.");
  await expect(home.locator(".brand-mark")).toHaveText("b");
  await expect(home.locator(".brand-mark")).toHaveAttribute(
    "aria-hidden",
    "true",
  );
  await expect(page).toHaveTitle("Help · braid");
  await expect(page.locator('meta[name="description"]')).toHaveAttribute(
    "content",
    "Understand, test, and deploy your data mappings with braid.",
  );
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
});

test("the sign-in wordmark, monogram and title use lowercase braid", async ({
  page,
}) => {
  await install(page);
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: {
        version: 1,
        mode: "session",
        state: "anonymous",
        authActionRequestToken: "YXV0aC1h",
        requestToken: null,
        identityContext: null,
        displayName: null,
      },
    }),
  );
  await page.goto("/sign-in");
  await expect(
    page.getByRole("heading", { name: "Sign in to braid", exact: true }),
  ).toBeVisible();
  await expect(page.locator(".sign-in-brand .brand-name")).toHaveText("braid.");
  await expect(page.locator(".sign-in-brand .brand-mark")).toHaveText("b");
  await expect(page).toHaveTitle("Sign in · braid");
});

test("the header opens the guide contents", async ({ page }) => {
  await install(page);
  await page.goto("/catalog");
  await page.getByRole("link", { name: "Help & guide", exact: true }).click();
  await expect(page).toHaveURL(/\/help$/);
  await expect(page).toHaveTitle("Help · braid");
  await expect(
    page.getByRole("heading", { level: 1, name: "User guide" }),
  ).toBeVisible();
  const index = page.getByRole("list", { name: "Guide sections" });
  await expect(index.getByRole("listitem")).toHaveCount(3);
  await expect(index).toContainText("What braid does and where to begin.");
  await expect(
    page.getByRole("link", { name: "Help & guide", exact: true }),
  ).toHaveAttribute("aria-current", "page");
  await index.getByRole("link", { name: "Chat about a revision" }).click();
  await expect(page).toHaveURL(/\/help\/chat$/);
});

test("a nested deep link loads its entry assets from the root", async ({
  page,
}) => {
  await install(page);
  const assets: string[] = [];
  page.on("response", (response) => {
    if (["script", "stylesheet"].includes(response.request().resourceType()))
      assets.push(`${response.status()} ${new URL(response.url()).pathname}`);
  });
  await page.goto("/help/overview");
  await expect(
    article(page).getByRole("heading", { level: 1, name: "Getting started" }),
  ).toBeVisible();
  const mainEntries = await page
    .locator("script[src]")
    .evaluateAll((scripts) =>
      scripts
        .map((script) => script.getAttribute("src") ?? "")
        .filter((src) => /^\/main(?:-[A-Za-z0-9_-]+)?\.js$/.test(src)),
    );
  expect(mainEntries).toHaveLength(1);
  expect(assets).toContain(`200 ${mainEntries[0]}`);
  expect(assets.filter((asset) => !/^200 \/(?!help\/)/.test(asset))).toEqual(
    [],
  );
});

test("a section renders its structure as text and links", async ({ page }) => {
  const fixture = await install(page);
  await page.goto("/help/overview");
  await expect(page).toHaveTitle("Getting started · Help · braid");
  const body = article(page);
  await expect(
    body.getByRole("heading", { level: 1, name: "Getting started" }),
  ).toBeVisible();
  await expect(body).toContainText("What braid does and where to begin.");
  await expect(
    body.locator("strong", { hasText: "every revision" }),
  ).toBeVisible();
  await expect(body.locator("em", { hasText: "explanation" })).toBeVisible();
  await expect(body.locator("p code", { hasText: "example()" })).toBeVisible();
  await expect(body.locator(".help-body p").first()).toHaveText(
    "braid keeps every revision and shows its explanation. Run example() or read about coverage.",
  );
  await expect(
    body.getByRole("heading", { level: 2, name: "Words braid uses" }),
  ).toHaveAttribute("id", "help-words");
  await expect(
    body.getByRole("heading", { level: 3, name: "Roles" }),
  ).toBeVisible();
  await expect(body.locator("ul li")).toHaveCount(5);
  await expect(body.locator(".help-body ol li")).toHaveCount(2);
  await expect(
    body.getByRole("columnheader", { name: "Allows" }),
  ).toBeVisible();
  await expect(body.getByRole("cell", { name: "Browse | read" })).toBeVisible();
  await expect(body.locator("pre code")).toHaveText("example()\n  indented");
  const outline = body.getByRole("navigation", { name: "On this page" });
  await expect(outline.getByRole("link")).toHaveText([
    "Words braid uses",
    "Where to start",
    "Commands",
  ]);
  await expect(
    contentsNav(page).getByRole("link", { name: "Getting started" }),
  ).toHaveAttribute("aria-current", "page");
  const pager = body.getByRole("navigation", { name: "Guide sections" });
  await expect(pager.getByRole("link")).toHaveCount(1);
  await expect(pager.getByRole("link")).toContainText(
    "Reports, coverage and diagnostics",
  );
  expect(fixture.requests).toEqual([
    "GET /api/v1/help/sections",
    "GET /api/v1/help/sections/overview",
  ]);
});

test("links between sections open the anchored heading", async ({ page }) => {
  await install(page);
  await page.goto("/help/overview");
  await article(page)
    .getByRole("link", { name: "coverage", exact: true })
    .click();
  await expect(page).toHaveURL(/\/help\/reports#coverage$/);
  const heading = article(page).getByRole("heading", {
    level: 2,
    name: "Coverage",
  });
  await expect(heading).toBeFocused();
  await expect(heading).toBeInViewport();
  await article(page).getByRole("link", { name: "the start" }).click();
  await expect(page).toHaveURL(/\/help\/overview$/);
  await expect(
    article(page).getByRole("heading", { level: 1, name: "Getting started" }),
  ).toBeVisible();
  await article(page)
    .getByRole("navigation", { name: "On this page" })
    .getByRole("link", { name: "Commands" })
    .click();
  await expect(page).toHaveURL(/\/help\/overview#commands$/);
  await expect(
    article(page).getByRole("heading", { level: 2, name: "Commands" }),
  ).toBeFocused();
  await expect(page).toHaveTitle("Getting started · Help · braid");
});

test("guide text is never interpreted as markup", async ({ page }) => {
  await install(page);
  await page.goto("/help/chat");
  const body = article(page);
  await expect(body.locator(".help-body p")).toContainText(
    "<script>window.helpInjected = true</script>",
  );
  await expect(body.locator(".help-body strong")).toHaveText(
    "<b>not bold markup</b>",
  );
  await expect(body.locator("script, img, b")).toHaveCount(0);
  expect(
    await page.evaluate(
      () => (window as { helpInjected?: boolean }).helpInjected,
    ),
  ).toBeUndefined();
});

test("a response outside the contract is not rendered", async ({ page }) => {
  const fixture = await install(page);
  fixture.sectionOverride = {
    ...sections["overview"],
    blocks: [{ kind: "html", html: "<em>raw</em>" }],
  };
  await page.goto("/help/overview");
  await expect(article(page)).toContainText(
    "The user guide couldn't be loaded.",
  );
  await expect(article(page).locator("em")).toHaveCount(0);
  fixture.sectionOverride = {
    ...sections["overview"],
    blocks: [
      {
        kind: "paragraph",
        content: [
          {
            kind: "link",
            text: "outside",
            sectionId: "javascript:alert(1)",
            anchor: null,
          },
        ],
      },
    ],
  };
  await article(page).getByRole("button", { name: "Try again" }).click();
  await expect.poll(() => fixture.requests.length).toBe(4);
  await expect(article(page)).toContainText(
    "The user guide couldn't be loaded.",
  );
  fixture.sectionOverride = undefined;
  await article(page).getByRole("button", { name: "Try again" }).click();
  await expect(
    article(page).getByRole("heading", { level: 1, name: "Getting started" }),
  ).toBeVisible();
});

test("unknown and malformed sections keep the contents", async ({ page }) => {
  const fixture = await install(page);
  await page.goto("/help/missing-section");
  await expect(article(page)).toContainText("This help page doesn't exist.");
  await expect(contentsNav(page).getByRole("link")).toHaveCount(4);
  await page.goto("/help/Not_A_Section");
  await expect(article(page)).toContainText("This help page doesn't exist.");
  expect(fixture.requests).not.toContain(
    "GET /api/v1/help/sections/Not_A_Section",
  );
  await article(page)
    .getByRole("link", { name: "Open the user guide" })
    .click();
  await expect(page).toHaveURL(/\/help$/);
});

test("access and availability failures are distinct", async ({ page }) => {
  const fixture = await install(page);
  fixture.failList = { status: 401, code: "identityRejected" };
  await page.goto("/help");
  await expect(article(page)).toContainText("Sign in to read the user guide.");
  fixture.failList = { status: 404, code: "unavailable" };
  await page.goto("/help/overview");
  await expect(article(page)).toContainText(
    "The user guide isn't available in this workspace.",
  );
  fixture.failList = { status: 503, code: "unavailable" };
  await page.goto("/help");
  await expect(article(page)).toContainText(
    "The user guide couldn't be loaded.",
  );
  fixture.failList = null;
  await article(page).getByRole("button", { name: "Try again" }).click();
  await expect(
    article(page).getByRole("heading", { level: 1, name: "User guide" }),
  ).toBeVisible();
});

test("the guide fits a narrow screen", async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 720 });
  await install(page);
  await page.goto("/help/overview");
  await expect(
    article(page).getByRole("heading", { level: 1, name: "Getting started" }),
  ).toBeVisible();
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - window.innerWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
  // On a phone the section comes first and the contents follow it.
  const nav = await contentsNav(page).boundingBox();
  const pager = await article(page)
    .getByRole("navigation", { name: "Guide sections" })
    .boundingBox();
  expect(pager!.y + pager!.height).toBeLessThanOrEqual(nav!.y);
  await expect(
    contentsNav(page).getByRole("link", { name: "Chat about a revision" }),
  ).toBeVisible();
});

test("going back restores the reading position instead of the anchor", async ({
  page,
}) => {
  await install(page);
  await page.goto("/help/reports#coverage");
  await expect(
    article(page).getByRole("heading", { level: 2, name: "Coverage" }),
  ).toBeFocused();
  // Read from the top of the section, well away from the anchored heading.
  await page
    .locator("#main-content")
    .evaluate((element) => element.scrollTo(0, 0));
  await expect
    .poll(() =>
      page.locator("#main-content").evaluate((element) => element.scrollTop),
    )
    .toBe(0);
  const anchor = await article(page)
    .getByRole("heading", { level: 2, name: "Coverage" })
    .evaluate(
      (element) =>
        element.getBoundingClientRect().top +
        document.getElementById("main-content")!.scrollTop,
    );
  expect(anchor).toBeGreaterThan(300);
  await contentsNav(page)
    .getByRole("link", { name: "Getting started" })
    .click();
  await expect(page).toHaveURL(/\/help\/overview$/);
  await page.goBack();
  await expect(page).toHaveURL(/\/help\/reports#coverage$/);
  await expect(page).toHaveTitle(
    "Reports, coverage and diagnostics · Help · braid",
  );
  await expect(
    article(page).getByRole("heading", { level: 2, name: "Coverage" }),
  ).toBeAttached();
  await page.waitForTimeout(500);
  expect(
    await page
      .locator("#main-content")
      .evaluate((element) => element.scrollTop),
  ).toBeLessThan(anchor / 2);
});

test("content at the server's limits renders and beyond them is refused", async ({
  page,
}) => {
  const fixture = await install(page);
  const longest = "a".repeat(8192);
  fixture.sectionOverride = {
    ...sections["overview"],
    blocks: [
      { kind: "paragraph", content: [text(longest)] },
      {
        kind: "list",
        ordered: false,
        items: Array.from({ length: 256 }, (_, index) => ({
          content: [text(`Item ${index + 1}`)],
        })),
      },
    ],
  };
  await page.goto("/help/overview");
  await expect(article(page).locator(".help-body li")).toHaveCount(256);
  await expect(article(page).locator(".help-body p").first()).toHaveText(
    longest,
  );
  fixture.sectionOverride = {
    ...sections["overview"],
    blocks: [{ kind: "paragraph", content: [text(longest + "a")] }],
  };
  await page.goto("/help/overview");
  await expect(article(page)).toContainText(
    "The user guide couldn't be loaded.",
  );
});

test("a contextual help link opens its section in a new tab", async ({
  page,
  context,
}) => {
  process.env.BRAID_REPORTS_BASE_URL = new URL(
    test.info().project.use.baseURL!,
  ).origin;
  const reports = new ControlledReports(page);
  await reports.install();
  await page.goto(reports.data.browserPath);
  await expect(
    page.getByRole("heading", { level: 1, name: reports.data.mapping.name }),
  ).toBeVisible();
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
  const link = page.getByRole("link", {
    name: "About reports (opens in a new tab)",
  });
  await expect(link).toHaveAttribute("href", "/help/reports");
  await expect(link).toHaveAttribute("target", "_blank");
  const [help] = await Promise.all([
    context.waitForEvent("page"),
    link.click(),
  ]);
  await expect(help).toHaveURL(/\/help\/reports$/);
  await expect(
    page.getByRole("heading", { level: 1, name: reports.data.mapping.name }),
  ).toBeVisible();
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
  await help.close();
});
