import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { workspaceFixture, legacyId } from "./workspace-chat.fixture";
import { projectPath } from "./catalog.fixture";
import {
  backToUnifiedChat,
  waitForUnifiedRoute,
} from "./unified-navigation.fixture";
import {
  chooseSavedConversation,
  historyTrigger,
  openMore,
  refreshChat,
} from "./chat-ui.fixture";

const chat = (page: Page) => page.locator("#workspace-chat");
async function createConversation(page: Page): Promise<string> {
  const previous = await historyTrigger(chat(page)).getAttribute(
    "data-conversation-id",
  );
  await chat(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  const selector = historyTrigger(chat(page));
  await expect(selector).not.toHaveAttribute("data-conversation-id", "");
  await expect(selector).not.toHaveAttribute("data-conversation-id", previous!);
  const selected = (await selector.getAttribute("data-conversation-id"))!;
  await expect(
    chat(page).getByRole("button", { name: "New conversation", exact: true }),
  ).toBeEnabled();
  await waitForUnifiedRoute(
    page,
    new URL(page.url()).pathname === "/chat"
      ? `/chat?conversationId=${selected}`
      : undefined,
  );
  return selected;
}

test("returning to a previous chat URL reconciles with the actual panel and protects its draft", async ({
  page,
}) => {
  await workspaceFixture(page);
  await page.goto("/chat");
  const a = await createConversation(page),
    b = await createConversation(page);
  await chooseSavedConversation(chat(page), a);
  await expect(page).toHaveURL(new RegExp(`conversationId=${a}`));
  await waitForUnifiedRoute(page, `/chat?conversationId=${a}`);
  await page
    .getByRole("navigation", { name: "Workspace", exact: true })
    .getByRole("link", { name: "Help & guide", exact: true })
    .click();
  await expect(page).toHaveURL(/\/help$/);
  await waitForUnifiedRoute(page, "/help");
  await chooseSavedConversation(chat(page), b);
  await expect(historyTrigger(chat(page))).toHaveAttribute(
    "data-conversation-id",
    b,
  );
  await expect(
    chat(page).getByRole("button", { name: "New conversation", exact: true }),
  ).toBeEnabled();
  await waitForUnifiedRoute(page, "/help");
  await chat(page).getByLabel("Question", { exact: true }).fill("Keep B draft");
  let warnings = 0;
  page.on("dialog", async (dialog) => {
    warnings++;
    await dialog.dismiss();
  });
  await page.goBack();
  await waitForUnifiedRoute(page);
  await expect(page).toHaveURL(new RegExp(`conversationId=${b}`));
  await expect(historyTrigger(chat(page))).toHaveAttribute(
    "data-conversation-id",
    b,
  );
  await expect(chat(page).getByLabel("Question", { exact: true })).toHaveValue(
    "Keep B draft",
  );
  expect(warnings).toBe(1);
});

for (const ambiguous of [false, true])
  test(`a busy question keeps its conversation URL and recovery owner (${ambiguous ? "ambiguous" : "accepted"})`, async ({
    page,
  }) => {
    const fixture = await workspaceFixture(page);
    await page.goto("/chat");
    const b = await createConversation(page);
    await page
      .getByRole("navigation", { name: "Workspace", exact: true })
      .getByRole("link", { name: "Catalog", exact: true })
      .click();
    await page
      .getByRole("link", { name: fixture.data.project.name, exact: true })
      .click();
    await page
      .getByRole("link", { name: fixture.data.mapping.name, exact: true })
      .click();
    await page.locator(`a[href="${fixture.data.browserPath}"]`).first().click();
    await expect(page).toHaveURL(new RegExp(fixture.data.browserPath + "$"));
    const a = await createConversation(page);
    let release!: () => void, arrived!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve)),
      pending = new Promise<void>((resolve) => (arrived = resolve));
    await page.route("**/turns", async (route) => {
      if (route.request().method() !== "POST") return route.fallback();
      arrived();
      await held;
      await route.fallback();
    });
    fixture.state.loseSend = ambiguous;
    try {
      await chat(page)
        .getByLabel("Question", { exact: true })
        .fill("Only send this to A");
      await chat(page)
        .getByRole("button", { name: "Send question", exact: true })
        .click();
      await pending;
      await backToUnifiedChat(page);
      await expect(page).toHaveURL(new RegExp(`conversationId=${a}`));
      expect(a).not.toBe(b);
      release();
      if (ambiguous)
        await expect(
          chat(page).getByRole("button", {
            name: "Retry original question",
            exact: true,
          }),
        ).toBeVisible();
      else await expect(chat(page)).toContainText("Answer complete.");
      await expect(historyTrigger(chat(page))).toHaveAttribute(
        "data-conversation-id",
        a,
      );
      const sends = fixture.posts.filter((p) => p.path.endsWith("/turns"));
      expect(sends).toHaveLength(1);
      expect(sends[0]!.path).toContain(a);
      expect(sends[0]!.body.text).toBe("Only send this to A");
    } finally {
      release();
    }
  });

