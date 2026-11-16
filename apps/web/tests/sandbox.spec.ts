import { test, expect, type Page } from "@playwright/test";
import {
  synthetic,
  syntheticCatalog,
  mappingPath,
  catalogApi,
} from "./catalog.fixture";

const target = "70000000-0000-4000-8000-000000000001";
const environment = "70000000-0000-4000-8000-000000000002";
const build = "70000000-0000-4000-8000-000000000003";
const first = "70000000-0000-4000-8000-000000000004";
const second = "70000000-0000-4000-8000-000000000005";
const qualification = "70000000-0000-4000-8000-000000000006";
const caseReference = "70000000-0000-4000-8000-000000000007/1";
const mapping = catalogApi(mappingPath(synthetic.mapping.scope));
const base64 = (text: string) => Buffer.from(text).toString("base64");
const uuid =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

interface Message {
  messageId: string;
  releaseId: string;
  source: string;
  replayOf: string | null;
  input: string;
  output: string;
}

/** Synthetic browser boundary only. Real sandbox deployment and invocation have service and live tests. */
async function fixture(page: Page) {
  const state = {
    enabled: true,
    targetEnabled: true,
    active: first as string | null,
    version: 3,
    failure: "" as "" | "lost",
    effects: new Set<string>(),
    messages: [] as Message[],
    sends: [] as { url: string; body: string; token: string }[],
    deploys: [] as { url: string; body: string }[],
    actions: [] as { url: string; body: string; etag: string }[],
    deployTarget: "",
    extraTarget: "",
  };
  const targetRow = (id = target) => ({
    targetId: id,
    environmentId: environment,
    environmentVersion: "1",
    enabled: state.targetEnabled,
    activeReleaseId: state.active,
    version: String(state.version),
    runtimeImage: "sha256:" + "f".repeat(64),
  });
  const view = (message: Message, contents: boolean) => ({
    messageId: message.messageId,
    targetId: target,
    sentAtUtc: "2026-11-09T12:00:00.0000000Z",
    durationMs: 42,
    status: "completed",
    outcome: "completed",
    replayed: false,
    releaseId: message.releaseId,
    buildId: build,
    source: message.source,
    replayOf: message.replayOf,
    inputs: [
      {
        name: null,
        mediaType: null,
        sha256: "a".repeat(64),
        content: contents ? base64(message.input) : null,
      },
    ],
    parameters: contents ? [] : null,
    outputs: [
      {
        name: null,
        mediaType: null,
        sha256:
          message.output === '{"total":3}' ? "b".repeat(64) : "c".repeat(64),
        content: contents ? base64(message.output) : null,
      },
    ],
  });
  await syntheticCatalog(page);
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (path === "/api/v1/health")
      return route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      });
    if (path === mapping.split("/mappings/")[0])
      return route.fulfill({
        json: {
          ...synthetic.project,
          access: { ...synthetic.project.access, role: "administrator" },
        },
      });
    if (path.startsWith(mapping + "/operations/")) {
      if (request.method() === "POST") {
        state.actions.push({
          url: path,
          body: request.postData()!,
          etag: request.headers()["if-match"]!,
        });
        const body = JSON.parse(request.postData()!);
        state.version++;
        if (body.action === "rollback") state.active = body.sourceReleaseId;
        if (body.action === "disable") state.targetEnabled = false;
        return route.fulfill({
          json: { operationId: path.split("/").at(-1), outcome: "applied" },
        });
      }
      return route.fulfill({
        json: {
          items: [first, second].map((id) => ({
            kind: "release",
            release_id: id,
            actor_id: synthetic.actor,
            qualification_id: qualification,
            rollback_of_id: null,
            phase: "active",
            rejection: "none",
            created_at: "2026-11-09T11:00:00Z",
            finished_at: "2026-11-09T11:00:01Z",
            build_id: build,
            artifact_digest: "d".repeat(64),
            runtime_image: "sha256:" + "f".repeat(64),
            approval_id: qualification,
            approval_revoked: false,
            approval_created_at: "2026-11-09T10:00:00Z",
          })),
          nextOffset: null,
        },
      });
    }
    if (!path.startsWith(mapping + "/sandbox")) return route.fallback();
    if (!state.enabled)
      return route.fulfill({ status: 404, json: { code: "notFound" } });
    if (path === mapping + "/sandbox")
      return route.fulfill({
        json: {
          mode: "localSimulation",
          targets: [
            targetRow(),
            ...(state.extraTarget ? [targetRow(state.extraTarget)] : []),
          ],
          builds: [
            {
              buildId: build,
              revisionId: synthetic.revision.scope.revisionId,
              environmentId: environment,
              environmentVersion: "1",
              createdAtUtc: "2026-11-09T10:00:00.0000000Z",
            },
          ],
          samples: [
            { kind: "case", reference: caseReference, name: "Order sample" },
          ],
        },
      });
    if (path.startsWith(mapping + "/sandbox/deployments/")) {
      state.deploys.push({ url: path, body: request.postData()! });
      state.extraTarget = state.deployTarget;
      return route.fulfill({
        json: {
          deploymentId: path.split("/").at(-1),
          outcome: "activated",
          target: targetRow(state.extraTarget || target),
          releaseId: state.active,
          qualificationId: qualification,
        },
      });
    }
    const requestedTarget = path
      .slice((mapping + "/sandbox/").length)
      .split("/")[0];
    const messages = `${mapping}/sandbox/${requestedTarget}/messages`;
    if (path === messages)
      return route.fulfill({
        json: { items: state.messages.map((value) => view(value, false)) },
      });
    const id = path.slice(messages.length + 1);
    if (request.method() === "GET") {
      const found = state.messages.find((value) => value.messageId === id);
      return found
        ? route.fulfill({ json: view(found, true) })
        : route.fulfill({ status: 404, json: { code: "notFound" } });
    }
    state.sends.push({
      url: path,
      body: request.postData()!,
      token: request.headers()["x-xsrf-token"]!,
    });
    const body = JSON.parse(request.postData()!);
    let message = state.messages.find((value) => value.messageId === id);
    if (!message) {
      const original = state.messages.find(
        (value) => value.messageId === body.replayOf,
      );
      message = {
        messageId: id,
        releaseId: state.active!,
        source: body.replayOf
          ? `replay of ${body.replayOf}`
          : "case Order sample (v1)",
        replayOf: body.replayOf,
        input: original?.input ?? '{"items":[1,2]}',
        output: state.active === first ? '{"total":3}' : '{"total":2}',
      };
      state.messages.unshift(message);
      state.effects.add(id);
    }
    const failure = state.failure;
    state.failure = "";
    if (failure === "lost") return route.abort("connectionreset");
    return route.fulfill({ json: view(message, true) });
  });
  await page.goto(mappingPath(synthetic.mapping.scope) + "/sandbox");
  await expect(
    page.getByRole("heading", { name: "Local sandbox", exact: true }),
  ).toBeVisible({ timeout: 20_000 });
  return state;
}

