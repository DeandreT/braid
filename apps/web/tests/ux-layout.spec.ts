import { expect, test, type Locator, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { projectPath, projectsPath } from "./catalog.fixture";
import { uxFixture, uxHash, uxOutput, uxSample } from "./ux-layout.fixture";
import { waitForUnifiedRoute } from "./unified-navigation.fixture";

// The assertions exercise the served Angular application, never the proposal HTML.
// A parent-owned static DEVELOPMENT build supplies baseURL; these tests start no servers.
const workflows = [
  ["mapping", "Data mapping"],
  ["preview", "Try sample"],
  ["revisions", "Versions"],
  ["compare", "Compare"],
  ["tests", "Tests"],
  ["builds", "Prepare"],
  ["releases", "In use"],
  ["audit", "Change history"],
] as const;
const docks = [
  ["inspector", "Inspector"],
  ["diagnostics", "Issues"],
  ["input", "Incoming data"],
  ["output", "Result"],
  ["diff", "Diff"],
  ["activity", "Activity"],
] as const;

async function noPageOverflow(page: Page) {
  await expect
    .poll(
      () =>
        page.evaluate(
          () =>
            Math.max(
              document.documentElement.scrollWidth,
              document.body.scrollWidth,
            ) - innerWidth,
        ),
      "Only contained tables/tab strips may overflow horizontally",
    )
    .toBeLessThanOrEqual(1);
}

async function activeTab(tab: Locator, panel: Locator) {
  await expect(tab).toHaveAttribute("aria-selected", "true");
  await expect(tab).toHaveAttribute("tabindex", "0");
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute(
    "aria-labelledby",
    (await tab.getAttribute("id"))!,
  );
  const controls = await tab.getAttribute("aria-controls");
  await expect(panel).toHaveAttribute("id", controls!);
}

async function accessibleState(page: Page, label: string) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
    .analyze();
  expect(
    result.violations.filter((violation) =>
      ["serious", "critical"].includes(violation.impact ?? ""),
    ),
    label,
  ).toEqual([]);
}

async function horizontallyReachable(tab: Locator) {
  await expect
    .poll(
      () =>
        tab.evaluate((element) => {
          const bounds = element.getBoundingClientRect();
          let left = 0,
            right = innerWidth;
          for (
            let parent = element.parentElement;
            parent;
            parent = parent.parentElement
          ) {
            if (
              /(auto|scroll|hidden|clip)/.test(
                getComputedStyle(parent).overflowX,
              )
            ) {
              const rect = parent.getBoundingClientRect();
              left = Math.max(left, rect.left);
              right = Math.min(right, rect.right);
            }
          }
          return bounds.left >= left - 2 && bounds.right <= right + 2;
        }),
      "Keyboard selection must reveal the active tab within its own scrollport",
    )
    .toBe(true);
}

async function returnToWork(page: Page) {
  const back = page.getByRole("button", { name: "Back to work", exact: true });
  if (await back.isVisible()) await back.click();
}

async function openWorkspaceLink(
  page: Page,
  name: string,
  destination: string,
) {
  const nav = page.getByRole("navigation", { name: "Workspace", exact: true });
  const link = nav.getByRole("link", { name, exact: true });
  if (!(await link.isVisible()))
    await page.getByRole("button", { name: "Menu", exact: true }).click();
  await link.click();
  await waitForUnifiedRoute(page, destination);
}

async function keyboardDisclosure(summary: Locator) {
  await summary.focus();
  await summary.press("Enter");
  await expect(summary.locator("..")).toHaveAttribute("open", "");
}

