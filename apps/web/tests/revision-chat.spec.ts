import { expect, test, type Page } from "@playwright/test";
import { controlledData, P, V } from "./revision-reports-controlled.fixture";
import { catalogApi, projectPath, mappingPath } from "./catalog.fixture";
import type {
  ChatEvent,
  ChatTurn,
  ChatConversation,
} from "../src/chat/chat-types";
import { reconcileTurn } from "../src/chat/chat-types";

const C = "80000000-0000-4000-8000-000000000001";
const T = "90000000-0000-4000-8000-000000000001";
const R = "a0000000-0000-4000-8000-000000000001";

test("a delayed cancellation status cannot reopen an already terminal turn", () => {
  const active: ChatTurn = {
    turnId: T,
    conversationId: C,
    phase: "active",
    text: "Question",
    selectors: [],
    profile: {
      profileId: "local",
      version: 1,
      providerId: "fixture",
      modelId: "test-model",
    },
    createdAtUtc: "2026-01-01T00:00:00Z",
    deadlineUtc: "2026-01-01T00:02:00Z",
    finishedAtUtc: null,
    lastSequence: 1,
  };
  for (const phase of [
    "completed",
    "failed",
    "cancelled",
    "interrupted",
  ] as const) {
    const terminal = {
      ...active,
      phase,
      finishedAtUtc: "2026-01-01T00:01:00Z",
      lastSequence: 3,
    };
    expect(reconcileTurn(terminal, active)).toBe(terminal);
    expect(reconcileTurn(active, terminal)).toBe(terminal);
  }
});
const frame = (event: ChatEvent) =>
  `id: ${event.sequence}\nevent: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`;

