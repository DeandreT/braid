import { test, expect, type Page } from "@playwright/test";
import { controlledData, P } from "./revision-reports-controlled.fixture";
import { catalogApi, projectPath, mappingPath } from "./catalog.fixture";
import type { SessionAuthStatus } from "../src/auth/browser-session-types";

const identity = `v1.${"A".repeat(43)}`;
const provider =
  "https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111/oauth2/v2.0/authorize?state=synthetic-return-only";
function status(authenticated: boolean): SessionAuthStatus {
  const common = {
    version: 1 as const,
    mode: "session" as const,
    authActionRequestToken: "YXV0aC1h",
  };
  return authenticated
    ? {
        ...common,
        state: "authenticated",
        requestToken: "ZXBvY2gtYQ",
        identityContext: identity,
        displayName: "Return user",
      }
    : {
        ...common,
        state: "anonymous",
        requestToken: null,
        identityContext: null,
        displayName: null,
      };
}

async function fixture(page: Page, initiallyAuthenticated: boolean) {
  const data = controlledData();
  const document = data.projection.report!.document!;
  document.fields[0]!.id = "field/café:🧵";
  document.rules[0]!.id = "rule/café:🧵";
  document.rules[0]!.outputFieldId = document.fields[0]!.id;
  let authenticated = initiallyAuthenticated;
  let returnTo = "";
  let logins = 0;
  const searches: URL[] = [];
  const retained: string[] = [];
  const context = {
    ...data.context,
    mode: "session",
    identityContext: identity,
    requestToken: "Y2F0YWxvZy1vbmx5",
  };
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (path === "/api/auth/session")
      return route.fulfill({ json: status(authenticated) });
    if (path === "/api/auth/login") {
      ++logins;
      expect(request.method()).toBe("POST");
      expect(request.postData()).toBeNull();
      returnTo = request.headers()["x-weft-return-to"] ?? "";
      return route.fulfill({
        json: { version: 1, authorizationUrl: provider },
      });
    }
    if (path === "/api/v1/health")
      return route.fulfill({
        json: { status: "ok", service: "weft-api", apiVersion: "v1" },
      });
    if (path === "/api/v1/catalog/context")
      return route.fulfill({ json: context });
    if (path === "/api/v1/catalog/interchange")
      return route.fulfill({ json: { enabled: false } });
    if (path.endsWith("/projects"))
      return route.fulfill({ json: { projects: [], nextCursor: null } });
    if (path.endsWith("/search")) {
      searches.push(url);
      return route.fulfill({
        json: {
          query: url.searchParams.get("q"),
          nextCursor: null,
          hits: [
            {
              kind: "field",
              ...data.revision.scope,
              projectionDerivationId: P,
              resourceId: document.fields[0]!.id,
              title: { text: "Saved return field", highlights: [] },
              description: { text: "Retained report", highlights: [] },
            },
          ],
        },
      });
    }
    if (path === catalogApi(projectPath(data.project.scope)))
      return route.fulfill({ json: data.project });
    if (path === catalogApi(mappingPath(data.mapping.scope)))
      return route.fulfill({ json: data.mapping });
    if (path === data.apiPath) return route.fulfill({ json: data.revision });
    if (path.startsWith(data.reportPath("projection"))) {
      retained.push(path);
      return route.fulfill({ json: data.projection });
    }
    if (path.startsWith(data.reportPath("validation")))
      return route.fulfill({
        json: {
          ...data.validation,
          projectionDerivationId: url.searchParams.get(
            "projectionDerivationId",
          ),
        },
      });
    return route.fulfill({ status: 404 });
  });
  await page.route(provider, async (route) => {
    expect(route.request().isNavigationRequest()).toBe(true);
    authenticated = true;
    await route.fulfill({
      status: 302,
      headers: { Location: new URL(returnTo, "http://127.0.0.1:5184").href },
    });
  });
  return {
    data,
    searches,
    retained,
    returnTo: () => returnTo,
    logins: () => logins,
    anonymous: () => {
      authenticated = false;
    },
  };
}

