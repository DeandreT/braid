import { expect, test, type Page, type Locator } from "@playwright/test";
import { createHash } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";
import { workspaceFixture, round } from "./workspace-chat.fixture";
import type { MappingWriteEvent } from "../src/chat/workspace-chat-types";
import type { MappingProposal } from "../src/chat/mapping-proposal-types";

const script =
  '{"format":"ferrule.edit.v1","operations":[{"op":"bind","note":"<script>untrusted</script>"}]}';
const digest = createHash("sha256").update(script).digest("hex");

for (const direction of ["proposal recovery", "receipt refresh"] as const) {
  test(`both lists reconcile through ${direction}, without unidentified recovery controls`, async ({
    page,
  }) => {
    const { f, proposal } = await fixture(page);
    proposal.status = "pending";
    const requestId = "90000000-0000-4000-8000-000000000001";
    f.state.receipts = [{ requestId, status: "pending", write: null }];
    const finish = () => {
      proposal.status = "saved";
      proposal.revisionId = "40000000-0000-4000-8000-0000000000aa";
      proposal.revisionSequence = 3;
      f.state.receipts = [
        {
          requestId,
          status: "saved",
          write: {
            type: "mapping.write",
            sequence: 1,
            roundId: round,
            outcome: "saved",
            reason: null,
            baseRevision: proposal.baseRevision,
            baseSequence: "2",
            revisionId: proposal.revisionId,
            revisionSequence: "3",
            headRevisionId: null,
            headSequence: null,
            operationCount: 1,
            changes: proposal.changes,
            changesTruncated: false,
          } as MappingWriteEvent,
        },
      ];
    };
    await page.route("**/mapping-edit-proposals/*/recover", async (route) => {
      finish();
      await route.fulfill({ json: proposal });
    });
    await ask(page);
    const receipt = page.getByRole("region", {
      name: "Mapping save status",
      exact: true,
    });
    await expect(receipt).toContainText("Unidentified save receipt");
    await expect(
      page.getByRole("button", { name: "Continue pending save" }),
    ).toHaveCount(0);
    if (direction === "receipt refresh") {
      finish();
      await receipt
        .getByRole("button", { name: "Refresh save status", exact: true })
        .click();
    } else {
      // Hold an old receipt read across recovery; it must not restore pending state.
      let release!: () => void;
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });
      let started = false;
      let proposalReadStarted = false;
      const oldProposal = { ...proposal };
      await page.route("**/mapping-edit-proposals", async (route) => {
        if (!proposalReadStarted) {
          proposalReadStarted = true;
          await held;
          await route.fulfill({ json: { proposals: [oldProposal] } });
        } else await route.fulfill({ json: { proposals: [proposal] } });
      });
      await page.route("**/mapping-writes", async (route) => {
        if (!started) {
          started = true;
          await held;
          await route.fulfill({
            json: { receipts: [{ requestId, status: "pending", write: null }] },
          });
        } else await route.fulfill({ json: { receipts: f.state.receipts } });
      });
      await receipt
        .getByRole("button", { name: "Refresh save status", exact: true })
        .click();
      await expect.poll(() => started).toBe(true);
      await expect.poll(() => proposalReadStarted).toBe(true);
      await cards(page)
        .getByRole("button", { name: "Resume pending save", exact: true })
        .click();
      await expect(cards(page)).toContainText("Saved as version 3");
      release();
    }
    await expect(cards(page)).toContainText("Saved as version 3");
    await expect(receipt).toHaveCount(0);
    await expect(
      page.getByRole("region", { name: "Saved mapping edit", exact: true }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("button", {
        name: /Resume pending save|Continue pending save/,
      }),
    ).toHaveCount(0);
  });
}
const cards = (page: Page) =>
  page.getByRole("region", { name: "Mapping edit proposal", exact: true });
