import { expect, test, type Page, type Route } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { expectSignedInAccount } from "./hosted-account.fixture";
import {
  chooseSavedConversation,
  historyTrigger,
  openFocus,
  openHistory,
  openMore,
} from "./chat-ui.fixture";
import {
  workspaceFixture,
  legacyId,
  otherMapping,
  otherProject,
} from "./workspace-chat.fixture";

const panel = (page: Page) =>
  page.getByRole("complementary", { name: /^(Workspace|Mapping) chat$/ });
const focusStatus = (page: Page) =>
  panel(page).locator("#chat-focus-menu").getByRole("status");
const expectComplete = (page: Page) =>
  expect(
    panel(page)
      .getByRole("region", { name: "Selected answer" })
      .getByRole("status"),
  ).toHaveText("Answer complete.");

test("the hosted session summary check dismisses its menu before workbench chat actions", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  const identityContext = `v1.${"A".repeat(43)}`;
  const requestToken = "ZXBvY2gtYQ";
  // The session and catalog must present the same owner and mode. A development
  // catalog response is deliberately rejected under an authenticated session.
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...f.data.context,
        mode: "session",
        identityContext,
        requestToken,
      },
    }),
  );
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: {
        version: 1,
        mode: "session",
        state: "authenticated",
        authActionRequestToken: "YXV0aC1h",
        requestToken,
        identityContext,
        displayName: "Fixture account",
      },
    }),
  );
  await page.goto(f.data.browserPath);
  await expectSignedInAccount(page);
  await page.getByRole("button", { name: "Show chat", exact: true }).click();
  await expect(panel(page)).toBeVisible();
  await create(page);
  await ask(page);
  await expectComplete(page);
});

test("new conversation route echoes do not replace an explicitly selected model profile", async ({
  page,
}) => {
  const f = await workspaceFixture(page, { secondProfile: true });
  await page.goto("/chat");
  await create(page);
  const selector = panel(page).getByRole("combobox", {
    name: "Model profile",
    exact: true,
  });
  await selector.selectOption("fixture-payloads");
  for (let attempt = 0; attempt < 3; attempt++) {
    await create(page);
    await expect(selector).toHaveValue("fixture-payloads");
    await ask(page, "Use the explicitly selected profile.");
    await expectComplete(page);
    expect(
      f.posts.filter((post) => post.path.endsWith("/turns")).at(-1)!.body
        .profileId,
    ).toBe("fixture-payloads");
  }
});
async function create(page: Page, acknowledged = true) {
  await page.locator("#workspace-chat-toggle").waitFor();
  if (
    (await page
      .locator("#workspace-chat-toggle")
      .getAttribute("aria-expanded")) === "false"
  )
    await page.locator("#workspace-chat-toggle").click();
  await expect(panel(page)).toBeVisible();
  const created = acknowledged
    ? page.waitForResponse(
        (response) =>
          response.request().method() === "POST" &&
          new URL(response.url()).pathname.endsWith("/conversations"),
      )
    : null;
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  if (created) {
    const response = await created;
    expect(response.status()).toBe(201);
    const conversation = await response.json();
    await expect(historyTrigger(panel(page))).toHaveAttribute(
      "data-conversation-id",
      conversation.conversationId,
    );
    await expect(
      panel(page).getByLabel("Question", { exact: true }),
    ).toBeVisible();
  }
}
async function ask(
  page: Page,
  text = "Explain the partner mapping and how to inspect it.",
) {
  await panel(page).getByLabel("Question", { exact: true }).fill(text);
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
}

test("standalone chat lists both kinds, searches mappings, pins focus and links cross-project/help sources", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto("/chat");
  await expect(
    page.getByRole("heading", { name: "Conversations", exact: true }),
  ).toBeVisible();
  await expect(await openHistory(panel(page))).toContainText(
    "Earlier revision conversation",
  );
  await page.keyboard.press("Escape");
  await openFocus(panel(page));
  await page.getByLabel("Find mapping").fill("Partner");
  await page
    .getByLabel("Mapping focus", { exact: true })
    .selectOption(otherMapping);
  await page
    .getByLabel("Revision focus", { exact: true })
    .selectOption(f.data.revision.scope.revisionId);
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await create(page);
  expect(f.posts[0]!.body.focus).toEqual({
    projectId: otherProject,
    mappingId: otherMapping,
    revisionId: f.data.revision.scope.revisionId,
  });
  await ask(page);
  await expectComplete(page);
  await expect(
    panel(page).getByRole("link", { name: "Open mapping", exact: true }),
  ).toHaveAttribute(
    "href",
    `/catalog/organizations/${f.data.revision.scope.organizationId}/projects/${otherProject}/mappings/${otherMapping}`,
  );
  const guide = panel(page).getByRole("link", {
    name: "Open help chat",
    exact: true,
  });
  await expect(guide).toHaveAttribute("href", "/help/chat");
  const next = page.context().waitForEvent("page");
  await guide.click();
  const help = await next;
  await expect(
    help.getByRole("heading", { name: "Using chat", exact: true }).first(),
  ).toBeVisible();
  await help.close();
  expect(f.posts[1]!.body).toEqual(
    expect.objectContaining({ profileId: "fixture", selectors: [] }),
  );
  expect(Object.keys(f.posts[1]!.body).sort()).toEqual([
    "profileId",
    "profileVersion",
    "selectors",
    "text",
    "turnId",
  ]);
});

