import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { expectSignedInAccount } from "./hosted-account.fixture";

// Runs only on acceptance-test.sh's private Session/API/worker/PostgreSQL stack.
// No API interception, substituted store or model endpoint is used here.
const work = process.env.WEFT_ACCEPTANCE_WORK!;
const organization = process.env.WEFT_SESSION_ORGANIZATION_ID!;
const captures = process.env.WEFT_EDI_CAPTURE_DIRECTORY;
const baseline = process.env.WEFT_EDI_CAPTURE_BASELINE === "true";
const envelopes = {
  x12: "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~",
  edifact:
    "UNB+UNOC:3+WEFTSENDER+WEFTRECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+WEFT-330'UNT+3+330'UNZ+1+330'",
};

function native(family: keyof typeof envelopes): Buffer {
  let id = 0;
  const nodes: Record<string, unknown> = {};
  const scope = (
    target_field: string,
    bindings: unknown[],
    children: unknown[],
  ) => ({ target_field, bindings, children });
  const group = (name: string, children: unknown[]) => ({
    name,
    kind: { kind: "group", children },
  });
  const scalar = (name: string) => ({
    name,
    kind: { kind: "scalar", ty: "string" },
  });
  const bind = (target_field: string, path: string[]) => {
    nodes[++id] = { kind: "source_field", path };
    return { target_field, node: id };
  };
  const scopes: unknown[] = [];
  const segments = envelopes[family]
    .split(family === "x12" ? "~" : "'")
    .filter(Boolean)
    .map((segment) => {
      const [tag, ...elements] = segment.split(family === "x12" ? "*" : "+");
      const bindings: unknown[] = [],
        children: unknown[] = [];
      const fields = elements.map((value, index) => {
        const field = `e${index + 1}`;
        if (family === "edifact" && value.includes(":")) {
          const parts = value.split(":").map((_, part) => `c${part + 1}`);
          children.push(
            scope(
              field,
              parts.map((part) => bind(part, [tag, field, part])),
              [],
            ),
          );
          return group(field, parts.map(scalar));
        }
        bindings.push(bind(field, [tag, field]));
        return scalar(field);
      });
      scopes.push(scope(tag, bindings, children));
      return group(tag, fields);
    });
  const schema = group("Interchange", segments);
  return Buffer.from(
    JSON.stringify({
      source: schema,
      target: schema,
      source_options: { edi_kind: family },
      target_options: { edi_kind: family },
      graph: { nodes },
      root: scope("", [], scopes),
    }),
  );
}

async function downloaded(
  page: Page,
  action: () => Promise<unknown>,
): Promise<Buffer> {
  const pending = page.waitForEvent("download");
  await action();
  const stream = await (await pending).createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  return Buffer.concat(chunks);
}

async function capture(page: Page, name: string): Promise<void> {
  if (!captures) return;
  mkdirSync(captures, { recursive: true });
  await page.screenshot({ path: `${captures}/${name}.png`, fullPage: true });
}

