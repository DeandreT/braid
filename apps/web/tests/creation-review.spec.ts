import { expect, test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { createHash } from "node:crypto";
import { synthetic, syntheticCatalog } from "./catalog.fixture";
import {
  creationProposal,
  creationOutput,
  creationStateFollows,
  sameCreation,
  type CreationProposal,
} from "../src/catalog/creation/creation-model";

const hash = (value: string) =>
  createHash("sha256").update(value).digest("hex");
const id = "50000000-0000-4000-8000-000000000001";
const other = "50000000-0000-4000-8000-000000000002";
const planJson = JSON.stringify({
  schema: "braid.creation-plan.v1",
  format: "json-record",
  source: [{ Name: "code", Type: 1, Required: true }],
  target: [{ Name: "item", Type: 1, Required: true }],
  bindings: [{ Source: "code", Target: "item" }],
});
function fixture(): CreationProposal {
  return {
    projectId: synthetic.project.scope.projectId,
    proposalId: id,
    fingerprint: hash("proposal"),
    snapshotDigest: hash("snapshot"),
    planDigest: hash(planJson),
    nativeDigest: hash("native"),
    name: "New customer mapping",
    description: "Generated from retained requirements",
    planJson,
    approved: false,
    evidence: [
      { bundleId: other, sourceId: id, sourceVersion: 1, chunkOrdinal: 0 },
    ],
    assumptions: [
      {
        id: "A1",
        text: "Use the customer's supplied item code.",
        critical: true,
      },
    ],
    questions: [],
    receipt: null,
  };
}
const selection = {
  project: fixture().projectId,
  proposal: id,
  fingerprint: fixture().fingerprint,
};
function output(p: CreationProposal, sample: string, text = '{"item":"A42"}') {
  return {
    proposalId: p.proposalId,
    fingerprint: p.fingerprint,
    nativeDigest: p.nativeDigest,
    sampleDigest: hash(sample),
    outcome: "Succeeded",
    format: "json",
    mediaType: "application/json",
    outputDigest: hash(text),
    outputBase64: Buffer.from(text).toString("base64"),
  };
}
const path = `/catalog/organizations/${synthetic.org}/projects/${selection.project}/creation/${id}?fingerprint=${selection.fingerprint}`;

for (const mutation of [
  "scope",
  "fingerprint",
  "plan",
  "assumption",
  "receipt",
  "unknown",
] as const) {
  test(`proposal parser refuses ${mutation} substitution`, async () => {
    const p = fixture();
    if (mutation === "scope") p.projectId = other;
    if (mutation === "fingerprint") p.fingerprint = hash("different");
    if (mutation === "plan") p.planJson += " ";
    if (mutation === "assumption") p.assumptions.push(p.assumptions[0]!);
    if (mutation === "receipt")
      p.receipt = {
        operationId: other,
        phase: "Committed",
        mappingId: other,
        revisionId: id,
      };
    if (mutation === "unknown") Object.assign(p, { extra: true });
    await expect(creationProposal(p, selection)).rejects.toThrow();
  });
}
test("plan versions keep exact bytes and all immutable review facts", async () => {
  const p = fixture();
  expect(await creationProposal(p, selection)).toEqual(p);
  const different = structuredClone(p);
  different.assumptions[0]!.text = "Changed requirement";
  expect(sameCreation(p, different)).toBe(false);
  const v2 = {
    ...p,
    planJson: '{"schema":"braid.creation-plan.v2"}',
    planDigest: hash('{"schema":"braid.creation-plan.v2"}'),
  };
  expect(await creationProposal(v2, selection)).toEqual(v2);
});
for (const mutation of [
  "proposal",
  "sample",
  "native",
  "digest",
  "mime",
  "failed",
  "base64",
] as const) {
  test(`preview parser refuses ${mutation} substitution`, async () => {
    const p = fixture();
    const v = output(p, "sample");
    if (mutation === "proposal") v.fingerprint = hash("other");
    if (mutation === "sample") v.sampleDigest = hash("other");
    if (mutation === "native") v.nativeDigest = hash("other");
    if (mutation === "digest") v.outputDigest = hash("other");
    if (mutation === "mime") v.mediaType = "text/html";
    if (mutation === "failed") v.outcome = "Failed";
    if (mutation === "base64") v.outputBase64 += "\n";
    await expect(creationOutput(v, p, hash("sample"))).rejects.toThrow();
  });
}
test("empty successful output remains distinct from failure", async () => {
  const p = fixture();
  const v = {
    ...output(p, "sample", ""),
    format: "csv",
    mediaType: "text/csv",
  };
  expect((await creationOutput(v, p, hash("sample")))?.text).toBe("");
  expect(
    await creationOutput(
      {
        ...v,
        outcome: "Failed",
        format: null,
        mediaType: null,
        outputDigest: null,
        outputBase64: null,
      },
      p,
      hash("sample"),
    ),
  ).toBeNull();
});

async function setup(page: Page, initial = fixture()) {
  await syntheticCatalog(page);
  const state = {
    p: initial,
    actor: synthetic.actor,
    denied: false,
    uncertain: false,
    substitute: false,
    posts: [] as { action: string; body: Record<string, unknown> }[],
  };
  await page.route("**/api/v1/catalog/context", (r) =>
    r.fulfill({
      json: { ...synthetic.context, actorPrincipalId: state.actor },
    }),
  );
  await page.route("**/creation-proposals/**", async (r) => {
    if (state.denied)
      return r.fulfill({ status: 404, json: { code: "NotFound" } });
    const request = r.request();
    const url = new URL(request.url());
    if (request.method() === "GET") return r.fulfill({ json: state.p });
    const action = url.pathname.split("/").at(-1)!;
    const body = request.postDataJSON() as Record<string, unknown>;
    state.posts.push({ action, body });
    if (action === "preview") {
      const sample = Buffer.from(
        body.sampleBase64 as string,
        "base64",
      ).toString("utf8");
      return r.fulfill({
        json: {
          ...output(state.p, sample),
          ...(state.substitute ? { sampleDigest: hash("other") } : {}),
        },
      });
    }
    if (action === "approve") {
      state.p.approved = true;
      state.p.receipt = {
        operationId: other,
        phase: "Committed",
        mappingId: other,
        revisionId: id,
      };
      if (state.uncertain) return r.abort("failed");
    }
    return r.fulfill({ json: state.p });
  });
  await page.goto(path);
  await expect(page.getByRole("heading", { name: state.p.name })).toBeVisible();
  return state;
}
async function review(page: Page) {
  await page
    .getByLabel("I accept: Use the customer's supplied item code.")
    .check();
  await page
    .getByLabel(
      "I've reviewed this exact mapping plan and its source evidence.",
    )
    .check();
}
test("approval requires the exact review and each critical assumption", async ({
  page,
}) => {
  const state = await setup(page);
  const save = page.getByRole("button", {
    name: "Approve and save this mapping",
  });
  await expect(save).toBeDisabled();
  await page
    .getByLabel(
      "I've reviewed this exact mapping plan and its source evidence.",
    )
    .check();
  await expect(save).toBeDisabled();
  await page
    .getByLabel("I accept: Use the customer's supplied item code.")
    .check();
  await save.click();
  await expect(
    page.getByRole("link", { name: "Open saved revision" }),
  ).toBeVisible();
  expect(state.posts).toEqual([
    {
      action: "approve",
      body: {
        fingerprint: selection.fingerprint,
        acceptedCriticalAssumptions: ["A1"],
      },
    },
  ]);
});
test("critical questions block approval and recovery never sends approval", async ({
  page,
}) => {
  const state = await setup(page);
  state.p.questions.push({ text: "Which unit of measure?", critical: true });
  await page
    .getByRole("button", { name: "Refresh proposal and access" })
    .click();
  // A changed body under the same fingerprint is refused before it can become an approval target.
  await expect(
    page.getByRole("region", { name: "Exact creation proposal" }),
  ).toHaveCount(0);
  await page.reload();
  await review(page);
  await expect(
    page.getByRole("button", { name: "Approve and save this mapping" }),
  ).toBeDisabled();
  await page.getByRole("button", { name: "Check save status" }).click();
  await expect(
    page.getByText(
      "No retained approval or save was found. Review and approve this exact proposal to save it.",
    ),
  ).toBeVisible();
  expect(state.posts).toEqual([
    { action: "recover", body: { fingerprint: selection.fingerprint } },
  ]);
});
test("sample execution binds exact bytes and cannot approve", async ({
  page,
}) => {
  const state = await setup(page);
  const sample = '{"code":"A42"}';
  await page.getByLabel("Paste sample input").fill(sample);
  await page.getByRole("button", { name: "Run sample" }).click();
  await expect(
    page.getByRole("region", { name: "Verified sample output" }).locator("pre"),
  ).toHaveText('{"item":"A42"}');
  expect(state.posts).toEqual([
    {
      action: "preview",
      body: {
        fingerprint: selection.fingerprint,
        sampleDigest: hash(sample),
        sampleBase64: Buffer.from(sample).toString("base64"),
      },
    },
  ]);
  expect(state.p.approved).toBe(false);
  state.substitute = true;
  await page.getByRole("button", { name: "Run sample" }).click();
  await expect(page.getByText(/response could not be verified/)).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Verified sample output" }),
  ).toHaveCount(0);
});
test("lost save response reconciles the original proposal without another approval", async ({
  page,
}) => {
  const state = await setup(page);
  state.uncertain = true;
  await review(page);
  await page
    .getByRole("button", { name: "Approve and save this mapping" })
    .click();
  await expect(
    page.getByText(
      "The save response is unconfirmed. Check its status before another approval.",
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Approve and save this mapping" }),
  ).toBeDisabled();
  await page.getByRole("button", { name: "Check save status" }).click();
  await expect(
    page.getByRole("link", { name: "Open saved revision" }),
  ).toBeVisible();
  expect(state.posts.map((p) => p.action)).toEqual(["approve", "recover"]);
});
test("current denial clears retained source facts, sample, and assumptions", async ({
  page,
}) => {
  const state = await setup(page);
  await page.getByLabel("Paste sample input").fill("private sample");
  await review(page);
  state.denied = true;
  await page
    .getByRole("button", { name: "Refresh proposal and access" })
    .click();
  await expect(
    page.getByText("Proposal unavailable or access denied."),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Exact creation proposal" }),
  ).toHaveCount(0);
  state.denied = false;
  await page
    .getByRole("button", { name: "Refresh proposal and access" })
    .click();
  await expect(page.getByLabel("Paste sample input")).toHaveValue("");
  await expect(
    page.getByLabel("I accept: Use the customer's supplied item code."),
  ).not.toBeChecked();
});
test("owner replacement before dispatch removes private content and sends no action", async ({
  page,
}) => {
  const state = await setup(page);
  await review(page);
  state.actor = other;
  await page
    .getByRole("button", { name: "Approve and save this mapping" })
    .click();
  await expect(
    page.getByRole("region", { name: "Exact creation proposal" }),
  ).toHaveCount(0);
  expect(state.posts).toEqual([]);
});
test("review text renders as inert content and passes accessibility checks", async ({
  page,
}) => {
  const state = await setup(page);
  state.p.assumptions[0]!.text =
    "<img src=x onerror=alert(1)> https://inert.invalid";
  await page.reload();
  await expect(page.getByText(/<img src=x/)).toBeVisible();
  await expect(page.locator("braid-creation-page img")).toHaveCount(0);
  const result = await new AxeBuilder({ page })
    .include("braid-creation-page")
    .analyze();
  expect(result.violations).toEqual([]);
});

test("metadata admits backend Unicode scalar ceilings and refuses excess", async () => {
  for (const character of ["漢", "😀"]) {
    const p = {
      ...fixture(),
      name: character.repeat(200),
      description: character.repeat(4000),
    };
    expect(await creationProposal(p, selection)).toEqual(p);
    await expect(
      creationProposal({ ...p, name: p.name + character }, selection),
    ).rejects.toThrow();
    await expect(
      creationProposal(
        { ...p, description: p.description + character },
        selection,
      ),
    ).rejects.toThrow();
  }
});
test("approval and receipt progress preserves stable and terminal identities", () => {
  const p = {
    ...fixture(),
    approved: true,
    receipt: {
      operationId: other,
      phase: "Committed",
      mappingId: other,
      revisionId: id,
    },
  };
  expect(creationStateFollows(p, structuredClone(p))).toBe(true);
  expect(creationStateFollows(p, { ...p, approved: false })).toBe(false);
  expect(creationStateFollows(p, { ...p, receipt: null })).toBe(false);
  for (const change of [
    { operationId: id },
    { mappingId: id },
    { revisionId: other },
    { phase: "Reserved", mappingId: null, revisionId: null },
  ])
    expect(
      creationStateFollows(p, { ...p, receipt: { ...p.receipt, ...change } }),
    ).toBe(false);
  const reserved = {
    ...p,
    receipt: {
      operationId: other,
      phase: "Reserved",
      mappingId: null,
      revisionId: null,
    },
  };
  expect(creationStateFollows(reserved, p)).toBe(true);
  expect(
    creationStateFollows(reserved, {
      ...p,
      receipt: { ...p.receipt, operationId: id },
    }),
  ).toBe(false);
});
test("repeated refresh cannot replace already observed immutable facts", async ({
  page,
}) => {
  const state = await setup(page);
  state.p.assumptions[0]!.text = "Substituted requirement";
  for (let i = 0; i < 2; ++i) {
    await page
      .getByRole("button", { name: "Refresh proposal and access" })
      .click();
    await expect(
      page.getByText(/response could not be verified/),
    ).toBeVisible();
    await expect(
      page.getByRole("region", { name: "Exact creation proposal" }),
    ).toHaveCount(0);
    await expect(page.getByText("Substituted requirement")).toHaveCount(0);
  }
});
for (const reserved of [false, true]) {
  test(`reconciled retained approval permits deliberate same-proposal retry (reserved=${reserved})`, async ({
    page,
  }) => {
    const p = fixture();
    p.approved = true;
    if (reserved)
      p.receipt = {
        operationId: other,
        phase: "Reserved",
        mappingId: null,
        revisionId: null,
      };
    const state = await setup(page, p);
    const retry = page.getByRole("button", {
      name: "Retry saving this exact mapping",
    });
    await expect(retry).toHaveCount(0);
    await page.getByRole("button", { name: "Check save status" }).click();
    await expect(retry).toBeVisible();
    await expect(retry).toBeDisabled();
    expect(state.posts.map((p) => p.action)).toEqual(["recover"]);
    await review(page);
    await retry.click();
    await expect(
      page.getByRole("link", { name: "Open saved revision" }),
    ).toBeVisible();
    expect(state.posts).toEqual([
      { action: "recover", body: { fingerprint: selection.fingerprint } },
      {
        action: "approve",
        body: {
          fingerprint: selection.fingerprint,
          acceptedCriticalAssumptions: ["A1"],
        },
      },
    ]);
  });
}
test("recovery refuses a substituted committed revision link", async ({
  page,
}) => {
  const p = fixture();
  p.approved = true;
  p.receipt = {
    operationId: other,
    phase: "Committed",
    mappingId: other,
    revisionId: id,
  };
  const state = await setup(page, p);
  const link = page.getByRole("link", { name: "Open saved revision" });
  await expect(link).toHaveAttribute(
    "href",
    new RegExp(`/mappings/${other}/revisions/${id}$`),
  );
  state.p.receipt!.revisionId = other;
  await page.getByRole("button", { name: "Check save status" }).click();
  await expect(page.getByText(/response could not be verified/)).toBeVisible();
  await expect(link).toHaveAttribute(
    "href",
    new RegExp(`/mappings/${other}/revisions/${id}$`),
  );
});