test("the workbench creates an exact-focused workspace thread and Open in chat continues its server identity", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto(f.data.browserPath);
  await create(page);
  expect(f.posts[0]!.path).toBe(`${f.root}/conversations`);
  expect(f.posts[0]!.body.focus).toEqual({
    projectId: f.data.revision.scope.projectId,
    mappingId: f.data.revision.scope.mappingId,
    revisionId: f.data.revision.scope.revisionId,
  });
  await ask(page);
  await expectComplete(page);
  const original = page.url(),
    id = f.posts[0]!.body.conversationId;
  const next = page.context().waitForEvent("page");
  await openMore(panel(page));
  await panel(page)
    .getByRole("link", { name: "Open full page", exact: true })
    .click();
  const standalone = await next;
  await expect(standalone).toHaveURL(
    new RegExp(`/chat\\?conversationId=${id}$`),
  );
  await expect(panel(standalone)).toContainText(
    "Partner orders maps the order fields.",
  );
  expect(page.url()).toBe(original);
  expect(f.posts).toHaveLength(2);
  await standalone.close();
});

test("existing revision-pinned conversations continue through the original routes", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto("/chat");
  await chooseSavedConversation(panel(page), legacyId);
  await expect(panel(page).locator(".chat-context")).toContainText(
    "Pinned version",
  );
  await ask(page, "Continue the original pinned question.");
  await expectComplete(page);
  expect(f.posts[0]!.path).toBe(
    `${f.data.apiPath}/chat/conversations/${legacyId}/turns`,
  );
  await page.reload();
  await expect(panel(page)).toContainText(
    "Continue the original pinned question.",
  );
  expect(f.posts).toHaveLength(1);
  await expect(
    panel(page).getByRole("link", {
      name: "Open saved explanation report",
    }),
  ).toHaveAttribute(
    "href",
    `${f.data.browserPath}?projectionDerivationId=${f.data.projection.result!.derivationId}`,
  );
});

test("removing selectable model profiles does not hide a saved answer or dispatch another turn", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto("/chat");
  await create(page);
  await ask(page);
  await expectComplete(page);
  f.state.profilesAvailable = false;
  await page.reload();
  await expect(panel(page)).toContainText(
    "Partner orders maps the order fields.",
  );
  await expect(
    panel(page).getByRole("button", { name: "Send question", exact: true }),
  ).toBeDisabled();
  expect(f.posts).toHaveLength(2);
});

test("the focus panel lists mappings when it opens and filters as the user types, ignoring a superseded scan", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  const held: Route[] = [];
  let hold = false,
    projectReads = 0;
  await page.route(
    new RegExp(
      `/api/v1/organizations/${f.data.revision.scope.organizationId}/projects(\\?|$)`,
    ),
    (route) => {
      projectReads++;
      return route.fallback();
    },
  );
  await page.route(
    new RegExp(`/projects/${otherProject}/mappings(\\?|$)`),
    (route) => {
      if (!hold) return route.fallback();
      hold = false;
      held.push(route);
    },
  );
  await page.goto("/chat");
  await openFocus(panel(page));
  const focus = page.getByRole("combobox", {
    name: "Mapping focus",
    exact: true,
  });
  await expect(focus).toContainText("Partner orders");
  await expect(focusStatus(page)).toHaveText("Select a mapping below.");
  expect(projectReads).toBe(1);
  await expect(page.getByLabel("Find mapping")).toBeFocused();

  hold = true;
  await page.getByLabel("Find mapping").fill("Partner");
  await expect.poll(() => held.length).toBe(1);
  await page.getByLabel("Find mapping").fill("Nothing matches this");
  await expect(focusStatus(page)).toHaveText("No readable mapping matched.");
  await held[0]!
    .fulfill({
      json: {
        mappings: [{ ...f.data.mapping, name: "Partner orders" }],
        nextCursor: null,
      },
    })
    .catch(() => {});
  await expect(focusStatus(page)).toHaveText("No readable mapping matched.");
  await expect(focus).not.toContainText("Partner orders");
  await expect(page.getByLabel("Find mapping")).toBeFocused();
  expect(projectReads).toBe(3);
});