async function fixture(
  page: Page,
  options: { enabled?: boolean; profiles?: boolean; session?: boolean } = {},
) {
  const data = controlledData();
  data.context.features.chat = options.enabled !== false;
  data.mapping.access.actions.chat = true;
  let conversation: ChatConversation | null = null;
  let turns: ChatTurn[] = [];
  const posts: { path: string; body: unknown; token?: string }[] = [];
  const reads: string[] = [];
  const streams: string[] = [];
  let responseEvents: ChatEvent[] = [
    { type: "turn.started", sequence: 1 },
    {
      type: "text.delta",
      sequence: 2,
      roundId: R,
      text: "This maps the saved value.",
    },
    { type: "turn.completed", sequence: 3 },
  ];
  let error: { status: number; code: string } | null = null;
  let failSend = false;
  let streamOverride: string | null = null;
  const availability = {
    schemaVersion: "weft.chat.v1",
    revision: data.revision.scope,
    toolsEnabled: true,
    limits: { maxTextBytes: 32768, maxSelectors: 32, turnSeconds: 120 },
    profiles:
      options.profiles === false
        ? []
        : [
            {
              profileId: "local",
              version: 1,
              displayName: "Local model",
              providerId: "fixture",
              modelId: "test-model",
              supportsTools: true,
            },
          ],
  };
  const makeConversation = (id: string): ChatConversation => ({
    conversationId: id,
    revision: data.revision.scope,
    createdAtUtc: "2026-01-01T00:00:00Z",
    retainUntilUtc: "2027-01-01T00:00:00Z",
    projectionDerivationId: P,
    validationDerivationId: V,
    title: "Saved mapping question",
    turnCount: 0,
    lastTurnAtUtc: null,
  });
  await page.route("**/api/**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname;
    if (request.method() === "POST")
      posts.push({
        path,
        body: request.postDataJSON(),
        token: request.headers()["x-xsrf-token"],
      });
    else reads.push(path);
    if (path === "/api/auth/session")
      return route.fulfill({
        json: options.session
          ? {
              version: 1,
              mode: "session",
              state: "anonymous",
              authActionRequestToken: "YXV0aC1h",
              requestToken: null,
              identityContext: null,
            }
          : { version: 1, mode: "development" },
      });
    if (path === "/api/v1/catalog/context")
      return route.fulfill({ json: data.context });
    if (path === catalogApi(projectPath(data.project.scope)))
      return route.fulfill({ json: data.project });
    if (path === catalogApi(mappingPath(data.mapping.scope)))
      return route.fulfill({ json: data.mapping });
    if (path === data.apiPath) return route.fulfill({ json: data.revision });
    if (path.startsWith(data.reportPath("projection")))
      return route.fulfill({ json: data.projection });
    if (path.startsWith(data.reportPath("validation"))) {
      const validation = structuredClone(data.validation);
      if (url.searchParams.get("projectionDerivationId") === P) {
        validation.projectionDerivationId = P;
        validation.report!.diagnostics[0]!.projectionLocator = {
          kind: "rule",
          ruleId: "controlled-rule",
        };
      }
      return route.fulfill({ json: validation });
    }
    const root = `${data.apiPath}/chat`;
    if (error && path.startsWith(root))
      return route.fulfill({
        status: error.status,
        json: { code: error.code },
      });
    if (path === root) return route.fulfill({ json: availability });
    if (path === `${root}/conversations`) {
      if (request.method() === "POST") {
        conversation = makeConversation(
          request.postDataJSON().conversationId as string,
        );
        return route.fulfill({ status: 201, json: conversation });
      }
      return route.fulfill({
        json: {
          conversations: conversation ? [conversation] : [],
          hasMore: false,
        },
      });
    }
    if (path.endsWith("/events")) {
      streams.push(url.search);
      const after = Number(url.searchParams.get("after") ?? "0");
      return route.fulfill({
        contentType: "text/event-stream",
        body:
          streamOverride ??
          responseEvents
            .filter((e) => e.sequence > after)
            .map(frame)
            .join(""),
      });
    }
    if (path.endsWith("/cancel")) {
      turns = turns.map((t) => ({
        ...t,
        phase: "cancelled",
        finishedAtUtc: "2026-01-01T00:01:00Z",
      }));
      responseEvents.push({
        type: "turn.cancelled",
        sequence: responseEvents.at(-1)!.sequence + 1,
      });
      return route.fulfill({ json: turns.at(-1) });
    }
    if (path.endsWith("/turns") && request.method() === "POST") {
      const body = request.postDataJSON() as { turnId: string; text: string };
      turns = [
        {
          turnId: body.turnId,
          conversationId: conversation!.conversationId,
          phase: "active",
          text: body.text,
          profile: {
            profileId: "local",
            version: 1,
            providerId: "fixture",
            modelId: "test-model",
          },
          selectors: [],
          createdAtUtc: "2026-01-01T00:00:00Z",
          deadlineUtc: "2026-01-01T00:02:00Z",
          finishedAtUtc: null,
          lastSequence: 1,
        },
      ];
      if (failSend) {
        failSend = false;
        return route.abort("failed");
      }
      return route.fulfill({ status: 201, json: turns[0] });
    }
    if (
      conversation &&
      path === `${root}/conversations/${conversation.conversationId}`
    )
      return route.fulfill({
        json: { conversation, turns, hasEarlierTurns: false },
      });
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  return {
    data,
    posts,
    reads,
    streams,
    events: (events: ChatEvent[]) => {
      responseEvents = events;
    },
    error: (value: typeof error) => {
      error = value;
    },
    loseSend: () => {
      failSend = true;
    },
    wire: (value: string) => {
      streamOverride = value;
    },
    saved: () => {
      conversation = makeConversation(C);
      turns = [
        {
          turnId: T,
          conversationId: C,
          phase: "completed",
          text: "Earlier question",
          profile: {
            profileId: "local",
            version: 1,
            providerId: "fixture",
            modelId: "test-model",
          },
          selectors: [],
          createdAtUtc: "2026-01-01T00:00:00Z",
          deadlineUtc: "2026-01-01T00:02:00Z",
          finishedAtUtc: "2026-01-01T00:01:00Z",
          lastSequence: 3,
        },
      ];
    },
  };
}
const panel = (page: Page) =>
  page.getByRole("complementary", { name: "Mapping chat" });

async function holdSessionCheck(page: Page) {
  let release!: () => void;
  let entered!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const arrived = new Promise<void>((resolve) => {
    entered = resolve;
  });
  await page.route("**/api/auth/session", async (route) => {
    entered();
    await gate;
    await route.fulfill({ json: { version: 1, mode: "development" } });
  });
  return { release, arrived };
}
async function start(page: Page) {
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("How does this map?");
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
}

test("creates, streams, and reopens a private revision conversation", async ({
  page,
}) => {
  const f = await fixture(page);
  const observed = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/auth/session",
  );
  await page.goto(f.data.browserPath);
  expect(await (await observed).json()).toEqual({
    version: 1,
    mode: "development",
  });
  await expect.poll(() => f.reads).toContain("/api/auth/session");
  await start(page);
  await expect(panel(page)).toContainText("This maps the saved value.");
  await expect(panel(page)).toContainText("Answer complete.");
  expect(f.posts).toHaveLength(2);
  expect(f.posts.every((p) => p.token === f.data.context.requestToken)).toBe(
    true,
  );
  await panel(page)
    .getByRole("button", { name: "Refresh chat", exact: true })
    .click();
  await panel(page)
    .getByLabel("Saved conversations")
    .selectOption({ label: "Saved mapping question" });
  await expect(panel(page)).toContainText("This maps the saved value.");
  expect(f.posts).toHaveLength(2);
});