test("declared EDI preview and saved comparison use real bytes in the authenticated workbench", async ({
  page,
}) => {
  test.setTimeout(1_500_000);
  expect(work).toBeTruthy();
  await page.goto("/");
  await page
    .getByRole("region", { name: "Sign in to braid" })
    .getByRole("button", { name: "Sign in with Microsoft", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Sign in as the acceptance user" })
    .click();
  await expectSignedInAccount(page);
  await page.goto(`/catalog/organizations/${organization}/projects`);
  await page.getByRole("link", { name: "Create project", exact: true }).click();
  await page
    .getByRole("textbox", { name: "Name", exact: true })
    .fill("EDI document acceptance");
  await page
    .getByRole("button", { name: "Create project", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Project creation confirmed" }),
  ).toBeVisible();
  const project = (await page
    .locator(".creation-facts code")
    .first()
    .textContent())!.trim();
  writeFileSync(`${work}/project-id`, project);
  for (const family of ["x12", "edifact"] as const) {
    const mediaType =
      family === "x12" ? "application/edi-x12" : "application/edifact";
    const terminator = family === "x12" ? "~" : "'";
    const expected = envelopes[family]
      .split(terminator)
      .filter(Boolean)
      .map((segment) => `${segment}${terminator}\n`)
      .join("");
    await page.goto(
      `/catalog/organizations/${organization}/projects/${project}/mappings/new`,
    );
    await page
      .getByRole("textbox", { name: "Name", exact: true })
      .fill(`${family} envelope`);
    await page
      .getByLabel("Native mapping file", { exact: true })
      .setInputFiles({
        name: `${family}.json`,
        mimeType: "application/json",
        buffer: native(family),
      });
    await page
      .getByRole("button", { name: "Upload mapping", exact: true })
      .click();
    const saved = page.getByRole("region", {
      name: "Mapping saved",
      exact: true,
    });
    await expect(saved).toBeVisible({ timeout: 300_000 });
    const path = (await saved
      .getByRole("link", { name: "View revision", exact: true })
      .getAttribute("href"))!;
    await page.goto(path);
    await page.getByRole("tab", { name: "Try sample", exact: true }).click();
    const preview = page.getByRole("region", {
      name: "Sample preview",
      exact: true,
    });
    await preview
      .getByLabel("Preview admission", { exact: true })
      .selectOption("document");
    await preview
      .getByLabel("Primary input (document)", { exact: true })
      .fill(envelopes[family]);
    await preview
      .getByRole("button", { name: "Run preview", exact: true })
      .click();
    await expect(
      preview
        .getByRole("status")
        .filter({ hasText: "Sample ran successfully" }),
    ).toBeVisible({
      timeout: 300_000,
    });
    await expect(preview).toContainText(
      "dddc4dce6c93f4137fb2aa2a011360d089e0a51b",
    );
    const output = preview.getByRole("region", {
      name: "Preview outputs",
      exact: true,
    });
    await output
      .getByRole("button", { name: "Show Interchange", exact: true })
      .click();
    if (baseline) {
      await expect(preview.locator(".preview-output")).toContainText(
        "WEFT-330",
      );
      await preview.locator(".preview-output").scrollIntoViewIfNeeded();
    } else {
      await expect(
        page.getByRole("region", { name: "Document bytes", exact: true }),
      ).toContainText("WEFT-330");
      await expect(preview.locator(".preview-output")).toHaveCount(0);
      const viewer = page.getByRole("region", {
        name: "Document bytes",
        exact: true,
      });
      await expect(viewer.getByRole("listitem")).toHaveCount(
        family === "x12" ? 7 : 5,
      );
      await viewer
        .getByRole("button", { name: "Raw text", exact: true })
        .click();
      await expect(viewer.locator("pre")).toHaveText(expected);
      await viewer
        .getByRole("button", { name: "EDI segments", exact: true })
        .click();
    }
    await capture(page, `${family}-preview`);
    const bytes = await downloaded(page, () =>
      output
        .getByRole("button", { name: "Download Interchange", exact: true })
        .click(),
    );
    expect(bytes.toString()).toBe(expected);
    await page.getByRole("tab", { name: "Tests", exact: true }).click();
    const cases = page.getByRole("region", {
      name: "Regression tests",
      exact: true,
    });
    await cases
      .getByRole("button", { name: "New test case", exact: true })
      .click();
    await cases
      .getByLabel("Test name", { exact: true })
      .fill(`${family} ordered envelope`);
    await cases
      .getByLabel("Test date and time (UTC or offset)", { exact: true })
      .fill("2026-10-24T12:00:00Z");
    await cases.getByLabel("Execution profile", { exact: true }).selectOption({
      label: "Declared document formats (JSON / X12 / EDIFACT)",
    });
    await cases
      .getByLabel("Comparison policy", { exact: true })
      .selectOption("weft.regression.edi-segments.v1");
    await cases
      .getByLabel("Compare service separators", { exact: true })
      .check();
    await cases
      .getByLabel("Input UTF-8 text", { exact: true })
      .fill(envelopes[family]);
    await cases
      .getByLabel("Expected media type", { exact: true })
      .selectOption(mediaType);
    await cases
      .getByLabel("Expected UTF-8 text", { exact: true })
      .fill(expected);
    const put = page.waitForResponse(
      (response) =>
        response.request().method() === "PUT" &&
        /\/regression-cases\/[^/]+\/versions\/1$/.test(
          new URL(response.url()).pathname,
        ),
    );
    await cases
      .getByRole("button", { name: "Save test version", exact: true })
      .click();
    const response = await put;
    expect(response.status()).toBe(201);
    const definition = response.request().postDataJSON();
    expect(definition.comparisonOptions).toEqual({
      compareSeparators: true,
      ignoreControlNumbers: false,
      ignoreDates: false,
    });
    expect(definition.expected[0].mediaType).toBe(mediaType);
    await cases
      .getByRole("button", { name: "Run saved version", exact: true })
      .click();
    const result = cases.getByRole("region", {
      name: "Regression run result",
      exact: true,
    });
    await expect(result).toContainText("Outcome: passed", { timeout: 300_000 });
    if (baseline) {
      await result
        .getByRole("button", { name: "View actual primary", exact: true })
        .click();
      await cases
        .getByRole("region", { name: "Output text preview", exact: true })
        .scrollIntoViewIfNeeded();
    } else {
      await page.getByRole("tab", { name: "Result", exact: true }).click();
      await expect(
        page.getByRole("region", { name: "Document bytes", exact: true }),
      ).toContainText("WEFT-330");
      expect(
        await downloaded(page, () =>
          page
            .getByRole("button", { name: "Download exact bytes", exact: true })
            .click(),
        ),
      ).toEqual(bytes);
    }
    await capture(page, `${family}-regression`);
    if (!baseline && family === "x12") {
      const viewport = page.viewportSize()!;
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(
        page.getByRole("region", { name: "Document bytes", exact: true }),
      ).toBeVisible();
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBe(true);
      await capture(page, "x12-regression-mobile");
      await page.setViewportSize(viewport);
    }
    await cases
      .getByRole("button", { name: "Edit as next version", exact: true })
      .click();
    await expect(
      cases.getByLabel("Comparison policy", { exact: true }),
    ).toHaveValue("weft.regression.edi-segments.v1");
    await expect(
      cases.getByLabel("Compare service separators", { exact: true }),
    ).toBeChecked();
    await expect(
      cases.getByLabel("Expected media type", { exact: true }),
    ).toHaveValue(mediaType);
    await cases
      .getByRole("button", { name: "Save test version", exact: true })
      .click();
    await expect(
      cases.getByRole("heading", {
        name: `${family} ordered envelope · Version 2`,
        exact: true,
      }),
    ).toBeVisible();
  }
  writeFileSync(
    `${work}/acceptance-facts.json`,
    JSON.stringify({
      families: Object.keys(envelopes),
      backend: "actual",
      baseline,
    }),
  );
});
