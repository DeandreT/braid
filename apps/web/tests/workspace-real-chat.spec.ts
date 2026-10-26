import { expect, test, type Page } from "@playwright/test";

// No API interception: actual catalog, restricted PostgreSQL ledger, tools and owned provider wire.
const panel = (page: Page) =>
  page.getByRole("complementary", { name: /^(Workspace|Mapping) chat$/ });
async function question(page: Page) {
  await panel(page)
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Read the other project and explain how workspace chat works.");
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const status = panel(page)
    .getByRole("region", { name: "Selected answer" })
    .getByRole("status");
  await expect(status).toHaveText(
    /^Answer (complete|failed|interrupted|cancelled)/,
    {
      timeout: 70000,
    },
  );
  await expect(status).toHaveText("Answer complete.");
}
test("workspace page and exact-focused workbench use real authority, persistence, tools and verified guide citations", async ({
  page,
}) => {
  await page.goto("/chat");
  await question(page);
  await expect(
    panel(page).getByRole("link", { name: "Open project", exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("link", {
      name: "Open guideSection chat",
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
      name: "Open guideSection chat",
      exact: true,
    }),
  ).toBeVisible();
  const next = page.context().waitForEvent("page");
  await panel(page)
    .getByRole("link", { name: "Open in chat", exact: true })
    .click();
  const standalone = await next;
  await expect(panel(standalone)).toContainText("Workspace sources checked.");
  await expect(panel(standalone)).toContainText("Turn focus: resolved");
  await expect(page).toHaveURL(new RegExp(path + "$"));
  await standalone.close();
});