for (const outcome of ["ready", "unavailable", "identityRejected"] as const)
  test(`a held availability refresh keeps route and conversation aligned (${outcome})`, async ({
    page,
  }) => {
    const fixture = await workspaceFixture(page);
    await page.goto("/chat");
    const b = await createConversation(page);
    await page
      .getByRole("navigation", { name: "Workspace", exact: true })
      .getByRole("link", { name: "Catalog", exact: true })
      .click();
    await page
      .getByRole("link", { name: fixture.data.project.name, exact: true })
      .click();
    await page
      .getByRole("link", { name: fixture.data.mapping.name, exact: true })
      .click();
    await page.locator(`a[href="${fixture.data.browserPath}"]`).first().click();
    const a = await createConversation(page);
    expect(a).not.toBe(b);
    await chat(page)
      .getByLabel("Question", { exact: true })
      .fill("Retain A while availability refreshes");
    let release!: () => void, arrived!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    const pending = new Promise<void>((resolve) => (arrived = resolve));
    await page.route("**/api/v1/organizations/*/chat", async (route) => {
      arrived();
      await held;
      if (outcome === "ready") await route.fallback();
      else
        await route.fulfill({
          status: outcome === "identityRejected" ? 403 : 503,
          json: { code: outcome },
        });
    });
    let warnings = 0;
    page.on("dialog", async (dialog) => {
      warnings++;
      await dialog.dismiss();
    });
    try {
      await refreshChat(chat(page));
      await pending;
      await backToUnifiedChat(page);
      await expect(page).toHaveURL(new RegExp(`conversationId=${a}`));
      await chat(page)
        .getByRole("button", { name: "Hide chat", exact: true })
        .click();
      release();
      await expect(
        chat(page).getByRole("button", {
          name: "Refresh chat",
          exact: true,
          includeHidden: true,
        }),
      ).toBeEnabled();
      await expect(page.locator("#workspace-chat-toggle")).toHaveAttribute(
        "aria-expanded",
        "false",
      );
      await page.locator("#workspace-chat-toggle").click();
      if (outcome === "identityRejected") {
        await expect(chat(page)).toContainText("The session was rejected.");
        await expect(
          chat(page).getByLabel("Question", { exact: true }),
        ).not.toBeVisible();
        await expect(chat(page)).not.toContainText(
          "Retain A while availability refreshes",
        );
      } else {
        if (outcome === "unavailable") {
          await expect(chat(page)).toContainText("Chat is unavailable.");
          await page.unroute("**/api/v1/organizations/*/chat");
          await refreshChat(chat(page));
        }
        await expect(historyTrigger(chat(page))).toHaveAttribute(
          "data-conversation-id",
          a,
        );
        await expect(
          chat(page).getByLabel("Question", { exact: true }),
        ).toHaveValue("Retain A while availability refreshes");
      }
      await expect(page).toHaveURL(new RegExp(`conversationId=${a}`));
      expect(warnings).toBe(0);
      expect(
        fixture.posts.filter((post) => post.path.endsWith("/turns")),
      ).toHaveLength(0);
    } finally {
      release();
    }
  });