test("focus search continues beyond its per-action scan budget without restarting earlier pages", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  let pages = 0;
  await page.route(
    `**/api/v1/organizations/${f.data.revision.scope.organizationId}/projects/${f.data.revision.scope.projectId}/mappings?*`,
    (route) => {
      pages++;
      return route.fulfill({
        json: {
          mappings:
            pages < 40 ? [] : [{ ...f.data.mapping, name: "Later mapping" }],
          nextCursor:
            pages < 40
              ? Buffer.from(`page${pages}`).toString("base64url")
              : null,
        },
      });
    },
  );
  await page.goto("/chat");
  await openFocus(panel(page));
  await expect(focusStatus(page)).toContainText("More projects");
  pages = 0;
  await page.getByLabel("Find mapping").fill("Later");
  await expect.poll(() => pages).toBeGreaterThan(0);
  await expect(focusStatus(page)).toContainText("More projects");
  await page
    .getByRole("button", { name: "Show more mappings", exact: true })
    .click();
  await expect(
    page.getByRole("combobox", { name: "Mapping focus", exact: true }),
  ).toContainText("Later mapping");
  expect(pages).toBe(40);
});

test("a failed focus-search continuation retries pages whose matches were never presented", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  let fail = true;
  const cursors: string[] = [];
  await page.route(
    `**/api/v1/organizations/${f.data.revision.scope.organizationId}/projects/${f.data.revision.scope.projectId}/mappings?*`,
    (route) => {
      const cursor =
        new URL(route.request().url()).searchParams.get("cursor") ?? "";
      cursors.push(cursor);
      const pageNumber = cursor
        ? Number(Buffer.from(cursor, "base64url").toString().slice(4))
        : 0;
      if (pageNumber === 40 && fail) {
        fail = false;
        return route.abort("failed");
      }
      const matches =
        pageNumber === 39 ? [{ ...f.data.mapping, name: "Later match" }] : [];
      return route.fulfill({
        json: {
          mappings: matches,
          nextCursor:
            pageNumber < 40
              ? Buffer.from(`page${pageNumber + 1}`).toString("base64url")
              : null,
        },
      });
    },
  );
  await page.goto("/chat");
  await openFocus(panel(page));
  await expect(focusStatus(page)).toContainText("More projects");
  cursors.length = 0;
  fail = true;
  await page.getByLabel("Find mapping").fill("Later match");
  await expect.poll(() => cursors.length).toBeGreaterThan(0);
  await expect(focusStatus(page)).toContainText("More projects");
  await page
    .getByRole("button", { name: "Show more mappings", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await page
    .getByRole("button", { name: "Show more mappings", exact: true })
    .click();
  await expect(
    page.getByRole("combobox", { name: "Mapping focus", exact: true }),
  ).toContainText("Later match");
  expect(
    cursors.filter(
      (cursor) => cursor === Buffer.from("page39").toString("base64url"),
    ),
  ).toHaveLength(2);
});

for (const [policy, grant] of [
  [false, false],
  [false, true],
  [true, false],
  [true, true],
] as const) {
  test(`sample disclosure marking follows actual events, not the profile flag alone (${policy}/${grant})`, async ({
    page,
  }) => {
    const f = await workspaceFixture(page, { payloadsAllowed: policy, grant });
    await page.goto("/chat");
    await create(page);
    await ask(page);
    await expectComplete(page);
    if (policy && grant)
      await expect(panel(page)).toContainText("Data shared with fixture-model");
    else await expect(panel(page).getByText(/Data shared with/)).toHaveCount(0);
    expect(f.offered[0]!.includes("get_preview_output")).toBe(policy && grant);
    await expect(panel(page)).toContainText(
      policy ? "current payload-read grant" : "Metadata only",
    );
  });
}

test("each send observes fresh authority and rotated tokens; grant loss cannot reuse a previous turn's disclosure", async ({
  page,
}) => {
  const f = await workspaceFixture(page, {
    payloadsAllowed: true,
    grant: true,
  });
  await page.goto("/chat");
  await create(page);
  await ask(page);
  await expect(panel(page)).toContainText("Data shared with fixture-model");
  f.state.grant = false;
  f.data.context.requestToken = "fresh-catalog-request-token";
  const receiptRead = page.waitForResponse((response) =>
    response.url().endsWith("/mapping-writes"),
  );
  await ask(page, "Read again after my grant changed.");
  await receiptRead;
  await expectComplete(page);
  await expect(
    panel(page)
      .getByRole("region", { name: "Selected answer" })
      .getByText(/Data shared with/),
  ).toHaveCount(0);
  expect(f.offered.at(-1)).not.toContain("get_preview_output");
  expect(f.posts.at(-1)!.token).toBe("fresh-catalog-request-token");
  const count = f.posts.length;
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("This must not be sent as a different owner.");
  f.data.context.actorPrincipalId = "b0000000-0000-4000-8000-000000000099";
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect(panel(page)).not.toContainText(
    "Partner orders maps the order fields.",
  );
  expect(f.posts).toHaveLength(count);
});

test("unknown create and send outcomes recover only the original IDs; reconnect and cancel never resend a question", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.loseCreate = true;
  await page.goto("/chat");
  await create(page, false);
  await panel(page)
    .getByRole("button", { name: "Recover conversation creation", exact: true })
    .click();
  await expect(
    panel(page).getByLabel("Question", { exact: true }),
  ).toBeVisible();
  expect(f.posts[0]!.body).toEqual(f.posts[1]!.body);
  f.state.loseSend = true;
  await ask(page, "Recover this exact original question.");
  await panel(page)
    .getByRole("button", { name: "Retry original question", exact: true })
    .click();
  await expectComplete(page);
  expect(f.posts[2]!.body).toEqual(f.posts[3]!.body);
  f.state.partial = true;
  await ask(page, "A question whose stream disconnects.");
  await expect(panel(page)).toContainText("Connection interrupted.");
  const sent = f.posts.length;
  f.state.partial = false;
  await panel(page)
    .getByRole("button", { name: "Reconnect answer", exact: true })
    .click();
  await expectComplete(page);
  expect(f.posts).toHaveLength(sent);
  f.state.partial = true;
  await ask(page, "Cancel this answer.");
  await expect(panel(page)).toContainText("Connection interrupted.");
  await panel(page)
    .getByRole("button", { name: "Cancel answer", exact: true })
    .click();
  await expect(panel(page)).toContainText("Answer cancelled.");
  expect(f.posts.at(-1)!.path.endsWith("/cancel")).toBe(true);
});

