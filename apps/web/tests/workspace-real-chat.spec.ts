import { expect, test, type Page } from "@playwright/test";
import { openMore } from "./chat-ui.fixture";

// No API interception: actual catalog, restricted PostgreSQL ledger, tools and owned provider wire.
const panel = (page: Page) =>
  page.getByRole("complementary", { name: /^(Workspace|Mapping) chat$/ });
const status = (page: Page) =>
  panel(page)
    .getByRole("region", { name: "Selected answer" })
    .getByRole("status");
/** Sends a question and returns the started turn's API path. */
async function send(page: Page) {
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Read the other project and explain how workspace chat works.");
  const started = page.waitForResponse(
    (r) =>
      r.request().method() === "POST" &&
      /\/turns$/.test(new URL(r.url()).pathname),
  );
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const response = await started;
  expect(response.status()).toBe(201);
  const { turnId } = (await response.json()) as { turnId: string };
  return `${new URL(response.url()).pathname}/${turnId}`;
}
async function question(page: Page) {
  const turn = await send(page);
  await expect(status(page)).toHaveText(
    /^\s*Answer (complete|failed|interrupted|cancelled)/,
    {
      timeout: 70000,
    },
  );
  await expect(status(page)).toHaveText("Answer complete.");
  return turn;
}
/** Two bodyless browser cancellations of a terminal turn; the owned proxy forwards each as empty chunked framing. */
async function replayCancel(page: Page, turn: string, phase: string) {
  const result = await page.evaluate(async (path) => {
    const listed = async () => {
      const r = await fetch(path.replace(/\/turns\/[^/]+$/, ""));
      const { turns } = (await r.json()) as {
        turns: { turnId: string }[];
      };
      return JSON.stringify(turns.find((t) => path.endsWith(t.turnId)));
    };
    const context = await fetch("/api/v1/catalog/context");
    const { requestToken } = (await context.json()) as {
      requestToken: string;
    };
    const cancel = async () => {
      const r = await fetch(path + "/cancel", {
        method: "POST",
        headers: { "X-XSRF-TOKEN": requestToken },
      });
      return { status: r.status, body: await r.text() };
    };
    const before = await listed();
    const first = await cancel();
    const second = await cancel();
    return { before, first, second, after: await listed() };
  }, turn);
  expect(result.first.status).toBe(200);
  expect(result.second).toEqual(result.first);
  expect(JSON.parse(result.first.body)).toMatchObject({ phase });
  expect(result.after).toBe(result.before);
  expect(JSON.parse(result.after)).toMatchObject({ phase });
}
test("workspace page and exact-focused workbench use real authority, persistence, tools and verified guide citations", async ({
  page,
}) => {
  await page.goto("/chat");
  const completed = await question(page);
  await expect(
    panel(page).getByRole("link", { name: "Open project", exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("link", {
      name: "Open help chat",
      exact: true,
    }),
  ).toHaveAttribute("href", "/help/chat");
  await expect(panel(page)).toContainText("Metadata only");
  await expect(panel(page).getByText(/Data shared with/)).toHaveCount(0);
  const thread = page.url();
  await page.reload();
  await expect(panel(page)).toContainText("Workspace sources checked.");
  await expect(page).toHaveURL(thread);
  const path = process.env.WEFT_WORKSPACE_CHAT_REVISION_PATH;
  if (!path || !path.startsWith("/catalog/organizations/"))
    throw new Error("The owned revision is required.");
  await page.goto(path);
  await question(page);
  await expect(panel(page)).toContainText("Turn focus: resolved");
  // The provider's second tool round runs after the other project's membership was withdrawn.
  await expect(
    panel(page).getByRole("link", { name: "Open project", exact: true }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("link", {
      name: "Open help chat",
      exact: true,
    }),
  ).toBeVisible();
  const next = page.context().waitForEvent("page");
  await openMore(panel(page));
  await panel(page)
    .getByRole("link", { name: "Open full page", exact: true })
    .click();
  const standalone = await next;
  await expect(panel(standalone)).toContainText("Workspace sources checked.");
  await expect(panel(standalone)).toContainText("Turn focus: resolved");
  await expect(page).toHaveURL(new RegExp(path + "$"));
  await standalone.close();

  // Cancellation through the browser-facing proxy, then idempotent replays of terminal turns.
  const active = await send(page);
  await expect(panel(page)).toContainText("Partial answer.", {
    timeout: 70000,
  });
  await panel(page)
    .getByRole("button", { name: "Cancel answer", exact: true })
    .click();
  await expect(status(page)).toHaveText("Answer cancelled.", {
    timeout: 70000,
  });
  await replayCancel(page, active, "cancelled");
  await replayCancel(page, completed, "completed");
});