test("chat admission waits for the page's initial report reads", async ({
  page,
}) => {
  const f = await fixture(page);
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(`**${f.data.reportPath("projection")}`, async (route) => {
    await held;
    await route.fulfill({ json: f.data.projection });
  });
  try {
    await page.goto(f.data.browserPath);
    await expect(
      panel(page).getByText(
        "Waiting for the initial report reads before loading chat.",
      ),
    ).toBeVisible();
    await expect(
      panel(page).getByRole("button", { name: "Refresh chat", exact: true }),
    ).toBeDisabled();
    expect(f.reads.filter((path) => path.endsWith("/chat"))).toHaveLength(0);
    release();
    await expect(
      panel(page).getByRole("button", {
        name: "New conversation",
        exact: true,
      }),
    ).toBeEnabled();
  } finally {
    release();
  }
});

for (const failed of ["projection", "validation"] as const) {
  test(`an initial ${failed} refusal cancels its sibling without blocking chat forever`, async ({
    page,
  }) => {
    const f = await fixture(page);
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const sibling = failed === "projection" ? "validation" : "projection";
    await page.route(`**${f.data.reportPath(sibling)}`, async (route) => {
      await gate;
      try {
        await route.fulfill({ json: f.data[sibling] });
      } catch (error) {
        if (!route.request().failure()) throw error;
      }
    });
    await page.route(`**${f.data.reportPath(failed)}`, (route) =>
      route.fulfill({ status: 404, json: { code: "notFound" } }),
    );
    try {
      await page.goto(f.data.browserPath);
      await expect(
        panel(page).getByRole("button", {
          name: "New conversation",
          exact: true,
        }),
      ).toBeEnabled();
      await expect(
        panel(page).getByRole("button", { name: "Refresh chat", exact: true }),
      ).toBeEnabled();
      await expect(panel(page)).not.toContainText(
        "Waiting for the initial report reads",
      );
    } finally {
      release();
    }
  });
}

test("keyboard creation restores focus to the composer after a delayed session check", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  const create = panel(page).getByRole("button", {
    name: "New conversation",
    exact: true,
  });
  await expect(create).toBeEnabled();
  const held = await holdSessionCheck(page);
  try {
    await create.focus();
    await page.keyboard.press("Enter");
    await held.arrived;
    await expect(create).not.toBeVisible();
    held.release();
    await expect(
      panel(page).getByLabel("Question", { exact: true }),
    ).toBeFocused();
  } finally {
    held.release();
  }
});

