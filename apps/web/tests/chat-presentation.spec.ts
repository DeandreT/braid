import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import type { ChatEvent } from "../src/chat/chat-types";
import {
  workspaceFixture,
  legacyId,
  otherMapping,
  frame,
  round,
} from "./workspace-chat.fixture";
import {
  chooseSavedConversation,
  historyTrigger,
  openFocus,
  openHistory,
  openMore,
  refreshChat,
} from "./chat-ui.fixture";

const chat = (page: Page) => page.locator("#workspace-chat");
async function captureReview(page: Page, name: string) {
  const directory = process.env.WEFT_CHAT_REVIEW_CAPTURE_DIR;
  if (!directory) return;
  await mkdir(directory, { recursive: true });
  await chat(page).screenshot({ path: `${directory}/${name}-panel.png` });
  await page.screenshot({ path: `${directory}/${name}-page.png` });
}
async function start(page: Page) {
  const fixture = await workspaceFixture(page, { secondProfile: true });
  await page.goto("/chat");
  await chat(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await expect(
    chat(page).getByLabel("Question", { exact: true }),
  ).toBeVisible();
  return fixture;
}
async function ask(page: Page, text: string) {
  const turns = chat(page).locator(".chat-turn");
  const previous = (await turns.count())
    ? await turns.last().getAttribute("data-turn-id")
    : null;
  await chat(page).getByLabel("Question", { exact: true }).fill(text);
  await chat(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  // An earlier selected answer can still be complete until this send is acknowledged.
  if (previous)
    await expect(turns.last()).not.toHaveAttribute("data-turn-id", previous);
  await expect(turns.last().locator(".chat-bubble")).toHaveText(text);
  await expect(
    chat(page)
      .getByRole("region", { name: "Selected answer" })
      .getByRole("status"),
  ).toHaveText("Answer complete.");
}

test("one transcript keeps each question, answer, tools and details, then replays retained answers without sending", async ({
  page,
}) => {
  const fixture = await start(page);
  await ask(page, "First question");
  await ask(page, "Second question");
  const transcript = chat(page).getByRole("list", {
    name: "Conversation transcript",
  });
  await expect(transcript.locator(".chat-turn")).toHaveCount(2);
  await expect(transcript.locator(".chat-answer-text")).toHaveText([
    "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
    "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
  ]);
  // Only the selected turn announces, and no live region is nested in another.
  await expect(transcript.getByRole("status")).toHaveCount(1);
  await expect(
    chat(page)
      .getByRole("region", { name: "Selected answer" })
      .getByRole("status"),
  ).toHaveText("Answer complete.");
  expect(
    await transcript.evaluate(
      (list) =>
        list.querySelectorAll(
          ':is([aria-live]:not([aria-live="off"]), [role="status"], [role="alert"]) :is([aria-live]:not([aria-live="off"]), [role="status"], [role="alert"])',
        ).length,
    ),
  ).toBe(0);
  const first = transcript.locator(".chat-turn").first();
  await first
    .getByRole("button", { name: "Checked 1 source", exact: true })
    .click();
  await expect(
    first.getByRole("list", { name: "Read-only tool activity" }),
  ).toContainText("get_mapping · completed");
  await first
    .getByRole("button", { name: "Answer details", exact: true })
    .focus();
  await first
    .getByRole("button", { name: "Answer details", exact: true })
    .press("Enter");
  await expect(first.locator(".chat-details")).toBeVisible();
  await expect(first.locator(".chat-details")).toContainText("fixture-model");
  await first.locator(".chat-bubble").click();
  await expect(first.getByRole("status")).toHaveText("Answer complete.");
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(2);
  // This fixture does not persist the SSE phase changes itself. Model the retained server result.
  for (const turns of fixture.turns.values())
    for (const turn of turns) {
      turn.phase = "completed";
      turn.finishedAtUtc = "2026-10-24T10:01:00Z";
    }
  await page.reload();
  await expect(transcript.locator(".chat-answer-text")).toHaveText([
    "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
    "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
  ]);
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(2);
});

test("history is searchable, groups today and earlier, pages older chats and returns keyboard focus", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  const earlier = fixture.conversations.get(legacyId)!;
  const today = {
    ...earlier,
    conversationId: "80000000-0000-4000-8000-000000000002",
    title: "Today question",
    createdAtUtc: new Date().toISOString(),
  };
  const older = {
    ...earlier,
    conversationId: "80000000-0000-4000-8000-000000000003",
    title: "Old archived question",
  };
  let pages = 0;
  await page.route(`**${fixture.root}/conversations?*`, (route) => {
    pages++;
    return route.fulfill({
      json: new URL(route.request().url()).searchParams.has("after")
        ? { conversations: [older], hasMore: false }
        : { conversations: [today, earlier], hasMore: true },
    });
  });
  await page.goto("/chat");
  const trigger = historyTrigger(chat(page));
  await trigger.focus();
  await trigger.press("Enter");
  const history = chat(page).locator("#chat-history-menu");
  await expect(history.getByRole("searchbox")).toBeFocused();
  await expect(history).toContainText("Today");
  await expect(history).toContainText("Earlier");
  await history.getByRole("searchbox").fill("revision");
  await expect(
    history.getByRole("button", { name: /Today question/ }),
  ).toHaveCount(0);
  await expect(
    history.getByRole("button", { name: /Earlier revision conversation/ }),
  ).toBeVisible();
  await history.getByRole("searchbox").fill("");
  await history
    .getByRole("button", { name: "Show older chats", exact: true })
    .click();
  await expect(
    history.getByRole("button", { name: /Old archived question/ }),
  ).toBeVisible();
  expect(pages).toBe(2);
  await page.keyboard.press("Escape");
  await expect(history).toBeHidden();
  await expect(trigger).toBeFocused();
  await chooseSavedConversation(chat(page), legacyId);
  await expect(chat(page).locator(".chat-context")).toContainText(
    "Pinned version",
  );
  await expect(trigger).toBeFocused();
});

test("focus choice distinguishes pinned from following, can be removed before creation and cannot rebind a live conversation", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.goto("/chat");
  await openFocus(chat(page));
  await chat(page).getByLabel("Find mapping").fill("Partner");
  await chat(page)
    .getByRole("button", { name: "Search mappings", exact: true })
    .click();
  await chat(page)
    .getByLabel("Mapping focus", { exact: true })
    .selectOption(otherMapping);
  await chat(page).getByRole("button", { name: "Done", exact: true }).click();
  await expect(chat(page).locator(".chat-focus-row")).toContainText(
    "follows newest version",
  );
  await chat(page)
    .getByRole("button", { name: "Remove focus", exact: true })
    .click();
  await expect(
    chat(page).getByRole("button", { name: /\+ Focus/ }),
  ).toBeVisible();
  await openFocus(chat(page));
  await chat(page)
    .getByLabel("Mapping focus", { exact: true })
    .selectOption(otherMapping);
  await chat(page)
    .getByLabel("Revision focus", { exact: true })
    .selectOption(fixture.data.revision.scope.revisionId);
  await chat(page).getByRole("button", { name: "Done", exact: true }).click();
  await chat(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  const chip = chat(page).locator(".chat-context-fixed");
  await expect(chip).toContainText("pinned version");
  await chip.focus();
  await expect(chip.getByRole("tooltip")).toBeVisible();
  await expect(chip.getByRole("tooltip")).toContainText(
    fixture.data.revision.scope.revisionId,
  );
  await expect(
    chat(page).getByRole("button", { name: "Remove focus", exact: true }),
  ).toHaveCount(0);
  await ask(page, "Keep that exact version");
  expect(fixture.posts[0]!.body.focus.revisionId).toBe(
    fixture.data.revision.scope.revisionId,
  );
});

test("composer grows, exposes limits near the UTF-8 bound and keeps the policy on one line", async ({
  page,
}) => {
  const fixture = await start(page);
  const composer = chat(page).getByLabel("Question", { exact: true });
  const height = (await composer.boundingBox())!.height;
  await composer.fill("one\ntwo\nthree\nfour\nfive\nsix");
  await expect
    .poll(async () => (await composer.boundingBox())!.height)
    .toBeGreaterThan(height);
  await expect(chat(page).locator(".chat-limit-near")).toHaveCount(0);
  await composer.fill("é".repeat(14000));
  await expect(chat(page).locator(".chat-limit-near")).toHaveText(
    "28000 of 32768 bytes",
  );
  await composer.fill("é".repeat(17000));
  await chat(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect(
    chat(page).locator(".chat-composer").getByRole("alert"),
  ).toContainText("exceeds the text limit");
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(0);
  const notes = chat(page).getByRole("button", {
    name: "History and limits",
    exact: true,
  });
  await notes.click();
  await expect(chat(page).locator("#chat-composer-notes")).toContainText(
    "sample-data turns stay in your history",
  );
  await expect(chat(page).locator("#chat-composer-notes")).toContainText(
    "most recent 100 questions",
  );
  await page.keyboard.press("Escape");
  await expect(notes).toBeFocused();
  expect(
    await chat(page)
      .locator(".chat-policy")
      .evaluate((element) => getComputedStyle(element).whiteSpace),
  ).toBe("nowrap");
});

test("an unresolved question disables the composer and retries only the original ID and text", async ({
  page,
}) => {
  const fixture = await start(page);
  fixture.state.loseSend = true;
  await chat(page)
    .getByLabel("Question", { exact: true })
    .fill("Original uncertain question");
  await chat(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const retry = chat(page).getByRole("button", {
    name: "Retry original question",
    exact: true,
  });
  await expect(retry).toBeFocused();
  await expect(
    chat(page).getByLabel("Question", { exact: true }),
  ).toBeDisabled();
  await expect(
    chat(page).getByLabel("Question", { exact: true }),
  ).toHaveAttribute("placeholder", "Resolve the question above first");
  await expect(chat(page).locator(".chat-user-pending")).toContainText(
    "Original uncertain question",
  );
  await retry.press("Enter");
  await expect(
    chat(page)
      .getByRole("region", { name: "Selected answer" })
      .getByRole("status"),
  ).toHaveText("Answer complete.");
  const posts = fixture.posts.filter((post) => post.path.endsWith("/turns"));
  expect(posts).toHaveLength(2);
  expect(posts[0]!.body).toEqual(posts[1]!.body);
  await expect(
    chat(page).getByLabel("Question", { exact: true }),
  ).toBeEnabled();
});

test("Stop and Reconnect stay keyboard accessible and neither creates another question", async ({
  page,
}) => {
  const fixture = await start(page);
  fixture.state.partial = true;
  await chat(page)
    .getByLabel("Question", { exact: true })
    .fill("Stop this answer");
  await chat(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const reconnect = chat(page).getByRole("button", {
    name: "Reconnect answer",
    exact: true,
  });
  await expect(reconnect).toBeVisible();
  const stop = chat(page).getByRole("button", {
    name: "Cancel answer",
    exact: true,
  });
  await stop.focus();
  await expect(stop).toBeFocused();
  await stop.press("Enter");
  await expect(chat(page)).toContainText("Answer cancelled.");
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(1);
  expect(fixture.posts.at(-1)!.path).toMatch(/\/cancel$/);
});

test("edge resize supports drag, keyboard, double-click and a per-user remembered width", async ({
  page,
}) => {
  await workspaceFixture(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto("/chat");
  const edge = page.getByRole("separator", {
    name: "Resize chat",
    exact: true,
  });
  await edge.focus();
  await edge.press("ArrowLeft");
  await expect(edge).toHaveAttribute("aria-valuenow", "370");
  await edge.press("Enter");
  await expect(edge).toHaveAttribute("aria-valuenow", "360");
  const box = (await edge.boundingBox())!;
  await page.mouse.move(box.x + 3, box.y + 30);
  await page.mouse.down();
  await page.mouse.move(box.x - 77, box.y + 30);
  await page.mouse.up();
  await expect(edge).toHaveAttribute("aria-valuenow", "440");
  await page.reload();
  await expect(edge).toHaveAttribute("aria-valuenow", "440");
  await edge.dblclick({ position: { x: 3, y: 30 } });
  await expect(edge).toHaveAttribute("aria-valuenow", "360");
  await expect(chat(page).getByRole("slider")).toHaveCount(0);
  await expect(
    chat(page).getByRole("button", { name: "Reset width" }),
  ).toHaveCount(0);
});

test("starters fill an editable question without sending and header actions return focus", async ({
  page,
}) => {
  const fixture = await workspaceFixture(page);
  await page.goto("/chat");
  await chat(page)
    .getByRole("button", {
      name: "How can I investigate a failing test?",
      exact: true,
    })
    .click();
  await expect(chat(page).getByLabel("Question", { exact: true })).toHaveValue(
    "How can I investigate a failing test?",
  );
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(0);
  await openMore(chat(page));
  await expect(
    chat(page).getByRole("link", { name: "Open full page", exact: true }),
  ).toHaveAttribute("href", /\/chat\?conversationId=/);
  await expect(
    chat(page).getByRole("link", { name: /^About chat/ }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(
    chat(page).getByRole("button", { name: "More chat actions", exact: true }),
  ).toBeFocused();
  const scope = chat(page).getByRole("button", { name: /Read-only/ });
  await scope.click();
  await expect(chat(page).locator("#chat-scope-menu")).toContainText(
    "cannot edit mappings or start runs",
  );
  await page.keyboard.press("Escape");
  await expect(scope).toBeFocused();
});

test("answer copy preserves literal text, hover actions remain keyboard reachable and rendered colors retain contrast", async ({
  page,
}) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async (text: string) => {
          (window as unknown as { copiedAnswer: string }).copiedAnswer = text;
        },
      },
    });
  });
  await start(page);
  await ask(page, "Copy the literal answer");
  const answer = chat(page).getByRole("region", { name: "Selected answer" });
  const copy = answer.getByRole("button", { name: "Copy answer", exact: true });
  await copy.focus();
  await expect(copy).toBeFocused();
  await expect(answer.locator(".chat-answer-actions")).toHaveCSS(
    "opacity",
    "1",
  );
  await copy.press("Enter");
  await expect(answer).toContainText("Copied.");
  expect(
    await page.evaluate(
      () => (window as unknown as { copiedAnswer: string }).copiedAnswer,
    ),
  ).toBe(
    "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
  );
  const ratios = await chat(page).evaluate((root) => {
    const luminance = (color: string) => {
      const [r, g, b] = color
        .match(/[\d.]+/g)!
        .slice(0, 3)
        .map((component) => {
          const value = Number(component) / 255;
          return value <= 0.04045
            ? value / 12.92
            : ((value + 0.055) / 1.055) ** 2.4;
        });
      return r! * 0.2126 + g! * 0.7152 + b! * 0.0722;
    };
    return [
      ...root.querySelectorAll(
        ".chat-policy, .chat-chip, .chat-badge, .chat-context-fixed, .chat-answer-text, .chat-steps",
      ),
    ].map((element) => {
      let parent: Element | null = element;
      while (
        parent &&
        getComputedStyle(parent).backgroundColor === "rgba(0, 0, 0, 0)"
      )
        parent = parent.parentElement;
      const foreground = luminance(getComputedStyle(element).color);
      const background = luminance(
        parent ? getComputedStyle(parent).backgroundColor : "rgb(255,255,255)",
      );
      return {
        role: element.className,
        ratio:
          (Math.max(foreground, background) + 0.05) /
          (Math.min(foreground, background) + 0.05),
      };
    });
  });
  for (const { role, ratio } of ratios)
    expect(ratio, role).toBeGreaterThanOrEqual(4.5);
});

for (const changedOwner of [false, true]) {
  test(`review recovery: a failed detail read cannot hide the original pending request (${changedOwner ? "changed owner refuses retry" : "same owner retries explicitly"})`, async ({
    page,
  }) => {
    const fixture = await workspaceFixture(page);
    await page.goto(fixture.data.browserPath);
    if (
      (await page
        .locator("#workspace-chat-toggle")
        .getAttribute("aria-expanded")) === "false"
    )
      await page.locator("#workspace-chat-toggle").click();
    await chat(page)
      .getByRole("button", { name: "New conversation", exact: true })
      .click();
    fixture.state.loseSend = true;
    await chat(page)
      .getByLabel("Question", { exact: true })
      .fill("Keep this exact original request through a failed read");
    await chat(page)
      .getByRole("button", { name: "Send question", exact: true })
      .click();
    const retry = chat(page).getByRole("button", {
      name: "Retry original question",
      exact: true,
    });
    await expect(retry).toBeEnabled();
    const original = fixture.posts.find((post) =>
      post.path.endsWith("/turns"),
    )!;
    const conversationId = original.path.split("/").at(-2)!;
    let failDetail = true,
      detailReads = 0;
    await page.route(
      `**${fixture.root}/conversations/${conversationId}`,
      (route) => {
        detailReads++;
        return failDetail
          ? route.fulfill({ status: 503, json: { code: "unavailable" } })
          : route.fallback();
      },
    );
    await page.route(
      "**/api/auth/session*",
      (route) => route.fulfill({ status: 503, json: { code: "unavailable" } }),
      { times: 1 },
    );
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    const unavailable = page.getByRole("alert").filter({
      has: page.getByRole("heading", {
        name: "Workspace access unavailable",
        exact: true,
      }),
    });
    await expect(unavailable).toBeVisible();
    await unavailable
      .getByRole("button", { name: "Retry", exact: true })
      .click();
    await expect(
      page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Retry", exact: true }).click();
    await expect.poll(() => detailReads).toBeGreaterThan(0);
    await expect(retry).toBeEnabled();
    await expect(chat(page).locator(".chat-user-pending")).toContainText(
      original.body.text,
    );
    await expect(
      chat(page).getByLabel("Question", { exact: true }),
    ).toHaveCount(0);
    expect(
      fixture.posts.filter((post) => post.path.endsWith("/turns")),
    ).toHaveLength(1);
    await captureReview(
      page,
      changedOwner
        ? "pending-detail-failure-owner-change"
        : "pending-detail-failure",
    );
    failDetail = false;
    if (changedOwner) {
      await retry.focus();
      await page.route(
        "**/api/v1/catalog/context",
        (route) =>
          route.fulfill({
            json: {
              ...fixture.data.context,
              actorPrincipalId: "b0000000-0000-4000-8000-000000000099",
            },
          }),
        { times: 1 },
      );
      await page.keyboard.press("Enter");
    } else await retry.click();
    if (changedOwner) {
      await expect(chat(page).locator(".chat-user-pending")).toHaveCount(0);
      expect(
        fixture.posts.filter((post) => post.path.endsWith("/turns")),
      ).toHaveLength(1);
    } else {
      await expect(
        chat(page)
          .getByRole("region", { name: "Selected answer" })
          .getByRole("status"),
      ).toHaveText("Answer complete.");
      const posts = fixture.posts.filter((post) =>
        post.path.endsWith("/turns"),
      );
      expect(posts).toHaveLength(2);
      expect(posts[1]!.body).toEqual(original.body);
      expect(posts[1]!.path).toBe(original.path);
      await expect(retry).toHaveCount(0);
      await expect(
        chat(page).getByLabel("Question", { exact: true }),
      ).toHaveValue("");
    }
  });
}

for (const ending of ["empty", "prefix", "truncated"] as const) {
  test(`review replay: retained ${ending} EOF stays interrupted and reconnects the same turn`, async ({
    page,
  }) => {
    const fixture = await start(page);
    await ask(page, "Retained first question");
    await ask(page, "Retained second question");
    for (const turns of fixture.turns.values())
      for (const turn of turns) {
        turn.phase = "completed";
        turn.finishedAtUtc = "2026-10-24T10:01:00Z";
      }
    const first = fixture.posts.filter((post) =>
      post.path.endsWith("/turns"),
    )[0]!;
    const id = first.body.turnId;
    const events = fixture.events.get(id)!;
    const afters: number[] = [];
    let interrupted = true;
    await page.route(`**/turns/${id}/events*`, (route) => {
      const after = Number(
        new URL(route.request().url()).searchParams.get("after"),
      );
      afters.push(after);
      const response = interrupted
        ? ending === "empty"
          ? []
          : events.filter((event) => event.sequence <= 6)
        : events.filter((event) => event.sequence > after);
      return route.fulfill({
        contentType: "text/event-stream",
        body:
          response.map(frame).join("") +
          (interrupted && ending === "truncated" ? "data: {" : ""),
      });
    });
    await page.reload();
    const firstTurn = chat(page).locator(`.chat-turn[data-turn-id="${id}"]`);
    // An earlier, unselected turn shows its state without its own live region.
    const firstStatus = firstTurn.locator(".chat-status");
    await expect(firstStatus).toContainText("Connection interrupted.");
    await expect(firstTurn.getByRole("status")).toHaveCount(0);
    if (ending !== "empty")
      await expect(firstTurn.locator(".chat-answer-text")).toContainText(
        "Partner orders maps the order fields.",
      );
    await expect(firstStatus).not.toHaveText("Answer complete.");
    await captureReview(page, `saved-${ending}-interrupted`);
    interrupted = false;
    await firstTurn
      .getByRole("button", { name: "Reconnect answer", exact: true })
      .click();
    await expect(firstStatus).toHaveText("Answer complete.");
    await expect(firstTurn.locator(".chat-answer-text")).toHaveText(
      "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
    );
    expect(afters).toEqual([0, ending === "empty" ? 0 : 6]);
    expect(
      fixture.posts.filter((post) => post.path.endsWith("/turns")),
    ).toHaveLength(2);
    await expect(
      firstTurn.getByRole("button", { name: "Reconnect answer", exact: true }),
    ).toHaveCount(0);
  });
}

for (const failedRead of ["availability", "history"] as const) {
  test(`review refresh: failed ${failedRead} remains visible with a selected answer and can recover`, async ({
    page,
  }) => {
    const fixture = await start(page);
    await ask(page, "Retain this answer across a failed refresh");
    await chat(page)
      .getByLabel("Question", { exact: true })
      .fill("Retain this draft too");
    let fail = true;
    await page.route(
      failedRead === "availability"
        ? `**${fixture.root}`
        : `**${fixture.root}/conversations?*`,
      (route) =>
        fail
          ? route.fulfill({ status: 503, json: { code: "unavailable" } })
          : route.fallback(),
    );
    await refreshChat(chat(page));
    await expect(chat(page).getByRole("alert")).toContainText(
      "Chat is unavailable.",
    );
    await expect(chat(page).getByRole("alert")).toBeVisible();
    await captureReview(page, `refresh-${failedRead}-failed`);
    fail = false;
    await refreshChat(chat(page));
    await expect(
      chat(page)
        .getByRole("region", { name: "Selected answer" })
        .locator(".chat-answer-text"),
    ).toContainText("Partner orders maps the order fields.");
    await expect(
      chat(page).getByLabel("Question", { exact: true }),
    ).toHaveValue("Retain this draft too");
    await expect(chat(page).getByRole("alert")).toHaveCount(0);
    expect(
      fixture.posts.filter((post) => post.path.endsWith("/turns")),
    ).toHaveLength(1);
  });
}

test("review scroll: reading remains in place on reconnect, but explicit send reveals the new turn", async ({
  page,
}) => {
  const fixture = await start(page);
  let firstId = "",
    partial = true;
  await page.route("**/events*", (route) => {
    const url = new URL(route.request().url());
    const id = url.pathname.split("/").at(-2)!;
    firstId ||= id;
    const events: ChatEvent[] =
      id === firstId
        ? [
            { type: "turn.started", sequence: 1 },
            {
              type: "text.delta",
              sequence: 2,
              roundId: round,
              text: "Read-only retained source context.\n".repeat(90),
            },
            {
              type: "text.delta",
              sequence: 3,
              roundId: round,
              text: "Finished reading the original answer.",
            },
            { type: "turn.completed", sequence: 4 },
          ]
        : [
            { type: "turn.started", sequence: 1 },
            {
              type: "text.delta",
              sequence: 2,
              roundId: round,
              text: "The new answer is here.",
            },
            { type: "turn.completed", sequence: 3 },
          ];
    const after = Number(url.searchParams.get("after"));
    return route.fulfill({
      contentType: "text/event-stream",
      body: events
        .filter(
          (event) =>
            event.sequence > after &&
            !(id === firstId && partial && event.sequence > 2),
        )
        .map(frame)
        .join(""),
    });
  });
  await chat(page)
    .getByLabel("Question", { exact: true })
    .fill("A long answer to read");
  await chat(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const answer = chat(page).getByRole("region", { name: "Selected answer" });
  await expect(answer.getByRole("status")).toContainText(
    "Connection interrupted.",
  );
  const scroll = chat(page).locator(".chat-scroll");
  await scroll.hover();
  await page.mouse.wheel(0, -100000);
  await expect
    .poll(() => scroll.evaluate((element) => element.scrollTop))
    .toBe(0);
  partial = false;
  await answer
    .getByRole("button", { name: "Reconnect answer", exact: true })
    .click();
  await expect(answer.getByRole("status")).toHaveText("Answer complete.");
  await expect
    .poll(() => scroll.evaluate((element) => element.scrollTop))
    .toBe(0);
  await ask(page, "Reveal this new question after I scrolled up");
  await expect(
    chat(page).locator(".chat-turn").last().locator(".chat-bubble"),
  ).toBeInViewport();
  await expect
    .poll(() =>
      scroll.evaluate(
        (element) =>
          element.scrollHeight - element.scrollTop - element.clientHeight,
      ),
    )
    .toBeLessThan(48);
  await captureReview(page, "explicit-send-reveals-turn");
  expect(
    fixture.posts.filter((post) => post.path.endsWith("/turns")),
  ).toHaveLength(2);
});

for (const width of [1440, 360]) {
  test(`review tooltip: Escape dismisses the focus tooltip without moving focus or hiding chat (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    await start(page);
    const chip = chat(page).locator(".chat-context-fixed");
    const tooltip = chip.getByRole("tooltip");
    await chip.hover();
    await chip.focus();
    await expect(tooltip).toBeVisible();
    await captureReview(page, `focus-tooltip-${width}-open`);
    await chip.press("Escape");
    await expect(tooltip).toBeHidden();
    await expect(chip).toBeFocused();
    await expect(chat(page)).toBeVisible();
    await chip.press("ArrowRight");
    await expect(tooltip).toBeHidden();
    await captureReview(page, `focus-tooltip-${width}-dismissed`);
    await page.keyboard.press("Tab");
    await page.keyboard.press("Shift+Tab");
    await expect(chip).toBeFocused();
    await expect(tooltip).toBeVisible();
    await chip.press("Escape");
    await expect(tooltip).toBeHidden();
    await page.mouse.move(0, 0);
    await chip.hover();
    await expect(tooltip).toBeVisible();
    await expect(chat(page)).toBeVisible();
  });
}

test("Escape outside chat stays with the page while a chat tooltip is visible", async ({
  page,
}) => {
  await start(page);
  const chip = chat(page).locator(".chat-context-fixed");
  const tooltip = chip.getByRole("tooltip");
  await chip.hover();
  await expect(tooltip).toBeVisible();
  await page.evaluate(() => {
    const record = window as unknown as { escapes: number };
    record.escapes = 0;
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape") record.escapes += 1;
    });
    (document.activeElement as HTMLElement | null)?.blur();
  });
  await page.keyboard.press("Escape");
  expect(
    await page.evaluate(
      () => (window as unknown as { escapes: number }).escapes,
    ),
  ).toBe(1);
  await expect(tooltip).toBeVisible();
  await chip.focus();
  await chip.press("Escape");
  await expect(tooltip).toBeHidden();
  expect(
    await page.evaluate(
      () => (window as unknown as { escapes: number }).escapes,
    ),
  ).toBe(1);
});
