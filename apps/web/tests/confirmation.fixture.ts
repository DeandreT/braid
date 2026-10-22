import { expect, type Page } from "@playwright/test";

/** Explicitly accept the application dialog; never inject a confirmation result. */
export async function confirmAction(page: Page): Promise<void> {
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await dialog.locator("[data-confirm-action]").click();
  await expect(dialog).not.toBeVisible();
}