test("foreign workspace citations are refused before link or answer adoption", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.foreignCitation = true;
  await page.goto("/chat");
  await create(page);
  await ask(page);
  await expect(panel(page)).toContainText("No unverified content was accepted");
  await expect(
    panel(page).getByRole("link", { name: "Open mapping", exact: true }),
  ).toHaveCount(0);
  await expect(panel(page)).not.toContainText(
    "Partner orders maps the order fields.",
  );
});

test("content navigation preserves unsent chat without a discard warning", async ({
  page,
}) => {
  await workspaceFixture(page);
  await page.goto("/chat");
  await create(page);
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Unsent private draft");
  let count = 0;
  page.on("dialog", async (dialog) => {
    count++;
    await dialog.dismiss();
  });
  await page
    .getByRole("navigation", { name: "Workspace", exact: true })
    .getByRole("link", { name: "Catalog", exact: true })
    .click();
  await expect(page).toHaveURL(/\/catalog/);
  expect(count).toBe(0);
  await expect(panel(page).getByLabel("Question", { exact: true })).toHaveValue(
    "Unsent private draft",
  );
  await panel(page).getByLabel("Question", { exact: true }).fill("");
});

test("capture sanitized chat page and workbench at a fixed viewport", async ({
  page,
}) => {
  const dir = process.env.WEFT_CHAT_CAPTURE_DIR;
  test.skip(
    !dir,
    "Screenshots are captured only in an explicit full-lock run.",
  );
  await mkdir(dir!, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  const f = await workspaceFixture(page);
  await page.goto("/chat");
  if (process.env.WEFT_CHAT_CAPTURE_BASE !== "true") {
    await create(page);
    await ask(page);
    await expectComplete(page);
  }
  await page.screenshot({ path: `${dir}/chat.png`, fullPage: true });
  await page.goto(f.data.browserPath);
  await create(page);
  await ask(page);
  await expectComplete(page);
  const resize = page.getByRole("separator", {
    name: "Resize chat",
  });
  await resize.focus();
  await resize.press("End");
  await panel(page)
    .getByRole("region", { name: "Selected answer" })
    .scrollIntoViewIfNeeded();
  await resize.focus();
  await page.screenshot({ path: `${dir}/workbench-chat.png`, fullPage: true });
});