test("deploy, send a saved case, replay after rollback and compare releases", async ({
  page,
}) => {
  const state = await fixture(page);
  page.on("dialog", (dialog) => dialog.accept());
  await expect(page.getByRole("note")).toContainText("Simulation");
  await page.getByRole("button", { name: "Deploy to sandbox" }).click();
  await expect(page.getByText("Deployed to the local sandbox.")).toBeVisible();
  expect(state.deploys).toHaveLength(1);
  expect(state.deploys[0]!.url.split("/").at(-1)).toMatch(uuid);
  expect(JSON.parse(state.deploys[0]!.body)).toEqual({ buildId: build });

  await page.getByRole("button", { name: "Send to active release" }).click();
  await expect(page.getByText("Message completed.")).toBeVisible();
  expect(JSON.parse(state.sends[0]!.body)).toEqual({
    source: "case",
    reference: caseReference,
    inputs: null,
    parameters: null,
    replayOf: null,
  });
  expect(state.sends[0]!.token).toBe(synthetic.context.requestToken);
  const detail = page.getByRole("region", { name: /^Message / });
  await expect(detail.locator("pre").last()).toHaveText('{"total":3}');

  await page
    .getByRole("combobox", { name: "Roll back to" })
    .selectOption(second);
  await page.getByRole("button", { name: "Roll back", exact: true }).click();
  await expect(page.getByText("Operation recorded.")).toBeVisible();
  expect(state.actions).toHaveLength(1);
  expect(state.actions[0]!.etag).toBe('"3"');
  expect(JSON.parse(state.actions[0]!.body)).toEqual({
    action: "rollback",
    environmentId: environment,
    environmentVersion: "1",
    qualificationId: qualification,
    sourceReleaseId: second,
  });
  await expect(page.getByRole("definition").first()).toHaveText(second);

  const original = state.messages[0]!.messageId;
  await page.getByRole("button", { name: "Replay" }).click();
  await expect(page.getByText("Message completed.")).toBeVisible();
  expect(JSON.parse(state.sends[1]!.body)).toEqual({
    source: "replay",
    replayOf: original,
    reference: null,
    inputs: null,
    parameters: null,
  });
  await expect(detail.locator("pre").last()).toHaveText('{"total":2}');

  const boxes = page.getByRole("checkbox", { name: /^Compare / });
  await expect(boxes).toHaveCount(2);
  for (const box of await boxes.all()) await box.check();
  await page.getByRole("button", { name: "Compare selected outputs" }).click();
  const comparison = page.getByRole("region", { name: "Output comparison" });
  await expect(
    comparison.getByRole("heading", { name: "Primary output: different" }),
  ).toBeVisible();
  await expect(comparison).toContainText(first);
  await expect(comparison).toContainText(second);
});