test("keyboard question selection keeps focus after reading session admission", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText("Answer complete.");
  const selected = panel(page).getByRole("button", {
    name: "How does this map?",
    exact: true,
  });
  const held = await holdSessionCheck(page);
  try {
    await selected.focus();
    await page.keyboard.press("Enter");
    await held.arrived;
    held.release();
    await expect(selected).toBeFocused();
    await expect(panel(page)).toContainText("Answer complete.");
  } finally {
    held.release();
  }
});

test("an unacknowledged keyboard send focuses explicit recovery after admission", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Recover this exact question");
  f.loseSend();
  const held = await holdSessionCheck(page);
  try {
    await panel(page)
      .getByRole("button", { name: "Send question", exact: true })
      .focus();
    await page.keyboard.press("Enter");
    await held.arrived;
    held.release();
    await expect(
      panel(page).getByRole("button", {
        name: "Recover original question",
        exact: true,
      }),
    ).toBeFocused();
    expect(f.posts).toHaveLength(2);
  } finally {
    held.release();
  }
});

test("admission completion does not steal deliberate focus outside chat", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  const create = panel(page).getByRole("button", {
    name: "New conversation",
    exact: true,
  });
  await expect(create).toBeEnabled();
  const held = await holdSessionCheck(page);
  try {
    await create.focus();
    await page.keyboard.press("Enter");
    await held.arrived;
    const examples = page
      .getByRole("navigation", { name: "Workspace", exact: true })
      .getByRole("link", { name: "Examples", exact: true });
    await examples.focus();
    held.release();
    await expect(
      panel(page).getByLabel("Question", { exact: true }),
    ).toBeVisible();
    await expect(examples).toBeFocused();
  } finally {
    held.release();
  }
});

test("unavailable report reading does not leave chat waiting for a nonexistent request", async ({
  page,
}) => {
  const f = await fixture(page);
  f.data.mapping.access.actions.readReports = false;
  f.data.mapping.access.actions.deriveReports = false;
  await page.goto(f.data.browserPath);
  await expect(
    panel(page).getByRole("button", { name: "New conversation", exact: true }),
  ).toBeEnabled();
  await expect(
    panel(page).getByText(
      "Waiting for the initial report reads before loading chat.",
    ),
  ).toHaveCount(0);
  expect(f.reads.filter((path) => path.includes("/reports/"))).toHaveLength(0);
});

test("resumes a dropped stream after its last event without a second POST", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([
    { type: "turn.started", sequence: 1 },
    { type: "text.delta", sequence: 2, roundId: R, text: "First " },
  ]);
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText("Connection interrupted.");
  f.events([
    { type: "text.delta", sequence: 4, roundId: R, text: "second." },
    { type: "turn.completed", sequence: 6 },
  ]);
  await panel(page).getByRole("button", { name: "Reconnect answer" }).click();
  await expect(panel(page)).toContainText("First second.");
  expect(f.streams).toEqual(["?after=0", "?after=2"]);
  expect(f.posts).toHaveLength(2);
});

test("unknown POST outcome only replays the original question deliberately", async ({
  page,
}) => {
  const f = await fixture(page);
  f.loseSend();
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(
    panel(page).getByRole("button", { name: "Recover original question" }),
  ).toBeVisible();
  expect(f.posts).toHaveLength(2);
  await panel(page)
    .getByRole("button", { name: "Recover original question" })
    .click();
  await expect(panel(page)).toContainText("Answer complete.");
  expect(f.posts).toHaveLength(3);
  expect(f.posts[1]!.body).toEqual(f.posts[2]!.body);
});

test("cancel is explicit and the stream records cancellation", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([{ type: "turn.started", sequence: 1 }]);
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText("Connection interrupted.");
  await panel(page).getByRole("button", { name: "Cancel answer" }).click();
  await expect(panel(page)).toContainText("Answer cancelled.");
  await expect(
    panel(page).getByLabel("Question", { exact: true }),
  ).toBeFocused();
  expect(f.posts.filter((p) => p.path.endsWith("/cancel"))).toHaveLength(1);
});