test("the conversation library stays visible after hiding chat and reactive refreshes", async ({
  page,
}) => {
  await workspaceFixture(page);
  await page.setViewportSize({ width: 360, height: 900 });
  await page.goto("/chat");
  await expect(chat(page)).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(chat(page)).not.toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Conversations", exact: true }),
  ).toBeVisible();
  const url = page.url();
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.locator("#workspace-chat-toggle")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
  await page.getByRole("button", { name: "Open chat", exact: true }).click();
  await chat(page)
    .getByRole("button", { name: "Hide chat", exact: true })
    .click();
  await expect(chat(page)).not.toBeVisible();
  await expect(page).toHaveURL(url);
});
async function openChat(page: Page) {
  if (!(await chat(page).isVisible())) {
    await page.locator("#workspace-chat-toggle").click();
    await expect(page.locator("#workspace-chat-heading")).toBeFocused();
  }
  await expect(chat(page)).toBeVisible();
}

test("chat mobile behavior matches the viewport boundary at 627, 628, 679 and 680", async ({
  page,
}) => {
  await workspaceFixture(page);
  await page.goto("/chat");
  await createConversation(page);
  for (const width of [627, 628, 679, 680]) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openChat(page);
    await page.setViewportSize({ width, height: 900 });
    if (width < 680) {
      await expect(chat(page)).not.toBeVisible();
      await openChat(page);
      await expect(page.locator("#main-content")).not.toBeVisible();
      await expect(
        page.getByRole("separator", { name: "Resize chat", exact: true }),
      ).not.toBeVisible();
      await expect
        .poll(async () => Math.round((await chat(page).boundingBox())!.width))
        .toBe(width);
      await page.keyboard.press("Escape");
      await expect(chat(page)).not.toBeVisible();
      await expect(page.locator("#workspace-chat-toggle")).toBeFocused();
      await openChat(page);
      await chat(page)
        .getByRole("button", { name: "Hide chat", exact: true })
        .click();
      await expect(page.locator("#main-content")).toBeVisible();
    } else {
      await expect(chat(page)).toBeVisible();
      await expect(page.locator("#main-content")).toBeVisible();
      await expect(
        page.getByRole("separator", { name: "Resize chat", exact: true }),
      ).toBeVisible();
    }
  }
});

for (const [width, height, textSize] of [
  [360, 400, "100%"],
  [1024, 450, "100%"],
  [1024, 700, "200%"],
] as const)
  test(`populated short chat keeps model, composer, send and cancel reachable (${width}x${height}, ${textSize})`, async ({
    page,
  }) => {
    await workspaceFixture(page);
    await page.goto("/chat");
    await createConversation(page);
    const question = chat(page).getByLabel("Question", { exact: true });
    await question.fill("Explain this mapping");
    await chat(page)
      .getByRole("button", { name: "Send question", exact: true })
      .click();
    await expect(chat(page)).toContainText("Answer complete.");
    await page.setViewportSize({ width, height });
    if (width < 680) await expect(chat(page)).not.toBeVisible();
    await page.evaluate(
      (size) => (document.documentElement.style.fontSize = size),
      textSize,
    );
    await openChat(page);
    const model = chat(page).getByRole("combobox", {
      name: "Model profile",
      exact: true,
    });
    await model.focus();
    await expect(model).toBeFocused();
    await expect(model).toBeInViewport({ ratio: 1 });
    await question.fill("A second question");
    await expect(question).toBeInViewport({ ratio: 1 });
    await page.keyboard.press("Tab");
    const send = chat(page).getByRole("button", {
      name: "Send question",
      exact: true,
    });
    await expect(send).toBeFocused();
    await expect(send).toBeInViewport({ ratio: 1 });
    await capture(
      page,
      `populated-chat-short-${width}-${height}-${textSize.replace("%", "")}`,
    );
    let release!: () => void, arrived!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve)),
      pending = new Promise<void>((resolve) => (arrived = resolve));
    await page.route("**/events*", async (route) => {
      arrived();
      await held;
      await route.fallback();
    });
    try {
      await send.press("Enter");
      await pending;
      const cancel = chat(page).getByRole("button", {
        name: "Cancel answer",
        exact: true,
      });
      await cancel.focus();
      await expect(cancel).toBeFocused();
      await expect(cancel).toBeInViewport({ ratio: 1 });
      await expect(chat(page)).toBeVisible();
    } finally {
      release();
    }
  });