test("new return flow rejects duplicate or unknown sign-in query keys", async ({
  page,
}, info) => {
  const source = await fixture(page, false);
  const good = encodeURIComponent("/catalog/search?q=Order");
  for (const [index, query] of [
    `returnTo=${good}&returnTo=${encodeURIComponent("//evil.example")}`,
    `returnTo=${good}&next=${encodeURIComponent("https://evil.example")}`,
  ].entries()) {
    source.anonymous();
    await page.goto(`/sign-in?${query}`);
    await expect(
      page.getByRole("heading", { name: "Sign in to Weft", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Sign in with Microsoft", exact: true }),
    ).toBeEnabled();
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    await page.screenshot({
      path: info.outputPath(`unsafe-return-${index}-before.png`),
      fullPage: true,
    });
    await page
      .getByRole("button", { name: "Sign in with Microsoft", exact: true })
      .click();
    await expect.poll(() => source.logins()).toBe(index + 1);
    expect(source.returnTo()).toBe("/catalog");
    await expect(page).toHaveURL(/\/catalog(?:\/|$)/);
    await expect(
      page.getByRole("heading", { name: "Projects", exact: true }),
    ).toBeVisible();
    await page.screenshot({
      path: info.outputPath(`unsafe-return-${index}-after.png`),
      fullPage: true,
    });
  }
});

for (const withCursor of [false, true]) {
  test(`new return flow preserves signed-out ${withCursor ? "long search cursor" : "Unicode and slash query"}`, async ({
    page,
  }, info) => {
    const source = await fixture(page, false);
    if (!withCursor) await page.setViewportSize({ width: 390, height: 844 });
    const query = withCursor ? "Order" : "café Orders/Order/Id";
    const target =
      "/catalog/search?q=" +
      encodeURIComponent(query) +
      (withCursor ? "&cursor=" + "A".repeat(4096) : "");
    await page.goto(target);
    await expect(page).toHaveURL(/\/sign-in\?returnTo=/);
    expect(new URL(page.url()).searchParams.get("returnTo")).toBe(target);
    await page.screenshot({
      path: info.outputPath("before-sign-in.png"),
      fullPage: true,
    });
    await page
      .getByRole("button", { name: "Sign in with Microsoft", exact: true })
      .click();
    await expect(page).toHaveURL(new URL(target, "http://127.0.0.1:5184").href);
    await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
    expect(source.returnTo()).toBe(target);
    expect(source.logins()).toBe(1);
    expect(source.searches.at(-1)!.searchParams.get("q")).toBe(query);
    expect(source.searches.at(-1)!.searchParams.get("cursor")).toBe(
      withCursor ? "A".repeat(4096) : null,
    );
    await page.screenshot({
      path: info.outputPath("after-return.png"),
      fullPage: true,
    });
  });
}

for (const kind of ["field", "rule"] as const) {
  test(`new return flow preserves retained ${kind} and resource focus on sign-in again`, async ({
    page,
  }, info) => {
    const source = await fixture(page, true);
    const document = source.data.projection.report!.document!;
    const id =
      kind === "field" ? document.fields[0]!.id : document.rules[0]!.id;
    const target = `${source.data.browserPath}?projectionDerivationId=${P}#${encodeURI(kind + ":" + id)}`;
    await page.goto(target);
    const inspector = page.getByRole("complementary", {
      name: "Context inspector",
    });
    const expected =
      kind === "field" ? "savedValue" : "Controlled saved explanation";
    await expect(inspector).toContainText(expected);
    await page.screenshot({
      path: info.outputPath("before-sign-in-again.png"),
      fullPage: true,
    });
    await page.locator("#account-trigger").click();
    await expect(
      page.getByRole("button", { name: "Sign in again", exact: true }),
    ).toBeInViewport();
    await page.screenshot({
      path: info.outputPath("account-actions-before-return.png"),
      fullPage: true,
    });
    const countBeforeLogin = source.retained.length;
    const loaded = page.waitForEvent("load");
    await page
      .getByRole("button", { name: "Sign in again", exact: true })
      .click();
    await loaded;
    await expect.poll(() => source.logins()).toBe(1);
    await expect
      .poll(() => source.retained.length)
      .toBeGreaterThan(countBeforeLogin);
    expect(
      source.retained
        .slice(countBeforeLogin)
        .every((path) => path.endsWith(`/${P}`)),
    ).toBe(true);
    expect(source.returnTo()).toBe(target);
    await expect(page).toHaveURL(new URL(target, "http://127.0.0.1:5184").href);
    await expect(inspector).toBeVisible();
    await expect(inspector).toContainText(expected);
    await page.screenshot({
      path: info.outputPath("after-retained-return.png"),
      fullPage: true,
    });
  });
}