test("citations select the exact report's field without changing the revision URL", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([
    { type: "turn.started", sequence: 1 },
    {
      type: "citation",
      sequence: 2,
      roundId: R,
      reportDerivationId: P,
      target: {
        kind: "field",
        resourceId: "controlled-field",
        diagnosticOrdinal: null,
      },
    },
    { type: "turn.completed", sequence: 3 },
  ]);
  await page.goto(f.data.browserPath);
  await start(page);
  const url = page.url();
  await panel(page)
    .getByRole("button", { name: "Show field controlled-field" })
    .click();
  await expect(
    page.getByRole("heading", {
      name: 'Controlled output ["savedValue"]',
      exact: true,
    }),
  ).toBeVisible();
  expect(page.url()).toBe(url);
});

test("a citation with another report pin cannot select viewer resources", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([
    {
      type: "citation",
      sequence: 2,
      roundId: R,
      reportDerivationId: V,
      target: {
        kind: "field",
        resourceId: "controlled-field",
        diagnosticOrdinal: null,
      },
    },
    { type: "turn.completed", sequence: 3 },
  ]);
  await page.goto(f.data.browserPath);
  await start(page);
  await panel(page)
    .getByRole("button", { name: "Show field controlled-field" })
    .click();
  await expect(
    page.getByText(
      "This citation belongs to a saved report that is not displayed.",
      { exact: false },
    ),
  ).toBeVisible();
});

for (const kind of [
  "rule",
  "projectionDiagnostic",
  "validationDiagnostic",
] as const) {
  test(`citation selects an exact ${kind} without navigation`, async ({
    page,
  }) => {
    const f = await fixture(page);
    f.data.projection.report!.diagnostics = [
      {
        code: "notice",
        message: "Operation-only notice",
        messageTruncated: false,
        severity: "warning",
        nativeLocator: null,
      },
    ];
    f.events([
      {
        type: "citation",
        sequence: 1,
        roundId: R,
        reportDerivationId: kind === "validationDiagnostic" ? V : P,
        target: {
          kind,
          resourceId: kind === "rule" ? "controlled-rule" : null,
          diagnosticOrdinal: kind === "rule" ? null : 0,
        },
      },
      { type: "turn.completed", sequence: 2 },
    ]);
    await page.goto(f.data.browserPath);
    await start(page);
    const url = page.url();
    await panel(page)
      .getByRole("button", { name: new RegExp(`^Show ${kind}`) })
      .click();
    if (kind === "rule")
      await expect(
        page.getByRole("heading", {
          name: "Controlled saved explanation",
          exact: true,
        }),
      ).toBeVisible();
    else
      await expect(
        page.locator(
          `[data-chat-${kind === "projectionDiagnostic" ? "projection" : "validation"}-diagnostic="0"]`,
        ),
      ).toBeFocused();
    expect(page.url()).toBe(url);
  });
}

test("a saved conversation reopens after reload without another turn POST", async ({
  page,
}) => {
  const f = await fixture(page);
  f.saved();
  await page.goto(f.data.browserPath);
  await panel(page).getByLabel("Saved conversations").selectOption(C);
  await expect(panel(page)).toContainText("Answer complete.");
  await page.reload();
  await panel(page).getByLabel("Saved conversations").selectOption(C);
  await expect(panel(page)).toContainText("This maps the saved value.");
  expect(f.posts).toHaveLength(0);
});

test("answer markup remains literal text", async ({ page }) => {
  const f = await fixture(page);
  f.events([
    {
      type: "text.delta",
      sequence: 1,
      roundId: R,
      text: '<img src=x onerror="alert(1)">',
    },
    { type: "turn.completed", sequence: 2 },
  ]);
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText('<img src=x onerror="alert(1)">');
  await expect(panel(page).locator("img")).toHaveCount(0);
});

