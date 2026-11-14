import { expect, type Page } from "@playwright/test";

/** Read the actual session summary, then dismiss its menu before workspace actions. */
export async function expectSignedInAccount(page: Page): Promise<void> {
  const trigger = page.locator("#account-trigger");
  if ((await trigger.getAttribute("aria-expanded")) !== "true")
    await trigger.click();
  const menu = page.getByRole("region", {
    name: "Sign-in and session",
    exact: true,
  });
  await expect(menu.getByRole("status").first()).toHaveText(
    "Signed in to braid",
  );
  await trigger.click();
  await expect(trigger).toHaveAttribute("aria-expanded", "false");
  await expect(menu).toBeHidden();
}