test("an uncertain send retries its original message ID and body", async ({
  page,
}) => {
  const state = await fixture(page);
  await page.getByRole("combobox", { name: "Sample" }).selectOption("inline");
  await page.getByLabel("Primary input").fill('{"items":[5]}');
  state.failure = "lost";
  await page.getByRole("button", { name: "Send to active release" }).click();
  const retry = page.getByRole("button", { name: "Retry original request" });
  await expect(retry).toBeEnabled();
  await expect(
    page.getByRole("button", { name: "Send to active release" }),
  ).toBeDisabled();
  await retry.click();
  await expect(page.getByText("Message completed.")).toBeVisible();
  await expect(retry).toHaveCount(0);
  expect(state.sends).toHaveLength(2);
  expect(state.sends[1]).toEqual(state.sends[0]);
  expect(state.effects.size).toBe(1);
  expect(JSON.parse(state.sends[0]!.body)).toMatchObject({
    source: "inline",
    inputs: [{ name: null, content: base64('{"items":[5]}') }],
  });
});

test("deploying a build for a new target selects its active release instead of the old inbox", async ({
  page,
}) => {
  const state = await fixture(page);
  state.deployTarget = "70000000-0000-4000-8000-000000000008";
  await page.getByRole("button", { name: "Deploy to sandbox" }).click();
  await expect(page.getByText("Deployed to the local sandbox.")).toBeVisible();
  await expect(
    page.getByRole("combobox", { name: "Sandbox target" }),
  ).toHaveValue(state.deployTarget);
  await expect(
    page.getByRole("button", { name: "Send to active release" }),
  ).toBeEnabled();
});

test("disable goes through the operations API and a disabled instance says so", async ({
  page,
}) => {
  const state = await fixture(page);
  page.on("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "Disable target" }).click();
  await expect(
    page.getByRole("button", { name: "Enable target" }),
  ).toBeVisible();
  expect(JSON.parse(state.actions[0]!.body)).toMatchObject({
    action: "disable",
    qualificationId: null,
    sourceReleaseId: null,
  });
  state.enabled = false;
  await page.reload();
  await expect(
    page.getByText("The local sandbox is not enabled here."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Deploy to sandbox" }),
  ).toHaveCount(0);
});

test("uploaded bytes survive the inbox and the page uses dark theme tokens", async ({
  page,
}, testInfo) => {
  await page.addInitScript(() =>
    localStorage.setItem("braid.theme.v1", "dark"),
  );
  const state = await fixture(page);
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.getByRole("combobox", { name: "Sample" }).selectOption("inline");
  const bytes = Buffer.from([0xff, 0, 0xc3, 0x28, 0x0a]);
  await page.getByLabel("Or upload a file").setInputFiles({
    name: "sample.bin",
    mimeType: "application/octet-stream",
    buffer: bytes,
  });
  await page.getByRole("button", { name: "Send to active release" }).click();
  await expect(page.getByText("Message completed.")).toBeVisible();
  expect(JSON.parse(state.sends[0]!.body).inputs[0].content).toBe(
    bytes.toString("base64"),
  );
  const colors = await page.locator("body").evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      background: style.backgroundColor,
      expected: style.getPropertyValue("--page").trim(),
    };
  });
  expect(colors.expected).not.toBe("");
  await page
    .getByRole("heading", { name: "Local sandbox", exact: true })
    .scrollIntoViewIfNeeded();
  await page.screenshot({
    path: testInfo.outputPath("sandbox-dark.png"),
    fullPage: true,
  });
});