test("identity changes clear private chat instead of adopting another owner's context", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText("This maps the saved value.");
  f.data.context.actorPrincipalId = "b0000000-0000-4000-8000-000000000001";
  await panel(page)
    .getByRole("button", { name: "Refresh chat", exact: true })
    .click();
  await expect(panel(page)).not.toContainText("This maps the saved value.");
  await expect(panel(page).getByLabel("Question", { exact: true })).toHaveCount(
    0,
  );
  expect(f.posts).toHaveLength(2);
  expect(
    await page.evaluate(() => ({
      local: localStorage.length,
      session: sessionStorage.length,
    })),
  ).toEqual({ local: 0, session: 0 });
});

/** A focus refresh whose session check fails, then the shell and page Retry controls. */
async function failSessionCheck(page: Page) {
  const workspaceAccess = page.getByRole("alert").filter({
    has: page.getByRole("heading", {
      name: "Workspace access unavailable",
      exact: true,
    }),
  });
  await page.route(
    "**/api/auth/session*",
    (route) => route.fulfill({ status: 503, json: { code: "unavailable" } }),
    { times: 1 },
  );
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(workspaceAccess).toBeVisible();
  await expect(panel(page)).toHaveCount(0);
  await workspaceAccess
    .getByRole("button", { name: "Retry", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Retry", exact: true }).click();
}

async function unsentQuestion(page: Page) {
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Which rule sets the region?");
}

test("an unsent question survives a failed session check", async ({ page }) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  await unsentQuestion(page);
  await failSessionCheck(page);
  await expect(panel(page).getByLabel("Question", { exact: true })).toHaveValue(
    "Which rule sets the region?",
  );
  expect(f.posts).toHaveLength(1);
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect(panel(page)).toContainText("Answer complete.");
  expect(f.posts).toHaveLength(2);
  expect((f.posts[1]!.body as { text: string }).text).toBe(
    "Which rule sets the region?",
  );
});

test("an unacknowledged question keeps its original request across a failed session check", async ({
  page,
}) => {
  const f = await fixture(page);
  f.loseSend();
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(
    panel(page).getByRole("button", { name: "Recover original question" }),
  ).toBeVisible();
  await failSessionCheck(page);
  await expect(
    panel(page).getByRole("button", { name: "Recover original question" }),
  ).toBeVisible();
  expect(f.posts).toHaveLength(2);
  // Reopening the conversation shows the committed turn; only the explicit resend confirms the request.
  await panel(page)
    .getByRole("button", { name: "Recover original question" })
    .click();
  await expect.poll(() => f.posts.length).toBe(3);
  expect(f.posts[1]!.body).toEqual(f.posts[2]!.body);
  await expect(
    panel(page).getByRole("button", { name: "Recover original question" }),
  ).toHaveCount(0);
  await expect(panel(page)).toContainText("Answer complete.");
});

test("a different actor after a failed session check clears the unsent question", async ({
  page,
}) => {
  const f = await fixture(page);
  await page.goto(f.data.browserPath);
  await unsentQuestion(page);
  f.data.context.actorPrincipalId = "b0000000-0000-4000-8000-000000000001";
  await failSessionCheck(page);
  await expect(
    panel(page).getByRole("button", { name: "New conversation", exact: true }),
  ).toBeEnabled();
  await expect(panel(page).getByLabel("Question", { exact: true })).toHaveCount(
    0,
  );
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await expect(panel(page).getByLabel("Question", { exact: true })).toHaveValue(
    "",
  );
});

test("tool activity and per-round usage snapshots do not sum observations", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([
    {
      type: "tool.started",
      sequence: 1,
      roundId: R,
      ordinal: 1,
      name: "get_mapping_summary",
    },
    {
      type: "tool.completed",
      sequence: 2,
      roundId: R,
      ordinal: 1,
      name: "get_mapping_summary",
      resultBytes: 128,
      retrievedNodes: 1,
    },
    {
      type: "usage",
      sequence: 3,
      roundId: R,
      origin: "observation",
      inputTokens: 10,
      outputTokens: 5,
      totalTokens: 15,
    },
    {
      type: "usage",
      sequence: 4,
      roundId: R,
      origin: "roundTerminal",
      inputTokens: 10,
      outputTokens: 8,
      totalTokens: null,
    },
    { type: "turn.completed", sequence: 5 },
  ]);
  await page.goto(f.data.browserPath);
  await start(page);
  await panel(page)
    .getByText("Token usage (latest snapshot per round)")
    .click();
  await expect(panel(page)).toContainText(
    "Input 10 · Output 8 · Total unknown",
  );
  await panel(page)
    .getByText("Read-only tool activity", { exact: true })
    .click();
  await expect(panel(page)).toContainText("get_mapping_summary — completed");
});

