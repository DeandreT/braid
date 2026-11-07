import { expect, test } from "@playwright/test";
import { synthetic, syntheticCatalog } from "./catalog.fixture";

test("reactive project fields validate and dispatch the current draft", async ({
  page,
}) => {
  // API responses are synthetic. No project is persisted by this browser test.
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": {
      ...synthetic.context,
      features: { ...synthetic.context.features, createProjects: true },
      organizationActions: { createProjects: true },
    },
  });
  const bodies: unknown[] = [];
  await page.route(
    `**/api/v1/organizations/${synthetic.org}/projects`,
    async (route) => {
      if (route.request().method() !== "POST") return route.fallback();
      bodies.push(route.request().postDataJSON());
      await route.fulfill({
        status: 503,
        json: { code: "projectCreationAdmissionBusy" },
      });
    },
  );
  await page.goto(`/catalog/organizations/${synthetic.org}/projects/new`);
  const submit = page.getByRole("button", {
    name: "Create project",
    exact: true,
  });
  await expect(submit).toBeEnabled();
  await submit.click();
  await expect(
    page.getByRole("button", { name: "Enter a project name." }),
  ).toBeVisible();
  expect(bodies).toHaveLength(0);
  const name = page.getByLabel("Name", { exact: true });
  await name.fill("x".repeat(201));
  await submit.click();
  await expect(name).toHaveAttribute("aria-invalid", "true");
  expect(bodies).toHaveLength(0);
  await name.fill("Dependency regression project");
  await page
    .getByLabel("Description (optional)", { exact: true })
    .fill("Synthetic draft");
  await submit.click();
  await expect.poll(() => bodies.length).toBe(1);
  expect(bodies[0]).toEqual({
    name: "Dependency regression project",
    description: "Synthetic draft",
  });
  await expect(
    page.getByText("Project creation is busy. No automatic retry was made.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(name).toHaveValue("Dependency regression project");
  expect(bodies).toHaveLength(1);
});
