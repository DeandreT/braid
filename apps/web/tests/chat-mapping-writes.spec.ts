import { expect, test, type Page } from "@playwright/test";
import type { ChatEvent } from "../src/chat/chat-types";
import { round, workspaceFixture } from "./workspace-chat.fixture";

const saved = "40000000-0000-4000-8000-0000000000aa";
const newer = "40000000-0000-4000-8000-0000000000bb";
const panel = (page: Page) =>
  page.getByRole("complementary", { name: /^(Workspace|Mapping) chat$/ });

function write(
  base: {
    organizationId: string;
    projectId: string;
    mappingId: string;
    revisionId: string;
  },
  outcome: "saved" | "proposed",
  reason: "notPermitted" | "outOfDate" | null,
): ChatEvent {
  return {
    type: "mapping.write",
    sequence: 7,
    roundId: round,
    outcome,
    reason,
    baseRevision: base,
    baseSequence: "2",
    revisionId: outcome === "saved" ? saved : null,
    revisionSequence: outcome === "saved" ? "3" : null,
    headRevisionId: reason === "outOfDate" ? newer : null,
    headSequence: reason === "outOfDate" ? "4" : null,
    operationCount: 1,
    changes: [
      { field: "order/code", before: "Not mapped", after: 'Constant "edited"' },
    ],
    changesTruncated: false,
  } as ChatEvent;
}

async function askForEdit(page: Page) {
  await page.goto("/chat");
  await page
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await page
    .getByLabel("Question", { exact: true })
    .fill("Set code to edited.");
  await page
    .getByRole("button", { name: "Send question", exact: true })
    .click();
}

test("a saved edit shows a receipt with plain before/after, explicit undo, diff, open and test actions", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.write = write(f.data.revision.scope, "saved", null);
  const restores: { key: string | undefined; body: unknown; path: string }[] =
    [];
  const runs: unknown[] = [];
  let failRestore = true;
  await page.route("**/restore", async (route) => {
    restores.push({
      key: route.request().headers()["idempotency-key"],
      body: route.request().postDataJSON(),
      path: new URL(route.request().url()).pathname,
    });
    if (failRestore) {
      failRestore = false;
      return route.abort("failed");
    }
    const { organizationId, projectId, mappingId } = f.data.revision.scope;
    return route.fulfill({
      json: {
        outcome: "committedValid",
        receipt: {
          operationId: "70000000-0000-4000-8000-000000000001",
          phase: "committed",
          futureRevision: {
            organizationId,
            projectId,
            mappingId,
            revisionId: newer,
          },
          committedSequence: "4",
        },
      },
    });
  });
  await page.route("**/regression-suites/**", (route) =>
    route.request().method() === "PUT"
      ? (runs.push(route.request().postDataJSON()), route.fulfill({ json: {} }))
      : route.fulfill({ status: 404, json: { code: "notFound" } }),
  );
  await askForEdit(page);
  const receipt = panel(page).getByRole("region", {
    name: "Saved mapping edit",
  });
  await expect(receipt).toContainText("Saved version 2 → 3");
  await expect(receipt).toContainText("The version in use is unchanged");
  await expect(receipt).toContainText("order/code");
  await expect(receipt).toContainText("Before: Not mapped");
  await expect(receipt).toContainText('After: Constant "edited"');
  await expect(receipt.getByRole("link", { name: "Diff" })).toHaveAttribute(
    "href",
    new RegExp(
      `/comparisons\\?left=${f.data.revision.scope.revisionId}&right=${saved}$`,
    ),
  );
  await expect(
    receipt.getByRole("link", { name: "Open version" }),
  ).toHaveAttribute("href", new RegExp(`/revisions/${saved}$`));
  // Nothing is run or restored until the user asks.
  expect(runs).toEqual([]);
  expect(restores).toEqual([]);
  await receipt.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(receipt.getByRole("status").last()).toContainText(
    "couldn't be confirmed",
  );
  await receipt.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(receipt.getByRole("status").last()).toContainText(
    "Undone: version 4 has the content of version 2.",
  );
  expect(restores).toHaveLength(2);
  // A retry repeats the same request, so it can never save twice.
  expect(restores[1]!.key).toBe(restores[0]!.key);
  expect(restores[0]!.path).toMatch(
    new RegExp(`/revisions/${f.data.revision.scope.revisionId}/restore$`),
  );
  expect(restores[0]!.body).toEqual({ expectedHeadRevisionId: saved });
  await receipt
    .getByRole("button", { name: "Run regression tests", exact: true })
    .click();
  await expect.poll(() => runs.length).toBe(1);
  expect(runs[0]).toEqual({
    mappingId: f.data.revision.scope.mappingId,
    revisionId: saved,
  });
});

test("without edit permission the edit is a read-only proposal with no write actions", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.write = write(f.data.revision.scope, "proposed", "notPermitted");
  await askForEdit(page);
  const proposal = panel(page).getByRole("region", {
    name: "Proposed mapping edit",
  });
  await expect(proposal).toContainText("Proposed edit to version 2");
  await expect(proposal).toContainText("don't have permission");
  await expect(proposal).toContainText('After: Constant "edited"');
  await expect(proposal.getByRole("button")).toHaveCount(0);
  await expect(proposal.getByRole("link")).toHaveCount(0);
});

test("an out-of-date proposal updates only through a new, explicitly sent question", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.write = write(f.data.revision.scope, "proposed", "outOfDate");
  await askForEdit(page);
  const proposal = panel(page).getByRole("region", {
    name: "Proposed mapping edit",
  });
  await expect(proposal).toContainText("version 4 is newer");
  const sends = () => f.posts.filter((post) => post.path.endsWith("/turns"));
  expect(sends()).toHaveLength(1);
  f.state.write = null;
  await proposal
    .getByRole("button", { name: "Update for version 4", exact: true })
    .click();
  await expect.poll(() => sends().length).toBe(2);
  const [first, second] = sends();
  expect(second!.body.turnId).not.toBe(first!.body.turnId);
  expect(second!.body.text).toContain("version 4");
});