for (const mode of [
  "disabled",
  "profiles",
  "missingReports",
  "rejected",
] as const) {
  test(`explicit unavailable state: ${mode}`, async ({ page }) => {
    const f = await fixture(page, {
      enabled: mode !== "disabled",
      profiles: mode !== "profiles",
    });
    await page.goto(f.data.browserPath);
    if (mode === "missingReports") {
      await expect(
        panel(page).getByRole("button", {
          name: "New conversation",
          exact: true,
        }),
      ).toBeEnabled();
      f.error({ status: 409, code: "reportsUnavailable" });
      await panel(page)
        .getByRole("button", { name: "New conversation", exact: true })
        .click();
      await expect(panel(page)).toContainText(
        "Generate the saved explanation and validation",
      );
    } else if (mode === "rejected") {
      await expect(
        panel(page).getByRole("button", {
          name: "New conversation",
          exact: true,
        }),
      ).toBeEnabled();
      f.error({ status: 403, code: "identityRejected" });
      await panel(page)
        .getByRole("button", { name: "New conversation", exact: true })
        .click();
      await expect(panel(page)).toContainText("The session was rejected");
    } else if (mode === "profiles")
      await expect(panel(page)).toContainText("No selectable model profile");
    else {
      await expect(panel(page)).toContainText("Chat is not configured");
      expect(f.reads.filter((p) => p.endsWith("/chat"))).toHaveLength(0);
    }
  });
}

test("signed-out session cannot load private chat", async ({ page }) => {
  const f = await fixture(page, { session: true });
  await page.goto(f.data.browserPath);
  await expect(page.getByText("Not signed in", { exact: true })).toBeVisible();
  expect(f.reads.filter((p) => p.includes("/chat"))).toHaveLength(0);
});

test("malformed event framing is refused and no HTML is executed", async ({
  page,
}) => {
  const f = await fixture(page);
  f.wire(
    'id: 2\nevent: text.delta\ndata: {"type":"text.delta","sequence":3,"roundId":"' +
      R +
      '","text":"<img src=x onerror=alert(1)>"}\n\n',
  );
  await page.goto(f.data.browserPath);
  await start(page);
  await expect(panel(page)).toContainText("could not be verified");
  await expect(panel(page).locator("img")).toHaveCount(0);
});

test("provider failure is explicit and keyboard/mobile controls fit", async ({
  page,
}) => {
  const f = await fixture(page);
  f.events([
    {
      type: "turn.failed",
      sequence: 1,
      reason: "provider",
      providerFailure: "providerUnavailable",
    },
  ]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(f.data.browserPath);
  const create = panel(page).getByRole("button", {
    name: "New conversation",
    exact: true,
  });
  await expect(create).toBeEnabled();
  await create.focus();
  await page.keyboard.press("Enter");
  await panel(page).getByLabel("Question", { exact: true }).fill("Explain");
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .focus();
  await page.keyboard.press("Enter");
  await expect(panel(page)).toContainText(
    "Answer failed: provider (providerUnavailable).",
  );
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
