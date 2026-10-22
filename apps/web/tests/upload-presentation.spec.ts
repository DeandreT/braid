import { expect, test } from "@playwright/test";
import {
  synthetic,
  syntheticCatalog,
  catalogApi,
  projectPath,
  mappingPath,
} from "./catalog.fixture";

// Controlled presentation only. No upload or persistence result is fabricated.
for (const kind of ["mapping", "version"] as const) {
  for (const state of [
    "empty",
    "loading",
    "readOnly",
    "unavailable",
  ] as const) {
    test(`${kind} upload shows ${state} distinctly without sending a write`, async ({
      page,
    }) => {
      const data = structuredClone(synthetic);
      data.context.features.createMappings = state !== "readOnly";
      data.context.features.uploadRevisions = state !== "readOnly";
      data.project.access.actions.createMappings = state !== "readOnly";
      data.mapping.access.actions.uploadRevisions = state !== "readOnly";
      const project = catalogApi(projectPath(data.project.scope));
      const mapping = catalogApi(mappingPath(data.mapping.scope));
      await syntheticCatalog(page, {
        "/api/v1/catalog/context": data.context,
        [project]: data.project,
        [mapping]: data.mapping,
      });
      const writes: string[] = [];
      page.on("request", (request) => {
        if (["POST", "PUT", "PATCH", "DELETE"].includes(request.method()))
          writes.push(request.url());
      });
      let release = () => {};
      const wait = new Promise<void>((resolve) => {
        release = resolve;
      });
      await page.route(
        "**" + (kind === "mapping" ? project : mapping),
        async (route) => {
          if (state === "loading") await wait;
          await route.fulfill(
            state === "unavailable"
              ? { status: 503, json: { code: "unavailable" } }
              : { json: kind === "mapping" ? data.project : data.mapping },
          );
        },
      );
      try {
        await page.goto(
          kind === "mapping"
            ? projectPath(data.project.scope) + "/mappings/new"
            : mappingPath(data.mapping.scope) + "/revisions/new",
        );
        if (state === "empty") {
          await expect(
            page.getByLabel("Native mapping file", { exact: true }),
          ).toBeEnabled();
          await expect(
            page.getByText("Original mapping file", { exact: true }),
          ).toBeVisible();
        } else {
          await expect(page.locator(`[data-state="${state}"]`)).toBeVisible();
          if (state === "loading")
            await expect(
              page.getByText(
                /Checking current identity and (project|mapping) access/,
              ),
            ).toBeVisible();
          if (state === "readOnly")
            await expect(
              page.getByText(/not (currently )?enabled or permitted/),
            ).toBeVisible();
          if (state === "unavailable")
            await expect(
              page.getByText(/Current access could not be checked/),
            ).toBeVisible();
        }
        await page.setViewportSize({ width: 360, height: 780 });
        await page.addStyleTag({
          content: ":root {font-size:24px !important}",
        });
        expect(
          await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
          ),
        ).toBe(true);
        expect(writes).toEqual([]);
      } finally {
        release();
      }
    });
  }
}