test("a disabled chat route offers no panel action or misleading instructions", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...fixture.data.context,
        features: { ...fixture.data.context.features, chat: false },
      },
    }),
  );
  await page.goto("/chat");
  await expect(page.getByRole("status")).toContainText(
    "Chat is not configured for this workspace.",
  );
  await expect(
    page.getByRole("button", { name: "Open chat", exact: true }),
  ).toHaveCount(0);
  await expect(page.locator("#workspace-chat-toggle")).toHaveCount(0);
  await expect(
    page.getByText(
      "Use the shared panel to continue a conversation without leaving your work.",
    ),
  ).toHaveCount(0);
  await expect(page.locator("braid-workspace-chat-panel")).not.toHaveClass(
    /chat-open/,
  );
  await expect(page.locator("#main-content")).toBeVisible();
});

test("an in-flight answer survives hiding and content navigation without another send", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  let release!: () => void, arrived!: () => void;
  const held = new Promise<void>((resolve) => (release = resolve)),
    requested = new Promise<void>((resolve) => (arrived = resolve));
  let streams = 0;
  await page.route("**/events*", async (route) => {
    streams++;
    arrived();
    await held;
    await route.fallback();
  });
  try {
    await page.goto("/chat");
    await chat(page)
      .getByRole("button", { name: "New conversation", exact: true })
      .click();
    await chat(page)
      .getByLabel("Question", { exact: true })
      .fill("Explain the mapping");
    await chat(page)
      .getByRole("button", { name: "Send question", exact: true })
      .click();
    await requested;
    await chat(page)
      .getByRole("button", { name: "Hide chat", exact: true })
      .click();
    await page
      .getByRole("navigation", { name: "Workspace", exact: true })
      .getByRole("link", { name: "Help & guide", exact: true })
      .click();
    await openChat(page);
    expect(streams).toBe(1);
    expect(fixture.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(
      1,
    );
    release();
    await expect(chat(page)).toContainText("Answer complete.");
  } finally {
    release();
  }
});