for (const width of [1440, 390, 360]) {
  test(`Astra workbench keeps all 17 real tab states usable at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    test.setTimeout(90000);
    const fixture = await uxFixture(page, baseURL!);
    await page.setViewportSize({ width, height: 960 });
    await page.goto(fixture.data.browserPath);
    const work = page.getByRole("tablist", {
      name: "Version workflows",
      exact: true,
    });
    const main = page.locator("#wb-main-panel");
    await expect(work.getByRole("tab")).toHaveCount(8);
    await expect(
      page.getByRole("table", { name: "Mapping field connections" }),
    ).toBeVisible();
    for (const [id, name] of workflows) {
      await returnToWork(page);
      const tab = work.getByRole("tab", { name, exact: true });
      await expect(tab).toHaveAttribute("data-workflow", id);
      await tab.click();
      await activeTab(tab, main);
      await expect(work.locator('[aria-selected="true"]')).toHaveCount(1);
      await expect(main).not.toBeEmpty();
      await noPageOverflow(page);
      await accessibleState(page, `${name} at ${width}px`);
    }
    const first = work.getByRole("tab", { name: "Data mapping", exact: true });
    await first.focus();
    await first.press("End");
    const last = work.getByRole("tab", { name: "Change history", exact: true });
    await expect(last).toBeFocused();
    expect(
      await last.evaluate((element) => element.matches(":focus-visible")),
    ).toBe(true);
    const focusStyle = await last.evaluate((element) => {
      const style = getComputedStyle(element);
      return {
        outline: style.outlineStyle,
        width: parseFloat(style.outlineWidth),
        shadow: style.boxShadow,
      };
    });
    expect(
      (focusStyle.outline !== "none" && focusStyle.width > 0) ||
        focusStyle.shadow !== "none",
    ).toBe(true);
    await activeTab(last, main);
    await horizontallyReachable(last);
    await last.press("Home");
    await expect(first).toBeFocused();
    await first.press("ArrowRight");
    await expect(
      work.getByRole("tab", { name: "Try sample", exact: true }),
    ).toBeFocused();
    await first.click();

    const investigation = page.getByRole("tablist", {
      name: "Investigation views",
      exact: true,
    });
    const dock = page.locator("#wb-dock-panel");
    await expect(investigation.getByRole("tab")).toHaveCount(6);
    for (const [id, name] of docks) {
      const tab = investigation.getByRole("tab", { name, exact: true });
      await expect(tab).toHaveAttribute("data-dock", id);
      await tab.click();
      await activeTab(tab, dock);
      await expect(investigation.locator('[aria-selected="true"]')).toHaveCount(
        1,
      );
      await expect(dock).not.toBeEmpty();
      await noPageOverflow(page);
      await accessibleState(page, `${name} investigation at ${width}px`);
    }
    await investigation
      .getByRole("tab", { name: "Activity", exact: true })
      .focus();
    await page.keyboard.press("Home");
    await expect(
      investigation.getByRole("tab", { name: "Inspector", exact: true }),
    ).toBeFocused();
    await page.keyboard.press("End");
    await horizontallyReachable(
      investigation.getByRole("tab", { name: "Activity", exact: true }),
    );
    await returnToWork(page);
    await first.click();
    await page
      .getByRole("button", {
        name: 'Inspect field ["savedValue"]',
        exact: true,
      })
      .click();
    const inspector = page.getByRole("tablist", {
      name: "Inspector views",
      exact: true,
    });
    await expect(inspector.getByRole("tab")).toHaveCount(3);
    for (const name of ["Rule", "Data flow", "Details"]) {
      const tab = inspector.getByRole("tab", { name, exact: true });
      await tab.click();
      await activeTab(tab, page.locator("#wb-inspector-panel"));
      await expect(page.locator("#wb-inspector-panel")).not.toBeEmpty();
      await noPageOverflow(page);
      if (width === 1440 && name === "Data flow") {
        const flow = page.locator("#wb-inspector-panel .declared-flow").first();
        await expect(flow).toBeVisible();
        await expect
          .poll(
            () =>
              flow.evaluate((element) => {
                const body = element.closest(".wb-dock-body");
                if (!body) return Infinity;
                const clip = body.getBoundingClientRect();
                const cards = [...element.querySelectorAll(".flow-card")];
                if (!cards.length) return Infinity;
                return Math.max(
                  ...cards.map((card) => {
                    const bounds = card.getBoundingClientRect();
                    return Math.max(
                      clip.top - bounds.top,
                      bounds.bottom - clip.bottom,
                    );
                  }),
                );
              }),
            "Default desktop flow cards must fit inside their visible dock body",
          )
          .toBeLessThanOrEqual(0.1);
      }
      await accessibleState(page, `${name} inspector at ${width}px`);
    }
    await inspector.getByRole("tab", { name: "Details", exact: true }).focus();
    await page.keyboard.press("Home");
    await expect(
      inspector.getByRole("tab", { name: "Rule", exact: true }),
    ).toBeFocused();
    await page.keyboard.press("End");
    await expect(
      inspector.getByRole("tab", { name: "Details", exact: true }),
    ).toBeFocused();
    await horizontallyReachable(
      inspector.getByRole("tab", { name: "Details", exact: true }),
    );
    expect(
      fixture.state.writes,
      "Reading any workflow/dock must not imply a mutation",
    ).toEqual([]);
    expect(fixture.escaped).toEqual([]);
  });

  test(`Astra catalog retains roles, keyboard links and every shell destination at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    const fixture = await uxFixture(page, baseURL!);
    await page.setViewportSize({ width, height: 960 });
    await page.goto(projectsPath(fixture.data.project.scope.organizationId));
    const list = page.getByRole("list", { name: "Projects", exact: true });
    await expect(list.getByRole("listitem")).toHaveCount(2);
    const primary = list
      .getByRole("listitem")
      .filter({ hasText: fixture.data.project.name });
    await expect(primary).toContainText("Viewer");
    await expect(
      list
        .getByRole("listitem")
        .filter({ hasText: "Synthetic carrier invoices" }),
    ).toContainText("Runner");
    await noPageOverflow(page);
    await expect(
      page.getByRole("link", { name: "Create project", exact: true }),
    ).toHaveCount(0);
    await page.keyboard.press("Tab");
    const skip = page.getByRole("link", {
      name: "Skip to content",
      exact: true,
    });
    await expect(skip).toBeFocused();
    await skip.press("Enter");
    await expect(page.locator("#main-content")).toBeFocused();
    const project = primary.getByRole("link", {
      name: fixture.data.project.name,
      exact: true,
    });
    await project.focus();
    await project.press("Enter");
    await expect(page).toHaveURL(
      new RegExp(projectPath(fixture.data.project.scope) + "$"),
    );
    await waitForUnifiedRoute(page, projectPath(fixture.data.project.scope));
    await expect(
      page.getByRole("link", { name: "Upload mapping", exact: true }),
    ).toHaveCount(0);
    await noPageOverflow(page);
    await openWorkspaceLink(page, "Help & guide", "/help");
    await expect(page).toHaveURL(/\/help$/);
    await openWorkspaceLink(page, "Administration", "/administration");
    await expect(page).toHaveURL(/\/administration$/);
    await openWorkspaceLink(page, "Conversations", "/chat");
    await expect(page).toHaveURL(/\/chat(?:\?|$)/);
    const hide = page
      .locator("#workspace-chat")
      .getByRole("button", { name: "Hide chat", exact: true });
    // Chat opens after its fresh context arrives, not merely when the URL changes.
    // Establish that visible state before hiding it; an instantaneous visibility
    // check can otherwise miss the opening and leave mobile content covered.
    await expect(hide).toBeVisible();
    await hide.click();
    await expect(page.locator("#workspace-chat-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await expect(page.locator("#workspace-chat")).not.toBeVisible();
    await expect(page.locator("#main-content")).toBeVisible();
    await expect(
      page.getByRole("heading", { name: "Conversations", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: /^Search \/ Go to/ }).click();
    const dialog = page.getByRole("dialog", {
      name: "Search / Go to",
      exact: true,
    });
    const search = dialog.getByRole("searchbox", {
      name: "Search catalog",
      exact: true,
    });
    await expect(search).toBeFocused();
    await search.fill("Synthetic");
    await dialog
      .getByRole("button", { name: "Search catalog", exact: true })
      .click();
    await expect(page).toHaveURL(/\/search\?/);
    await waitForUnifiedRoute(page);
    await expect(page.locator("#main-content")).toBeVisible();
    await expect(
      page.getByRole("heading", { name: /Search/ }).first(),
    ).toBeVisible();
    await noPageOverflow(page);
    expect(fixture.state.writes).toEqual([]);
    expect(fixture.escaped).toEqual([]);
  });

  test(`Astra sample inputs and verified outputs remain separate, functional sections at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    const fixture = await uxFixture(page, baseURL!);
    await page.setViewportSize({ width, height: 960 });
    await page.goto(fixture.data.browserPath);
    await page.getByRole("tab", { name: "Try sample", exact: true }).click();
    const sample = page.getByRole("region", {
      name: "Sample preview",
      exact: true,
    });
    await expect(
      sample.getByRole("group", { name: "Input documents", exact: true }),
    ).toBeVisible();
    await expect(
      sample.getByRole("group", { name: "Outputs", exact: true }),
    ).toBeVisible();
    await expect(sample).toContainText(
      "Results appear here after you run the sample.",
    );
    await sample
      .getByLabel("Primary input (JSON)", { exact: true })
      .fill(uxSample);
    await sample
      .getByRole("button", { name: "Run preview", exact: true })
      .click();
    const outputs = sample.getByRole("region", {
      name: "Preview outputs",
      exact: true,
    });
    await expect(outputs).toBeVisible();
    await expect(outputs).toContainText(uxHash(uxOutput));
    await outputs.getByRole("button", { name: /^Show / }).click();
    await expect(
      page.getByRole("region", { name: "Document bytes", exact: true }),
    ).toBeVisible();
    expect(fixture.state.writes.map((write) => write.path)).toEqual([
      `${fixture.data.apiPath}/previews`,
    ]);
    await noPageOverflow(page);
    expect(fixture.escaped).toEqual([]);
  });
}

const railDestinations = [
  ["Catalog", "/catalog"],
  ["Conversations", "/chat"],
  ["Notifications", "/notifications"],
  ["Help & guide", "/help"],
  ["Administration", "/administration"],
] as const;

for (const width of [680, 1023]) {
  test(`Astra compact navigation retains named, keyboard-operable destinations and permissions at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    const fixture = await uxFixture(page, baseURL!);
    const projects = projectsPath(fixture.data.project.scope.organizationId);
    await page.setViewportSize({ width, height: 960 });
    await page.goto(projects);
    const nav = page.getByRole("navigation", {
      name: "Workspace",
      exact: true,
    });
    await expect(nav.getByRole("link")).toHaveCount(railDestinations.length);
    await expect
      .poll(() =>
        nav.evaluate((element) => element.getBoundingClientRect().width),
      )
      .toBe(44);
    await expect(nav.locator(".workspace-nav-caption")).not.toBeVisible();
    await expect(
      page.getByRole("button", { name: "Menu", exact: true }),
    ).not.toBeVisible();

    const first = nav.getByRole("link", { name: "Catalog", exact: true });
    await first.focus();
    await first.press("Tab");
    await expect(
      nav.getByRole("link", { name: "Conversations", exact: true }),
    ).toBeFocused();
    await page.keyboard.press("Shift+Tab");
    for (const [index, [name, href]] of railDestinations.entries()) {
      const link = nav.getByRole("link", { name, exact: true });
      await expect(link).toBeFocused();
      await expect(link).toHaveAccessibleName(name);
      await expect(link).toHaveAttribute("aria-label", name);
      await expect(link).toHaveAttribute("title", name);
      await expect(link).toHaveAttribute("href", href);
      const bounds = await link.boundingBox();
      expect(bounds!.width, `${name} target width`).toBeGreaterThanOrEqual(44);
      expect(bounds!.height, `${name} target height`).toBeGreaterThanOrEqual(
        44,
      );
      await expect(link.locator(".rail-icon")).toBeVisible();
      await expect(link.locator(".rail-label")).not.toBeVisible();
      const focus = await link.evaluate((element) => {
        const style = getComputedStyle(element);
        return {
          visible: element.matches(":focus-visible"),
          outline: style.outlineStyle,
          width: parseFloat(style.outlineWidth),
          shadow: style.boxShadow,
        };
      });
      expect(focus.visible).toBe(true);
      expect(
        (focus.outline !== "none" && focus.width > 0) ||
          focus.shadow !== "none",
      ).toBe(true);
      await link.hover();
      await expect(link.locator(".rail-label")).not.toBeVisible();
      if (index < railDestinations.length - 1) await link.press("Tab");
    }
    await noPageOverflow(page);
    await accessibleState(page, `Compact navigation at ${width}px`);

    for (const [name, href] of railDestinations) {
      const link = nav.getByRole("link", { name, exact: true });
      await link.click();
      await waitForUnifiedRoute(page, href === "/catalog" ? projects : href);
      await expect(link).toHaveAttribute("aria-current", "page");
      await expect(page.locator("#main-content")).toBeVisible();
      if (href === "/chat") {
        const hide = page.locator("#workspace-chat").getByRole("button", {
          name: "Hide chat",
          exact: true,
        });
        await expect(hide).toBeVisible();
        await hide.click();
        await expect(page.locator("#workspace-chat")).not.toBeVisible();
      }
      await noPageOverflow(page);
    }
    await openWorkspaceLink(page, "Catalog", projects);
    const list = page.getByRole("list", { name: "Projects", exact: true });
    const primary = list.getByRole("listitem").filter({
      hasText: fixture.data.project.name,
    });
    await expect(primary).toContainText("Viewer");
    await expect(
      list
        .getByRole("listitem")
        .filter({ hasText: "Synthetic carrier invoices" }),
    ).toContainText("Runner");
    await expect(
      page.getByRole("link", { name: "Create project", exact: true }),
    ).toHaveCount(0);
    await primary
      .getByRole("link", { name: fixture.data.project.name, exact: true })
      .click();
    await waitForUnifiedRoute(page, projectPath(fixture.data.project.scope));
    await expect(
      page.getByRole("region", { name: "Project metadata", exact: true }),
    ).toContainText("viewer");
    await expect(
      page.getByRole("link", { name: "Upload mapping", exact: true }),
    ).toHaveCount(0);
    expect(fixture.state.writes).toEqual([]);
    expect(fixture.escaped).toEqual([]);
  });

  test(`Astra compact navigation scrolls its last keyboard target into the short rail at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    const fixture = await uxFixture(page, baseURL!);
    await page.setViewportSize({ width, height: 240 });
    await page.goto(projectsPath(fixture.data.project.scope.organizationId));
    const nav = page.getByRole("navigation", {
      name: "Workspace",
      exact: true,
    });
    await expect(nav.getByRole("link")).toHaveCount(railDestinations.length);
    await expect(nav).toHaveCSS("overflow-y", "auto");
    await expect
      .poll(() =>
        nav.evaluate((element) => element.getBoundingClientRect().width),
      )
      .toBe(44);
    expect(
      await nav.evaluate(
        (element) => element.scrollHeight - element.clientHeight,
      ),
    ).toBeGreaterThan(0);
    await nav.getByRole("link", { name: "Catalog", exact: true }).focus();
    for (const [index, [name]] of railDestinations.entries()) {
      const link = nav.getByRole("link", { name, exact: true });
      await expect(link).toBeFocused();
      const bounds = await link.boundingBox();
      expect(bounds!.width).toBeGreaterThanOrEqual(44);
      expect(bounds!.height).toBeGreaterThanOrEqual(44);
      if (index < railDestinations.length - 1) await link.press("Tab");
    }
    const last = nav.getByRole("link", { name: "Administration", exact: true });
    await expect(last.locator(".rail-label")).not.toBeVisible();
    await expect
      .poll(() =>
        last.evaluate((element) => {
          const nav = element.closest("nav")!;
          const scrollport = nav.getBoundingClientRect();
          const bounds = element.getBoundingClientRect();
          const top = Math.max(0, scrollport.top + nav.clientTop);
          const bottom = Math.min(
            innerHeight,
            scrollport.top + nav.clientTop + nav.clientHeight,
          );
          return bounds.top >= top - 1 && bounds.bottom <= bottom + 1;
        }),
      )
      .toBe(true);
    expect(await nav.evaluate((element) => element.scrollTop)).toBeGreaterThan(
      0,
    );
    await noPageOverflow(page);
    expect(fixture.state.writes).toEqual([]);
    expect(fixture.escaped).toEqual([]);
  });
}

test("Astra navigation preserves the labeled desktop rail and phone menu at compact boundaries", async ({
  page,
  baseURL,
}) => {
  const fixture = await uxFixture(page, baseURL!);
  await page.setViewportSize({ width: 1024, height: 960 });
  await page.goto(projectsPath(fixture.data.project.scope.organizationId));
  const nav = page.getByRole("navigation", { name: "Workspace", exact: true });
  await expect(nav.getByRole("link")).toHaveCount(railDestinations.length);
  await expect
    .poll(() =>
      nav.evaluate((element) => element.getBoundingClientRect().width),
    )
    .toBe(192);
  await expect(nav.locator(".workspace-nav-caption")).toBeVisible();
  for (const [name] of railDestinations)
    await expect(
      nav.getByRole("link", { name, exact: true }).locator(".rail-label"),
    ).toBeVisible();
  await noPageOverflow(page);

  await page.setViewportSize({ width: 1023, height: 960 });
  await expect
    .poll(() =>
      nav.evaluate((element) => element.getBoundingClientRect().width),
    )
    .toBe(44);
  for (const [name] of railDestinations)
    await expect(
      nav.getByRole("link", { name, exact: true }).locator(".rail-label"),
    ).not.toBeVisible();
  await noPageOverflow(page);

  await page.setViewportSize({ width: 679, height: 960 });
  const menu = page.getByRole("button", { name: "Menu", exact: true });
  await expect(menu).toBeVisible();
  await expect(menu).toHaveAttribute("aria-expanded", "false");
  await expect(nav).not.toBeVisible();
  await menu.click();
  await expect(menu).toHaveAttribute("aria-expanded", "true");
  await expect(nav).toBeVisible();
  for (const [name] of railDestinations)
    await expect(
      nav.getByRole("link", { name, exact: true }).locator(".rail-label"),
    ).toBeVisible();
  await noPageOverflow(page);
  await accessibleState(page, "Phone navigation at 679px");
  expect(fixture.state.writes).toEqual([]);
  expect(fixture.escaped).toEqual([]);
});

test("Astra unavailable tests, builds and release status never become successful or enabled", async ({
  page,
  baseURL,
}) => {
  const fixture = await uxFixture(page, baseURL!);
  fixture.state.regressionUnavailable = true;
  await page.goto(fixture.data.browserPath);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  const regression = page.getByRole("region", {
    name: "Regression tests",
    exact: true,
  });
  await expect(regression.getByRole("alert")).toBeVisible();
  await expect(
    regression.getByRole("button", { name: "New test case", exact: true }),
  ).toHaveCount(0);
  await expect(regression).not.toContainText("Matches expected result");
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
  await expect(page.locator("#wb-main-panel")).toContainText(/unavailable/i);
  await expect(page.locator("#wb-main-panel")).not.toContainText(
    "Preparation complete",
  );
  await page.getByRole("tab", { name: "In use", exact: true }).click();
  await expect(page.locator("#wb-main-panel")).toContainText("Unknown");
  await expect(page.locator("#wb-main-panel")).toContainText(
    "requires permission and current verification",
  );
  expect(fixture.state.writes).toEqual([]);
  expect(fixture.escaped).toEqual([]);
});

for (const width of [1440, 390, 360]) {
  test(`Astra creation review preserves explicit decisions, digest disclosures and recovery at ${width}px`, async ({
    page,
    baseURL,
  }) => {
    const fixture = await uxFixture(page, baseURL!);
    await page.setViewportSize({ width, height: 960 });
    await page.goto(fixture.reviewPath);
    const requirements = page.getByRole("region", {
      name: "Exact creation proposal",
      exact: true,
    });
    const sample = page.getByRole("region", {
      name: "Sample execution",
      exact: true,
    });
    const approval = page.getByRole("region", {
      name: "Save exact proposal",
      exact: true,
    });
    await expect(requirements).toBeVisible();
    await expect(sample).toBeVisible();
    await expect(approval).toBeVisible();
    const save = approval.getByRole("button", {
      name: "Approve and save this mapping",
      exact: true,
    });
    await expect(save).toBeDisabled();
    const critical = requirements.getByRole("checkbox", {
      name: "I accept: Use the customer's supplied item code.",
      exact: true,
    });
    const reviewed = approval.getByRole("checkbox", {
      name: "I've reviewed this exact mapping plan and its source evidence.",
      exact: true,
    });
    for (const checkbox of [critical, reviewed]) {
      await expect(checkbox).not.toBeChecked();
      const box = await checkbox.boundingBox();
      expect(
        box!.width,
        "Native checkbox must not stretch to the container width",
      ).toBeLessThanOrEqual(32);
      expect(box!.height).toBeLessThanOrEqual(32);
      expect(box!.width).toBeGreaterThan(0);
    }
    // Open identity disclosures through their real keyboard controls, outermost first.
    const identities = requirements
      .locator("details")
      .filter({ hasText: fixture.proposal.nativeDigest });
    expect(
      await identities.count(),
      "Long digests belong in accessible disclosures",
    ).toBeGreaterThan(0);
    for (let index = 0; index < (await identities.count()); index++) {
      const details = identities.nth(index);
      if ((await details.getAttribute("open")) === null)
        await keyboardDisclosure(details.locator(":scope > summary"));
    }
    await expect(
      requirements.getByText(fixture.proposal.nativeDigest, { exact: true }),
    ).toBeVisible();
    const plan = requirements
      .locator("summary")
      .filter({ hasText: /Exact mapping plan/ });
    if ((await plan.locator("..").getAttribute("open")) === null)
      await keyboardDisclosure(plan);
    await expect(requirements.locator("pre")).toHaveText(
      fixture.proposal.planJson,
    );
    await sample
      .getByLabel("Paste sample input", { exact: true })
      .fill(uxSample);
    await sample
      .getByRole("button", { name: "Run sample", exact: true })
      .click();
    await expect(
      page
        .getByRole("region", { name: "Verified sample output", exact: true })
        .locator("pre"),
    ).toHaveText(uxOutput);
    await expect(save).toBeDisabled();
    await approval
      .getByRole("button", { name: "Check save status", exact: true })
      .click();
    expect(fixture.state.proposal.approved).toBe(false);
    await reviewed.check();
    await expect(save).toBeDisabled();
    await critical.check();
    await expect(save).toBeEnabled();
    await noPageOverflow(page);
    fixture.state.loseApprovalAcknowledgement = true;
    await save.click();
    await expect(
      page.getByText(
        "The save response is unconfirmed. Check its status before another approval.",
        { exact: true },
      ),
    ).toBeVisible();
    await expect(save).toBeDisabled();
    await approval
      .getByRole("button", { name: "Check save status", exact: true })
      .click();
    await expect(
      approval.getByRole("link", { name: "Open saved revision", exact: true }),
    ).toHaveAttribute("href", fixture.data.browserPath);
    expect(
      fixture.state.writes.map((write) => write.path.split("/").at(-1)),
    ).toEqual(["preview", "recover", "approve", "recover"]);
    const decision = fixture.state.writes.find((write) =>
      write.path.endsWith("/approve"),
    )!;
    expect(decision.body).toEqual({
      fingerprint: fixture.proposal.fingerprint,
      acceptedCriticalAssumptions: ["A1"],
    });
    await noPageOverflow(page);
    expect(fixture.escaped).toEqual([]);
  });
}

test("Astra short zoom-equivalent layouts keep context ahead of workflow content", async ({
  page,
  baseURL,
}) => {
  const fixture = await uxFixture(page, baseURL!);
  await page.setViewportSize({ width: 720, height: 480 });
  await page.goto(fixture.data.browserPath);
  const workflows = page.getByRole("tablist", {
    name: "Version workflows",
    exact: true,
  });
  for (const name of ["Data mapping", "Try sample"]) {
    await workflows.getByRole("tab", { name, exact: true }).click();
    const context = page.locator(".wb-context");
    const bar = page.locator(".wb-workflow-bar");
    await expect
      .poll(async () => {
        const identity = await context.boundingBox();
        const navigation = await bar.boundingBox();
        return identity && navigation
          ? navigation.y - (identity.y + identity.height)
          : -Infinity;
      }, "Workflow content must not paint over the mapping context")
      .toBeGreaterThanOrEqual(-1);
    const heading = page.locator("#catalog-page-title");
    await heading.scrollIntoViewIfNeeded();
    await expect(heading).toBeInViewport();
    expect(
      await heading.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        const painted = document.elementFromPoint(
          rect.left + rect.width / 2,
          rect.top + rect.height / 2,
        );
        return painted === element || element.contains(painted);
      }),
      "The mapping heading must remain painted, not just have a rectangle",
    ).toBe(true);
    await noPageOverflow(page);
  }
  expect(fixture.state.writes).toEqual([]);
  expect(fixture.escaped).toEqual([]);
});

test("Astra feedback carries exact identity without implicitly approving unanswered requirements", async ({
  page,
  baseURL,
}) => {
  const fixture = await uxFixture(page, baseURL!);
  fixture.state.proposal.questions = [
    { text: "Which unit of measure?", critical: true },
  ];
  await page.goto(fixture.reviewPath);
  await expect(page.getByText(/Which unit of measure\?/)).toBeVisible();
  await page
    .getByRole("checkbox", {
      name: "I accept: Use the customer's supplied item code.",
      exact: true,
    })
    .check();
  await page
    .getByRole("checkbox", {
      name: "I've reviewed this exact mapping plan and its source evidence.",
      exact: true,
    })
    .check();
  await expect(
    page.getByRole("button", {
      name: "Approve and save this mapping",
      exact: true,
    }),
  ).toBeDisabled();
  const feedback = page.getByRole("link", {
    name: "Give feedback in chat",
    exact: true,
  });
  await feedback.click();
  await expect(page).toHaveURL(/\/chat(?:\?|$)/);
  const destination = new URL(page.url());
  expect(destination.pathname).toBe("/chat");
  expect(destination.searchParams.get("creationProjectId")).toBe(
    fixture.proposal.projectId,
  );
  expect(destination.searchParams.get("creationProposalId")).toBe(
    fixture.proposal.proposalId,
  );
  expect(destination.searchParams.get("creationFingerprint")).toBe(
    fixture.proposal.fingerprint,
  );
  expect(fixture.state.writes).toEqual([]);
  expect(fixture.escaped).toEqual([]);
});

for (const theme of ["light", "dark"] as const) {
  test(`Astra actual catalog, workbench and review pass serious accessibility and contrast checks (${theme})`, async ({
    page,
    baseURL,
  }) => {
    test.setTimeout(90000);
    const fixture = await uxFixture(page, baseURL!);
    await page.emulateMedia({ colorScheme: theme });
    await page.setViewportSize({ width: 390, height: 960 });
    for (const path of [
      projectsPath(fixture.data.project.scope.organizationId),
      fixture.data.browserPath,
      fixture.reviewPath,
    ]) {
      await page.goto(path);
      await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
      await expect(page.locator("#main-content h1")).toBeVisible();
      if (path === fixture.data.browserPath)
        await expect(
          page.getByRole("table", { name: "Mapping field connections" }),
        ).toBeVisible();
      if (path === fixture.reviewPath)
        await expect(
          page.getByRole("region", {
            name: "Exact creation proposal",
            exact: true,
          }),
        ).toBeVisible();
      const result = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze();
      expect(
        result.violations.filter((violation) =>
          ["serious", "critical"].includes(violation.impact ?? ""),
        ),
        path,
      ).toEqual([]);
      await noPageOverflow(page);
    }
    const contrasts = await page.evaluate(() => {
      const resolve = (token: string) => {
        const probe = document.createElement("span");
        probe.style.color = `var(${token})`;
        document.body.append(probe);
        const color = getComputedStyle(probe).color;
        probe.remove();
        return color;
      };
      const luminance = (color: string) =>
        color
          .match(/[\d.]+/g)!
          .slice(0, 3)
          .map(Number)
          .map((part) => part / 255)
          .map((part) =>
            part <= 0.04045 ? part / 12.92 : ((part + 0.055) / 1.055) ** 2.4,
          )
          .reduce(
            (sum, value, index) =>
              sum + value * [0.2126, 0.7152, 0.0722][index]!,
            0,
          );
      const pairs = [
        ["--text", "--surface", 4.5],
        ["--text-muted", "--surface", 4.5],
        ["--text", "--page", 4.5],
        ["--focus-ring", "--surface", 3],
        ["--wb-source-text", "--wb-source-tint", 4.5],
        ["--wb-rule-text", "--wb-rule-tint", 4.5],
        ["--wb-output-text", "--wb-output-tint", 4.5],
      ] as const;
      return pairs.map(([foreground, background, minimum]) => {
        const a = luminance(resolve(foreground)),
          b = luminance(resolve(background));
        return {
          foreground,
          background,
          minimum,
          ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05),
        };
      });
    });
    for (const value of contrasts)
      expect(
        value.ratio,
        `${value.foreground} on ${value.background}`,
      ).toBeGreaterThanOrEqual(value.minimum);
    expect(fixture.state.writes).toEqual([]);
    expect(fixture.escaped).toEqual([]);
  });
}