async function ask(page: Page) {
  await page.goto("/chat");
  await page
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await page.getByLabel("Question", { exact: true }).fill("Change code to X.");
  await page
    .getByRole("button", { name: "Send question", exact: true })
    .click();
}
async function review(card: Locator) {
  await card
    .getByRole("button", { name: "Review complete operations", exact: true })
    .click();
  await expect(
    card.getByLabel("Complete proposal operations", { exact: true }),
  ).toHaveText(script);
  await card.getByRole("checkbox").check();
}
async function fixture(page: Page) {
  const f = await workspaceFixture(page);
  const proposal: MappingProposal = {
    proposalId: "a0000000-0000-4000-8000-000000000001",
    fingerprint: "a".repeat(64),
    status: "awaitingApproval",
    baseRevision: f.data.revision.scope,
    baseSequence: 2,
    operationCount: 1,
    operationsDigest: digest,
    changes: [{ field: "code", before: "old", after: "new" }],
    changesTruncated: true,
    revisionId: null,
    revisionSequence: null,
  };
  const state = {
    proposals: [proposal],
    requests: [] as {
      path: string;
      body: unknown;
      token: string | undefined;
    }[],
    badDetails: false,
    loseApproval: false,
    hold: null as Promise<void> | null,
    listReads: 0,
  };
  await page.route("**/mapping-edit-proposals**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname;
    if (path.endsWith("/mapping-edit-proposals")) {
      state.listReads++;
      return route.fulfill({ json: { proposals: state.proposals } });
    }
    const p = state.proposals.find((p) => path.includes(p.proposalId))!;
    if (path.endsWith("/operations")) {
      expect(url.searchParams.get("expectedFingerprint")).toBe(p.fingerprint);
      return route.fulfill({
        json: {
          proposalId: p.proposalId,
          fingerprint: state.badDetails ? "f".repeat(64) : p.fingerprint,
          operationCount: p.operationCount,
          operationsDigest: p.operationsDigest,
          canonicalScript: script,
        },
      });
    }
    state.requests.push({
      path,
      body: request.postDataJSON(),
      token: request.headers()["x-xsrf-token"],
    });
    if (state.hold) await state.hold;
    if (state.loseApproval) return route.abort("failed");
    if (path.endsWith("/approve")) {
      p.status = "saved";
      p.revisionId = "40000000-0000-4000-8000-0000000000aa";
      p.revisionSequence = 3;
    }
    return route.fulfill({ json: p });
  });
  return { f, state, proposal };
}

test("two cards approve only the reviewed fingerprint, block duplicate clicks, and rediscover after reload without a proposal event", async ({
  page,
}) => {
  const { f, state, proposal } = await fixture(page);
  state.proposals.push({
    ...proposal,
    proposalId: "a0000000-0000-4000-8000-000000000002",
    fingerprint: "b".repeat(64),
  });
  await ask(page);
  await expect(cards(page)).toHaveCount(2);
  expect(state.requests).toHaveLength(0);
  const second = cards(page).nth(1);
  await expect(second).toContainText("Only some change summaries");
  await expect(
    second.getByRole("button", {
      name: "Approve this exact proposal",
      exact: true,
    }),
  ).toHaveCount(0);
  await review(second);
  let release!: () => void;
  state.hold = new Promise((resolve) => {
    release = resolve;
  });
  const approve = second.getByRole("button", {
    name: "Approve this exact proposal",
    exact: true,
  });
  await approve.click();
  await approve.dispatchEvent("click");
  await expect.poll(() => state.requests.length).toBe(1);
  expect(state.requests[0]!.body).toEqual({
    expectedFingerprint: "b".repeat(64),
  });
  expect(state.requests[0]!.path).toContain(state.proposals[1]!.proposalId);
  expect(state.requests[0]!.token).toBeTruthy();
  await expect(approve).toBeDisabled();
  release();
  await expect(second).toContainText("Saved as version 3");
  await expect(cards(page).first()).toContainText("Awaiting your review");
  await page.reload();
  await expect(cards(page)).toHaveCount(2);
  await expect(cards(page).nth(1)).toContainText("Saved as version 3");
  expect(state.requests).toHaveLength(1);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(1);
});