test("project content fits the shared shell at every supported width", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.goto(projectPath(fixture.data.project.scope));
  await expect(
    page.getByRole("heading", { name: fixture.data.project.name, exact: true }),
  ).toBeVisible();
  for (const width of [1440, 1024, 720, 360]) {
    await page.setViewportSize({ width, height: 1000 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await capture(page, `project-${width}`);
  }
});
async function capture(page: Page, name: string) {
  const directory = process.env.BRAID_UI_CAPTURE_DIR;
  if (directory) {
    await mkdir(directory, { recursive: true });
    await page.screenshot({ path: `${directory}/${name}.png`, fullPage: true });
  }
}

test("one conversation and unsent draft survive route changes, resizing and hide/restore", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(fixture.data.browserPath);
  await openChat(page);
  await chat(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  const question = chat(page).getByLabel("Question", { exact: true });
  await question.fill("Keep this draft across workspace contexts");
  const conversation = (await historyTrigger(chat(page)).getAttribute(
    "data-conversation-id",
  ))!;
  const separator = page.getByRole("separator", {
    name: "Resize chat",
    exact: true,
  });
  await separator.focus();
  await page.keyboard.press("End");
  await expect(separator).toHaveAttribute("aria-valuenow", "560");
  await capture(page, "mapping-chat-wide");
  await page.keyboard.press("Home");
  await expect(separator).toHaveAttribute("aria-valuenow", "300");
  const box = await separator.boundingBox();
  await page.mouse.move(box!.x + 3, box!.y + 30);
  await page.mouse.down();
  await page.mouse.move(box!.x - 57, box!.y + 30);
  await page.mouse.up();
  await expect(separator).toHaveAttribute("aria-valuenow", "360");
  await chat(page)
    .getByRole("button", { name: "Hide chat", exact: true })
    .click();
  await expect(question).not.toBeVisible();
  await capture(page, "mapping-chat-hidden");
  await page.locator("#workspace-chat-toggle").click();
  await expect(question).toHaveValue(
    "Keep this draft across workspace contexts",
  );
  await page
    .getByRole("navigation", { name: "Workspace", exact: true })
    .getByRole("link", { name: "Help & guide", exact: true })
    .click();
  await expect(page).toHaveURL(/\/help$/);
  await expect(question).toHaveValue(
    "Keep this draft across workspace contexts",
  );
  await expect(historyTrigger(chat(page))).toHaveAttribute(
    "data-conversation-id",
    conversation,
  );
  await expect(page.locator("braid-revision-chat")).toHaveCount(1);
  await capture(page, "guide-shared-chat");
  await page
    .getByRole("navigation", { name: "Workspace", exact: true })
    .getByRole("link", { name: "Conversations", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Conversations", exact: true }),
  ).toBeVisible();
  await expect(question).toHaveValue(
    "Keep this draft across workspace contexts",
  );
  expect(
    fixture.posts.filter((p) => p.path.endsWith("/conversations")),
  ).toHaveLength(1);
  expect(fixture.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(
    0,
  );
});

test("cancelled conversation changes restore the URL and retain the draft", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.goto("/chat");
  await expect(chat(page)).toBeVisible();
  await chat(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  const selected = (await historyTrigger(chat(page)).getAttribute(
    "data-conversation-id",
  ))!;
  await chat(page)
    .getByLabel("Question", { exact: true })
    .fill("Do not discard this question");
  let warnings = 0;
  page.on("dialog", async (dialog) => {
    warnings++;
    await dialog.dismiss();
  });
  await chooseSavedConversation(chat(page), legacyId);
  await expect(page).toHaveURL(new RegExp(`conversationId=${selected}`));
  await expect(chat(page).getByLabel("Question", { exact: true })).toHaveValue(
    "Do not discard this question",
  );
  expect(warnings).toBe(1);
  expect(fixture.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(
    0,
  );
});

test("actual shell and inspector fit desktop, narrow and phone viewports", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto(fixture.data.browserPath);
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toBeVisible();
  for (const width of [1440, 1024, 720, 360]) {
    await page.setViewportSize({ width, height: 1000 });
    if (await chat(page).isVisible())
      await chat(page)
        .getByRole("button", { name: "Hide chat", exact: true })
        .click();
    await capture(page, `mapping-${width}`);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await openChat(page);
    await expect(page.locator("#workspace-chat-heading")).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await capture(page, `chat-${width}`);
    if (width === 360) {
      await expect(page.locator("#main-content")).not.toBeVisible();
      await page.keyboard.press("Escape");
      await expect(page.locator("#workspace-chat-toggle")).toBeFocused();
    } else
      await chat(page)
        .getByRole("button", { name: "Hide chat", exact: true })
        .click();
    await page
      .getByRole("button", { name: "Show inspector", exact: true })
      .click();
    await expect(
      page.getByRole("complementary", { name: "Context inspector" }),
    ).toBeVisible();
    await capture(page, `inspector-${width}`);
    await page
      .getByRole("button", { name: /^(Hide inspector|Back to work)$/ })
      .click();
  }
  await page.setViewportSize({ width: 720, height: 800 });
  await page.evaluate(() => (document.documentElement.style.fontSize = "200%"));
  await capture(page, "mapping-200-percent-text");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  expect(errors).toEqual([]);
});

test("global navigation, search and account remain inside the unified shell", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.goto(fixture.data.browserPath);
  await waitForUnifiedRoute(page, fixture.data.browserPath);
  await expect(
    page.getByRole("button", { name: /^Search \/ Go to/ }),
  ).toBeVisible();
  await page.keyboard.press("Control+k");
  const dialog = page.getByRole("dialog", {
    name: "Search / Go to",
    exact: true,
  });
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Guide", exact: true }).click();
  await expect(page).toHaveURL(/\/help$/);
  await expect(page.locator(".app-shell.app-workbench")).toBeVisible();
  for (const [label, path] of [
    ["Catalog", "/catalog"],
    ["Notifications", "/notifications"],
    ["Administration", "/administration"],
  ]) {
    await page
      .getByRole("navigation", { name: "Workspace", exact: true })
      .getByRole("link", { name: label, exact: true })
      .click();
    await expect(page).toHaveURL(new RegExp(path + "(?:/.*)?$"));
    await expect(page.locator("main h1").first()).toBeVisible();
    await expect(page.locator(".app-shell.app-workbench")).toBeVisible();
    await capture(page, label.toLowerCase());
  }
  await page.setViewportSize({ width: 360, height: 900 });
  await page.locator("#account-trigger").click();
  const menu = await page.locator(".session-panel").boundingBox();
  expect(menu!.x).toBeGreaterThanOrEqual(0);
  expect(menu!.x + menu!.width).toBeLessThanOrEqual(360);
  await page.keyboard.press("Escape");
  await expect(page.locator("#account-trigger")).toBeFocused();
});
