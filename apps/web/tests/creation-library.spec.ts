import { expect, test, type Page } from "@playwright/test";
import { createHash } from "node:crypto";
import {
  creationCapability,
  creationPage,
  creationReviewPath,
  feedbackQuestion,
} from "../src/catalog/creation/creation-library-model";
import { synthetic, syntheticCatalog } from "./catalog.fixture";
import { legacyId, workspaceFixture } from "./workspace-chat.fixture";
import type { CreationProposal } from "../src/catalog/creation/creation-model";
import type { components } from "../src/api/generated";

type ProposalPage = components["schemas"]["CreationProposalPageV1"];

const project = synthetic.project.scope.projectId;
const org = synthetic.org;
const id = "50000000-0000-4000-8000-000000000001";
const hash = (value: string) =>
  createHash("sha256").update(value).digest("hex");
const fingerprint = hash("exact retained proposal");
const root = `/catalog/organizations/${org}/projects/${project}`;
const row = (proposalId = id) => ({
  proposalId,
  fingerprint,
  reviewPath: creationReviewPath(org, project, proposalId, fingerprint),
});

test("closed pages bind exact links, canonical fingerprints and advancing cursors", () => {
  expect(creationCapability({ available: true })).toBe(true);
  expect(creationCapability({ available: false })).toBe(false);
  for (const value of [
    {},
    { available: "true" },
    { available: true, other: 1 },
  ])
    expect(() => creationCapability(value)).toThrow();
  const page: ProposalPage = { proposals: [row()], afterProposal: null };
  expect(creationPage(page, org, project, null).proposals).toEqual([row()]);
  for (const value of [
    { ...page, afterProposal: id },
    { ...page, extra: true },
    { proposals: [row(), row()], afterProposal: null },
    { ...page, proposals: [{ ...row(), fingerprint: "A".repeat(64) }] },
    {
      ...page,
      proposals: [{ ...row(), reviewPath: "https://foreign.invalid/" }],
    },
    { ...page, proposals: [{ ...row(), extra: true }] },
  ])
    expect(() => creationPage(value, org, project, null)).toThrow();
  expect(() => creationPage(page, org, project, id)).toThrow();
  expect(
    feedbackQuestion({ projectId: project, proposalId: id, fingerprint }),
  ).toContain(fingerprint);
});

async function library(page: Page, available = true) {
  await syntheticCatalog(page);
  const state = {
    available,
    denied: false,
    reads: [] as string[],
    mutations: [] as string[],
  };
  await page.route("**/creation-capabilities", (route) =>
    route.fulfill({ json: { available: state.available } }),
  );
  await page.route("**/creation-proposals?*", (route) => {
    state.reads.push(route.request().url());
    return state.denied
      ? route.fulfill({ status: 404, json: { code: "NotFound" } })
      : route.fulfill({
          json: {
            proposals: [row()],
            afterProposal: null,
          } satisfies ProposalPage,
        });
  });
  page.on("request", (request) => {
    if (
      request.method() === "POST" &&
      request.url().includes("creation-proposals")
    )
      state.mutations.push(request.url());
  });
  await page.goto(root + "/creation");
  return state;
}

test("proposal library rediscovers exact links on reload without approving", async ({
  page,
}) => {
  const state = await library(page);
  const link = page.getByRole("link", {
    name: `Review proposal ${id}`,
    exact: true,
  });
  await expect(link).toHaveAttribute("href", row().reviewPath);
  await page.reload();
  await expect(link).toHaveAttribute("href", row().reviewPath);
  expect(state.reads.length).toBeGreaterThan(1);
  expect(state.mutations).toHaveLength(0);
  state.denied = true;
  await page
    .getByRole("button", { name: "Refresh proposals and access" })
    .click();
  await expect(link).toHaveCount(0);
});

