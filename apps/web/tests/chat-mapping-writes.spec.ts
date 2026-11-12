import { expect, test, type Page } from "@playwright/test";
import type { ChatEvent } from "../src/chat/chat-types";
import type { MappingWriteEvent } from "../src/chat/workspace-chat-types";
import {
  otherMapping,
  otherProject,
  round,
  workspaceFixture,
} from "./workspace-chat.fixture";

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
  f.state.receipts = [
    {
      requestId: "90000000-0000-4000-8000-000000000001",
      status: "saved",
      write: f.state.write as MappingWriteEvent,
    },
  ];
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
  expect(second!.body.text).toContain(first!.body.turnId);
  expect(second!.body.text).toContain(f.data.revision.scope.mappingId);
  expect(second!.body.text).toContain(f.data.revision.scope.projectId);
  expect(second!.body.text).toContain(f.data.revision.scope.organizationId);
  expect(second!.body.text).toContain(newer);
  expect(second!.body.text).toContain('"eventSequence":7');
  expect(second!.body.text).toContain(
    '"originalQuestion":"Set code to edited."',
  );
  expect(second!.body.text).toContain('"selectedChanges":');
  expect(second!.body.text).toContain("order/code");
});

test("Update identifies the clicked mapping and source proposal when two mappings share a head sequence", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  f.state.write = write(f.data.revision.scope, "proposed", "outOfDate");
  await askForEdit(page);
  await expect(
    panel(page).getByRole("region", { name: "Proposed mapping edit" }),
  ).toHaveCount(1);
  await expect(
    panel(page).getByLabel("Question", { exact: true }),
  ).toBeEnabled();
  f.state.write = write(
    {
      ...f.data.revision.scope,
      projectId: otherProject,
      mappingId: otherMapping,
    },
    "proposed",
    "outOfDate",
  );
  await panel(page)
    .getByLabel("Question", { exact: true })
    .fill("Edit the other mapping.");
  await panel(page)
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  const proposals = panel(page).getByRole("region", {
    name: "Proposed mapping edit",
  });
  await expect(proposals).toHaveCount(2);
  const sends = () => f.posts.filter((post) => post.path.endsWith("/turns"));
  const firstSource = sends()[0]!.body.turnId;
  const secondSource = sends()[1]!.body.turnId;
  f.state.write = null;
  await proposals
    .nth(1)
    .getByRole("button", { name: "Update for version 4", exact: true })
    .click();
  await expect.poll(() => sends().length).toBe(3);
  expect(sends()[2]!.body.text).toContain(otherMapping);
  expect(sends()[2]!.body.text).toContain(otherProject);
  expect(sends()[2]!.body.text).toContain(secondSource);
  expect(sends()[2]!.body.text).not.toContain(firstSource);
  await expect(
    panel(page).getByLabel("Question", { exact: true }),
  ).toBeEnabled();
  await proposals
    .nth(0)
    .getByRole("button", { name: "Update for version 4", exact: true })
    .click();
  await expect.poll(() => sends().length).toBe(4);
  expect(sends()[3]!.body.text).toContain(f.data.revision.scope.mappingId);
  expect(sends()[3]!.body.text).toContain(firstSource);
  expect(sends()[3]!.body.text).not.toContain(secondSource);
});

test("pending save status survives an interrupted answer and resolves without replaying its turn", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  const requestId = "90000000-0000-4000-8000-000000000001";
  f.state.partial = true;
  f.state.receipts = [{ requestId, status: "pending", write: null }];
  await askForEdit(page);
  const status = panel(page).getByRole("region", {
    name: "Mapping save status",
  });
  await expect(status).toContainText("reports pending");
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toHaveCount(0);
  // The ingestion completes after the stream has ended. Reading receipts never sends a new question.
  f.state.receipts = [
    {
      requestId,
      status: "saved",
      write: write(f.data.revision.scope, "saved", null) as MappingWriteEvent,
    },
  ];
  await status
    .getByRole("button", { name: "Refresh save status", exact: true })
    .click();
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toContainText("Saved version 2 → 3");
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(1);
  expect(f.posts.filter((p) => p.path.endsWith("/recover"))).toHaveLength(0);
  await page.reload();
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toHaveCount(1);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(1);
});

test("an unidentified receipt is read-only and a lost saved event is recovered by refresh", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  const requestId = "90000000-0000-4000-8000-000000000001";
  f.state.receipts = [{ requestId, status: "pending", write: null }];
  const resumes: string[] = [];
  await page.route("**/mapping-writes/recover", (route) => {
    resumes.push(route.request().headers()["x-xsrf-token"] ?? "missing");
    f.state.receipts = [
      {
        requestId,
        status: "saved",
        write: write(f.data.revision.scope, "saved", null) as MappingWriteEvent,
      },
    ];
    return route.fulfill({ json: { receipts: f.state.receipts } });
  });
  await askForEdit(page);
  const status = panel(page).getByRole("region", {
    name: "Mapping save status",
  });
  await expect(status).toContainText("reports pending");
  await expect(
    status.getByRole("button", { name: "Continue pending save" }),
  ).toHaveCount(0);
  expect(resumes).toEqual([]);
  f.state.receipts = [
    {
      requestId,
      status: "saved",
      write: write(f.data.revision.scope, "saved", null) as MappingWriteEvent,
    },
  ];
  await status
    .getByRole("button", { name: "Refresh save status", exact: true })
    .click();
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toHaveCount(1);
  expect(resumes).toHaveLength(0);
  // The same receipt also arriving in the ledger must not create a second card or Undo state.
  f.state.write = write(f.data.revision.scope, "saved", null);
  await page.reload();
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toHaveCount(1);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(1);
});

test("overlapping historical and selected-turn receipt reads both retain their cards", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await askForEdit(page);
  // An enabled composer does not mean the first answer stream has finished.
  // Establish two retained terminal turns before racing their receipt reads.
  const selectedAnswer = panel(page).getByRole("region", {
    name: "Selected answer",
    exact: true,
  });
  await expect(
    selectedAnswer.getByText("Answer complete.", { exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByLabel("Question", { exact: true }),
  ).toBeEnabled();
  await page
    .getByLabel("Question", { exact: true })
    .fill("Change another field.");
  await page
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect
    .poll(() => f.posts.filter((p) => p.path.endsWith("/turns")).length)
    .toBe(2);
  await expect(
    panel(page).getByText("Change another field.", { exact: true }),
  ).toBeVisible();
  await expect(
    selectedAnswer.getByText("Answer complete.", { exact: true }),
  ).toBeVisible();
  expect([...f.turns.values()].flat().map((turn) => turn.phase)).toEqual([
    "completed",
    "completed",
  ]);
  const requested = new Set<string>();
  let release!: () => void;
  const overlap = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/mapping-writes", async (route) => {
    const turn = new URL(route.request().url()).pathname.split("/").at(-2)!;
    requested.add(turn);
    if (requested.size === 2) release();
    await overlap;
    return route.fulfill({
      json: {
        receipts: [
          {
            requestId: turn,
            status: "saved",
            write: write(f.data.revision.scope, "saved", null),
          },
        ],
      },
    });
  });
  await page.reload();
  await expect.poll(() => requested.size).toBe(2);
  await expect(
    panel(page).getByRole("region", { name: "Saved mapping edit" }),
  ).toHaveCount(2);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(2);
});