test("mismatched details cannot approve and an ambiguous approval requires durable refresh", async ({
  page,
}) => {
  const { state, proposal } = await fixture(page);
  state.badDetails = true;
  await ask(page);
  const card = cards(page).first();
  await card
    .getByRole("button", { name: "Review complete operations", exact: true })
    .click();
  await expect(card).toContainText("Couldn't verify the complete operations");
  await expect(card.getByRole("checkbox")).toHaveCount(0);
  state.badDetails = false;
  await review(card);
  state.loseApproval = true;
  await card
    .getByRole("button", { name: "Approve this exact proposal", exact: true })
    .click();
  await expect(card).toContainText("request outcome is unknown");
  await expect(card).not.toContainText("Saved as version");
  await expect(
    card.getByRole("button", {
      name: "Approve this exact proposal",
      exact: true,
    }),
  ).toBeDisabled();
  proposal.status = "saved";
  proposal.revisionId = "40000000-0000-4000-8000-0000000000aa";
  proposal.revisionSequence = 3;
  await card
    .getByRole("button", { name: "Refresh proposal status", exact: true })
    .click();
  await expect(card).toContainText("Saved as version 3");
  expect(state.requests).toHaveLength(1);
});

test("pending recovery is explicit and out-of-date updates prepare an unsent question needing new approval", async ({
  page,
}) => {
  const { f, state, proposal } = await fixture(page);
  proposal.status = "pending";
  await ask(page);
  const card = cards(page).first();
  await expect(card).toContainText("Save pending");
  expect(state.requests).toHaveLength(0);
  await card
    .getByRole("button", { name: "Resume pending save", exact: true })
    .click();
  await expect.poll(() => state.requests.length).toBe(1);
  expect(state.requests[0]!.path).toMatch(/\/recover$/);
  expect(state.requests[0]!.body).toEqual({
    expectedFingerprint: proposal.fingerprint,
  });
  await expect(card).toContainText("Save pending");
  proposal.status = "outOfDate";
  await card
    .getByRole("button", { name: "Refresh proposal status", exact: true })
    .click();
  await card
    .getByRole("button", { name: "Prepare new proposal question", exact: true })
    .click();
  await expect(page.getByLabel("Question", { exact: true })).toHaveValue(
    /Prepare a new proposal.*Do not save or approve anything/,
  );
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(1);
  expect(state.requests).toHaveLength(1);
});

test("a completed approval response cannot populate a different conversation", async ({
  page,
}) => {
  const { state } = await fixture(page);
  await ask(page);
  await review(cards(page).first());
  let release!: () => void;
  state.hold = new Promise((resolve) => {
    release = resolve;
  });
  await cards(page)
    .first()
    .getByRole("button", { name: "Approve this exact proposal", exact: true })
    .click();
  await expect.poll(() => state.requests.length).toBe(1);
  await page
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  release();
  await expect(cards(page)).toHaveCount(0);
  await expect(
    page.getByRole("complementary", { name: /chat$/ }),
  ).not.toContainText("Saved as version 3");
});

test("fresh owner admission prevents approving previously reviewed private content", async ({
  page,
}) => {
  const { f, state } = await fixture(page);
  await ask(page);
  await review(cards(page).first());
  f.data.context.actorPrincipalId = "b0000000-0000-4000-8000-000000000099";
  await cards(page)
    .first()
    .getByRole("button", { name: "Approve this exact proposal", exact: true })
    .click();
  await expect(cards(page)).toHaveCount(0);
  expect(state.requests).toHaveLength(0);
});

test("proposal review remains keyboard-accessible and bounded on a narrow screen", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await fixture(page);
  await ask(page);
  const card = cards(page).first();
  await review(card);
  await expect(card.locator("script")).toHaveCount(0);
  const operations = card.getByRole("region", {
    name: "Complete proposal operations",
    exact: true,
  });
  await operations.focus();
  await expect(operations).toBeFocused();
  const bounds = await card.boundingBox();
  expect(bounds!.width).toBeLessThanOrEqual(390);
  const result = await new AxeBuilder({ page })
    .include(".chat-write-proposal")
    .analyze();
  expect(result.violations).toEqual([]);
});
