import { expect, type Locator } from "@playwright/test";

/** Presentation-only helpers. Conversation and turn requests still use the real UI. */
export const historyTrigger = (chat: Locator) => chat.locator(".chat-title");

export async function openHistory(chat: Locator): Promise<Locator> {
  const trigger = historyTrigger(chat);
  if ((await trigger.getAttribute("aria-expanded")) !== "true")
    await trigger.click();
  return chat.locator("#chat-history-menu");
}

export async function chooseSavedConversation(
  chat: Locator,
  id: string,
): Promise<void> {
  const history = await openHistory(chat);
  await history.locator(`button[data-conversation-id="${id}"]`).click();
}

export async function openMore(chat: Locator): Promise<void> {
  const trigger = chat.getByRole("button", {
    name: "More chat actions",
    exact: true,
  });
  if ((await trigger.getAttribute("aria-expanded")) !== "true")
    await trigger.click();
}

export async function refreshChat(chat: Locator): Promise<void> {
  await openMore(chat);
  await chat.getByRole("button", { name: "Refresh chat", exact: true }).click();
}

export async function openFocus(chat: Locator): Promise<void> {
  const add = chat.getByRole("button", {
    name: /focus for the next conversation/i,
  });
  await add.click();
  await expect(chat.getByLabel("Find mapping")).toBeVisible();
}