const proposalId = (n: number) =>
  `50000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const proposalLinks = (page: Page) =>
  page.getByRole("link", { name: /^Review proposal / });
const refreshLibrary = (page: Page) =>
  page.getByRole("button", {
    name: "Refresh proposals and access",
    exact: true,
  });
const prepareFeedback = (page: Page) =>
  page.getByRole("button", { name: "Prepare feedback question", exact: true });
const feedbackUrl = (proposal = id, fp = fingerprint) =>
  "/chat?" +
  new URLSearchParams({
    creationProjectId: project,
    creationProposalId: proposal,
    creationFingerprint: fp,
  });

// Angular observes popstate; keep the mounted app/panel and its in-flight work alive.
async function navigateSpa(page: Page, path: string) {
  await page.evaluate((url) => {
    history.pushState(null, "", url);
    dispatchEvent(new PopStateEvent("popstate", { state: null }));
  }, path);
  await expect(page).toHaveURL(
    new RegExp(path.split("?")[0]!.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")),
  );
}

function gate() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

test("Chromium pages by afterProposal in groups of 20 and caps rendering at 200", async ({
  page,
}) => {
  await syntheticCatalog(page);
  await page.route("**/creation-capabilities", (route) =>
    route.fulfill({ json: { available: true } }),
  );
  const cursors: (string | null)[] = [];
  await page.route("**/creation-proposals?*", (route) => {
    const query = new URL(route.request().url()).searchParams;
    expect(query.get("limit")).toBe("20");
    expect([...query.keys()].sort()).toEqual(
      cursors.length ? ["afterProposal", "limit"] : ["limit"],
    );
    const after = query.get("afterProposal");
    expect(after).toBe(cursors.length ? proposalId(cursors.length * 20) : null);
    const start = cursors.length * 20 + 1;
    cursors.push(after);
    const response: ProposalPage = {
      proposals: Array.from({ length: 20 }, (_, i) =>
        row(proposalId(start + i)),
      ),
      afterProposal: proposalId(start + 19),
    };
    return route.fulfill({ json: response });
  });
  await page.goto(root + "/creation");
  for (let count = 20; count <= 200; count += 20) {
    await expect(proposalLinks(page)).toHaveCount(count);
    await expect(proposalLinks(page).last()).toHaveAttribute(
      "href",
      row(proposalId(count)).reviewPath,
    );
    if (count < 200)
      await page.getByRole("button", { name: "Load more proposals" }).click();
  }
  await expect(
    page.getByRole("button", { name: "Load more proposals" }),
  ).toHaveCount(0);
  expect(cursors).toHaveLength(10);
});

for (const fault of ["duplicate", "backward"] as const) {
  test(`Chromium refuses a ${fault} proposal page after the first cursor`, async ({
    page,
  }) => {
    await syntheticCatalog(page);
    await page.route("**/creation-capabilities", (route) =>
      route.fulfill({ json: { available: true } }),
    );
    const reads: string[] = [];
    await page.route("**/creation-proposals?*", (route) => {
      reads.push(route.request().url());
      const more = new URL(route.request().url()).searchParams.get(
        "afterProposal",
      );
      const rows = Array.from({ length: 20 }, (_, i) =>
        row(proposalId((more ? 21 : 1) + i)),
      );
      if (more) {
        expect(more).toBe(proposalId(20));
        if (fault === "duplicate") rows[1] = rows[0]!;
        else rows[0] = row(proposalId(19));
      }
      return route.fulfill({
        json: {
          proposals: rows,
          afterProposal: rows.at(-1)!.proposalId,
        } satisfies ProposalPage,
      });
    });
    await page.goto(root + "/creation");
    await expect(proposalLinks(page)).toHaveCount(20);
    await page.getByRole("button", { name: "Load more proposals" }).click();
    await expect(
      page.getByText(
        "Proposals could not be read under current access. Refresh to retry.",
      ),
    ).toBeVisible();
    await expect(proposalLinks(page)).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Load more proposals" }),
    ).toHaveCount(0);
    expect(reads).toHaveLength(2);
  });
}

test("refresh refuses a changed immutable fingerprint even with a matching new review path", async ({
  page,
}) => {
  await library(page);
  await expect(proposalLinks(page)).toHaveCount(1);
  const changed = hash("substituted retained proposal");
  await page.route("**/creation-proposals?*", (route) =>
    route.fulfill({
      json: {
        proposals: [
          {
            ...row(),
            fingerprint: changed,
            reviewPath: creationReviewPath(org, project, id, changed),
          },
        ],
        afterProposal: null,
      } satisfies ProposalPage,
    }),
  );
  await refreshLibrary(page).click();
  await expect(
    page.getByText(
      "Proposals could not be read under current access. Refresh to retry.",
    ),
  ).toBeVisible();
  await expect(proposalLinks(page)).toHaveCount(0);
});

for (const delayed of [
  "creation-capabilities",
  "creation-proposals?*",
] as const) {
  test(`late ${delayed} response cannot repopulate a replaced library route`, async ({
    page,
  }) => {
    await library(page);
    await expect(proposalLinks(page)).toHaveCount(1);
    const started = gate(),
      release = gate(),
      finished = gate();
    await page.route(
      `**/${delayed}`,
      async (route) => {
        started.release();
        await release.promise;
        await route.fulfill({
          json:
            delayed === "creation-capabilities"
              ? { available: true }
              : { proposals: [row()], afterProposal: null },
        });
        finished.release();
      },
      { times: 1 },
    );
    await refreshLibrary(page).click();
    await started.promise;
    await navigateSpa(page, root);
    await expect(
      page.getByRole("heading", {
        name: "Your new mapping proposals",
        exact: true,
      }),
    ).toHaveCount(0);
    release.release();
    await finished.promise;
    // Return to a fresh instance that has a different, empty authorized page.
    await page.route("**/creation-proposals?*", (route) =>
      route.fulfill({
        json: { proposals: [], afterProposal: null } satisfies ProposalPage,
      }),
    );
    await navigateSpa(page, root + "/creation");
    await expect(
      page.getByText("No retained proposals were found.", { exact: false }),
    ).toBeVisible();
    await expect(proposalLinks(page)).toHaveCount(0);
  });
}

test("late proposal response from the old actor stays hidden after a shared context refresh changes owner", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.route("**/creation-capabilities", (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await page.route("**/creation-proposals?*", (route) =>
    route.fulfill({
      json: { proposals: [row()], afterProposal: null } satisfies ProposalPage,
    }),
  );
  await page.goto(root + "/creation");
  await expect(proposalLinks(page)).toHaveCount(1);
  const started = gate(),
    release = gate(),
    finished = gate();
  await page.route(
    "**/creation-proposals?*",
    async (route) => {
      started.release();
      await release.promise;
      await route.fulfill({
        json: {
          proposals: [row()],
          afterProposal: null,
        } satisfies ProposalPage,
      });
      finished.release();
    },
    { times: 1 },
  );
  await refreshLibrary(page).click();
  await started.promise;
  f.data.context.actorPrincipalId = "b0000000-0000-4000-8000-000000000099";
  // Exercise the real shared store's HTTP refresh while this route stays mounted.
  // Chat's private admission read does not publish into CatalogContextStore.
  await page.evaluate(
    () =>
      new Promise<void>((resolve, reject) => {
        const debug = (
          window as unknown as {
            ng: {
              getComponent(element: Element): {
                contexts: {
                  refresh(): {
                    subscribe(observer: {
                      next(): void;
                      error(error: unknown): void;
                    }): unknown;
                  };
                };
              };
            };
          }
        ).ng;
        const component = debug.getComponent(
          document.querySelector("weft-creation-library")!,
        );
        component.contexts
          .refresh()
          .subscribe({ next: resolve, error: reject });
      }),
  );
  await expect(proposalLinks(page)).toHaveCount(0);
  release.release();
  await finished.promise;
  await expect(refreshLibrary(page)).toBeEnabled();
  await expect(proposalLinks(page)).toHaveCount(0);
  await page.route("**/creation-proposals?*", (route) =>
    route.fulfill({
      json: { proposals: [], afterProposal: null } satisfies ProposalPage,
    }),
  );
  await refreshLibrary(page).click();
  await expect(
    page.getByText("No retained proposals were found.", { exact: false }),
  ).toBeVisible();
});

test("feedback replacement declines preserve the draft and acceptance replaces it without sending", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto(feedbackUrl());
  await prepareFeedback(page).click();
  const question = page.getByLabel("Question", { exact: true });
  await expect(question).toHaveValue(
    feedbackQuestion({ projectId: project, proposalId: id, fingerprint }),
  );
  await question.fill("Keep this unsent work.");
  const before = f.posts.length;
  page.once("dialog", (dialog) => dialog.dismiss());
  await prepareFeedback(page).click();
  await expect(question).toHaveValue("Keep this unsent work.");
  expect(f.posts).toHaveLength(before);
  page.once("dialog", (dialog) => dialog.accept());
  await prepareFeedback(page).click();
  await expect(question).toHaveValue(
    feedbackQuestion({ projectId: project, proposalId: id, fingerprint }),
  );
  expect(f.posts).toHaveLength(before);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
});

test("choosing a legacy conversation drops the feedback tuple instead of constructing a mixed URL", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto(feedbackUrl());
  await page
    .getByRole("button", { name: "Earlier revision conversation", exact: true })
    .click();
  await expect(
    page.locator(`.chat-title[data-conversation-id="${legacyId}"]`),
  ).toBeVisible();
  await expect
    .poll(() => new URL(page.url()).searchParams.get("revisionId"))
    .toBe(f.data.revision.scope.revisionId);
  const query = new URL(page.url()).searchParams;
  expect(query.get("conversationId")).toBe(legacyId);
  expect([...query.keys()].sort()).toEqual([
    "conversationId",
    "mappingId",
    "projectId",
    "revisionId",
  ]);
  await expect(
    page.getByText("This chat link is invalid.", { exact: false }),
  ).toHaveCount(0);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
});

test("preparing feedback with a current legacy conversation creates a workspace conversation", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  await page.goto(`/chat?conversationId=${legacyId}`);
  await expect(
    page.locator(`.chat-title[data-conversation-id="${legacyId}"]`),
  ).toBeVisible();
  await navigateSpa(page, feedbackUrl());
  await prepareFeedback(page).click();
  await expect(page.getByLabel("Question", { exact: true })).toHaveValue(
    feedbackQuestion({ projectId: project, proposalId: id, fingerprint }),
  );
  const created = f.posts.filter((p) => p.path.endsWith("/conversations"));
  expect(created).toHaveLength(1);
  expect(created[0]!.path).toBe(
    `/api/v1/organizations/${org}/chat/conversations`,
  );
  expect(f.conversations.get(created[0]!.body.conversationId)?.kind).toBe(
    "workspace",
  );
  const query = new URL(page.url()).searchParams;
  expect(query.get("creationProposalId")).toBe(id);
  expect(query.has("revisionId")).toBe(false);
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
});

test("delayed workspace creation for feedback A cannot insert its draft after SPA navigation to feedback B", async ({
  page,
}) => {
  const f = await workspaceFixture(page);
  const started = gate(),
    release = gate(),
    finished = gate();
  await page.route(
    `**/organizations/${org}/chat/conversations`,
    async (route) => {
      if (route.request().method() === "POST") {
        started.release();
        await release.promise;
      }
      await route.fallback();
      if (route.request().method() === "POST") finished.release();
    },
  );
  await page.goto(feedbackUrl());
  await prepareFeedback(page).click();
  await started.promise;
  const secondId = proposalId(2),
    secondFingerprint = hash("second exact proposal");
  await navigateSpa(page, feedbackUrl(secondId, secondFingerprint));
  await expect(
    page.getByRole("region", { name: "Feedback on exact mapping proposal" }),
  ).toContainText(secondId);
  release.release();
  await finished.promise;
  await expect(prepareFeedback(page)).toBeEnabled();
  // A route/context boundary may abandon the empty conversation rather than
  // retain it. Both outcomes must leave every mounted composer free of A's draft.
  const drafts = await page
    .locator("textarea[data-chat-composer]")
    .evaluateAll((elements) =>
      elements.map((element) => (element as HTMLTextAreaElement).value),
    );
  expect(drafts.every((draft) => draft === "")).toBe(true);
  await expect(
    page.getByRole("region", { name: "Feedback on exact mapping proposal" }),
  ).toContainText(secondId);
  expect(f.posts.filter((p) => p.path.endsWith("/conversations"))).toHaveLength(
    1,
  );
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
});

test("server creation capability gates the project entry and library reads", async ({
  page,
}) => {
  const state = await library(page, false);
  await expect(
    page.getByText("Mapping creation is not configured for this project."),
  ).toBeVisible();
  expect(state.reads).toHaveLength(0);
  await page.goto(root);
  await expect(
    page.getByRole("link", { name: "Your new mapping proposals", exact: true }),
  ).toHaveCount(0);
  state.available = true;
  await page.reload();
  await expect(
    page.getByRole("link", { name: "Your new mapping proposals", exact: true }),
  ).toBeVisible();
});

test("feedback carries only exact identity and prepares an unsent editable question", async ({
  page,
}) => {
  const f = await workspaceFixture(page, {
    payloadsAllowed: true,
    grant: true,
  });
  const planJson = JSON.stringify({
    schema: "weft.creation-plan.v1",
    format: "json-record",
    source: [{ Name: "code", Type: 1, Required: true }],
    target: [{ Name: "item", Type: 1, Required: true }],
    bindings: [{ Source: "code", Target: "item" }],
  });
  const proposal: CreationProposal = {
    projectId: project,
    proposalId: id,
    fingerprint,
    snapshotDigest: hash("snapshot"),
    planDigest: hash(planJson),
    nativeDigest: hash("native"),
    name: "Retained feedback mapping",
    description: "",
    planJson,
    approved: false,
    evidence: [
      { bundleId: id, sourceId: id, sourceVersion: 1, chunkOrdinal: 0 },
    ],
    assumptions: [],
    questions: [],
    receipt: null,
  };
  await page.route("**/creation-proposals/**", (route) =>
    route.fulfill({ json: proposal }),
  );
  await page.goto(row().reviewPath);
  await expect(
    page.getByRole("heading", { name: proposal.name }),
  ).toBeVisible();
  await page
    .getByLabel("Paste sample input")
    .fill("PRIVATE-SAMPLE-NOT-FEEDBACK");
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("link", { name: "Give feedback in chat", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Revise a new mapping proposal" }),
  ).toBeVisible();
  expect(page.url()).not.toContain("PRIVATE-SAMPLE");
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
  const prepare = page.getByRole("button", {
    name: "Prepare feedback question",
    exact: true,
  });
  await expect(prepare).toBeEnabled();
  await prepare.click();
  const question = page.getByLabel("Question", { exact: true });
  await expect(question).toHaveValue(
    feedbackQuestion({ projectId: project, proposalId: id, fingerprint }),
  );
  expect(await question.inputValue()).not.toContain("PRIVATE-SAMPLE");
  expect(f.posts.filter((p) => p.path.endsWith("/turns"))).toHaveLength(0);
  await question.fill(
    (await question.inputValue()) + "Rename the target item to itemCode.",
  );
  await page
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect
    .poll(() => f.posts.filter((p) => p.path.endsWith("/turns")).length)
    .toBe(1);
  expect(f.posts.at(-1)!.body.text).not.toContain("PRIVATE-SAMPLE");
});
