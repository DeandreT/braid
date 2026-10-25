import { test, expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import AxeBuilder from "@axe-core/playwright";
import type { components } from "../src/api/generated";

type Scope = components["schemas"]["CatalogRevisionScopeV1"];
const manifest = JSON.parse(
  readFileSync(process.env.WEFT_PREVIEW_BROWSER_MANIFEST!, "utf8"),
) as { schemaVersion: string; editor: Scope };
if (
  manifest.schemaVersion !== "weft.preview-browser-manifest.v1" ||
  !manifest.editor
)
  throw new Error("The owned fixture must enable editing.");
const revisionPath = (scope: Scope) =>
  `/catalog/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`;
const editor = (page: Page) => page.locator("weft-mapping-editor");
async function open(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Edit mapping", exact: true }).click();
  await expect(editor(page).getByRole("status")).toContainText("draft loaded", {
    ignoreCase: true,
  });
}
async function bind(page: Page, value: string): Promise<void> {
  await editor(page)
    .getByRole("button", { name: /Inspect field.*code/ })
    .focus();
  await page.keyboard.press("Enter");
  await editor(page).getByLabel("Exact value", { exact: true }).fill(value);
  await editor(page)
    .getByRole("button", { name: "Apply mapping", exact: true })
    .click();
  await expect(editor(page).getByRole("status")).toHaveText("Draft autosaved.");
}

test("actual draft edit, reload, checkpoint, semantic diff, preview, save and explicit conflict/rebase", async ({
  page,
}, info) => {
  await page.goto(revisionPath(manifest.editor));
  await expect(
    page.getByRole("button", { name: "Edit mapping", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: info.outputPath("mapping-before.png"),
    fullPage: true,
    animations: "disabled",
  });
  await open(page);
  await bind(page, "edited-live");
  await page.reload();
  await open(page);
  await expect(editor(page)).toContainText("Map field");
  await editor(page)
    .getByRole("button", { name: "Check issues", exact: true })
    .click();
  await expect(editor(page).getByRole("status")).toContainText(
    "Worker checkpoint:",
  );
  await editor(page)
    .getByRole("button", { name: "Compare with base", exact: true })
    .click();
  await expect(
    editor(page).getByRole("heading", { name: "Compared with exact base" }),
  ).toBeVisible();
  await editor(page)
    .getByText("Try a sample against this checkpoint", { exact: true })
    .click();
  await editor(page)
    .getByLabel("Primary sample (text)")
    .fill('{"code":"original","a.b":"a","nested":{"b":"b"}}');
  await editor(page)
    .getByRole("button", { name: "Try sample", exact: true })
    .click();
  await expect(editor(page).getByRole("status")).toContainText(
    "Sample preview queued",
  );
  await expect(async () => {
    await editor(page)
      .getByRole("button", { name: "Read preview progress" })
      .click();
    await expect(
      editor(page).getByRole("button", { name: "Read output 0" }),
    ).toBeVisible({ timeout: 1000 });
  }).toPass({ intervals: [3000], timeout: 180_000 });
  await editor(page).getByRole("button", { name: "Read output 0" }).click();
  await expect(editor(page).getByLabel("Preview output")).toContainText(
    "edited-live",
  );
  expect(
    (await new AxeBuilder({ page }).include("weft-mapping-editor").analyze())
      .violations,
  ).toEqual([]);
  await page.screenshot({
    path: info.outputPath("mapping-edited.png"),
    fullPage: true,
  });
  await editor(page)
    .getByRole("button", { name: "Save new version", exact: true })
    .click();
  await expect(page).not.toHaveURL(revisionPath(manifest.editor));
  await expect(
    page.getByRole("heading", { name: "Version 2", exact: true }),
  ).toBeVisible();

  // Explicitly start on the old revision, never silently on the new head.
  await page.goto(revisionPath(manifest.editor));
  await open(page);
  await editor(page)
    .getByRole("button", { name: /Inspect field.*code/ })
    .focus();
  await page.keyboard.press("Enter");
  await editor(page)
    .getByRole("button", { name: "Remove mapping", exact: true })
    .click();
  await expect(editor(page).getByRole("status")).toHaveText("Draft autosaved.");
  await editor(page)
    .getByRole("button", { name: "Save new version", exact: true })
    .click();
  await expect(
    editor(page).getByRole("heading", { name: "Nothing was silently rebased" }),
  ).toBeVisible();
  await editor(page)
    .getByRole("button", { name: "Rebase onto latest observed version" })
    .click();
  await expect(editor(page).getByRole("status")).toContainText("draft loaded", {
    ignoreCase: true,
  });
  await editor(page)
    .getByRole("button", { name: "Save new version", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Version 3", exact: true }),
  ).toBeVisible();
});
