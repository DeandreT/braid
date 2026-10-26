import "@angular/compiler";
import { signal, type DestroyRef } from "@angular/core";
import { expect, test } from "@playwright/test";
import { hasChatUnsentWork } from "../src/chat/chat-unsent-work";
import { UnsentWork } from "../src/workbench/unsent-work";

test("only an unsent draft or unacknowledged request needs a leave warning", () => {
  for (const state of [
    { draft: "", pending: null, pendingConversation: null, streaming: true },
    {
      draft: "  \n",
      pending: null,
      pendingConversation: null,
      streaming: false,
    },
    {
      draft: "",
      pending: null,
      pendingConversation: null,
      recoverableTurnId: "saved-turn",
    },
  ])
    expect(hasChatUnsentWork(state)).toBe(false);
  expect(
    hasChatUnsentWork({
      draft: "question",
      pending: null,
      pendingConversation: null,
    }),
  ).toBe(true);
  expect(
    hasChatUnsentWork({
      draft: "",
      pending: { turnId: "unconfirmed" },
      pendingConversation: null,
    }),
  ).toBe(true);
  expect(
    hasChatUnsentWork({
      draft: "",
      pending: null,
      pendingConversation: "unconfirmed",
    }),
  ).toBe(true);
});

test("shared UnsentWork reacts to request acknowledgement and view lifetime, not a server-owned stream", () => {
  const work = new UnsentWork();
  const draft = signal("");
  const pending = signal<unknown | null>(null);
  const callbacks: (() => void)[] = [];
  const lifetime: DestroyRef = {
    destroyed: false,
    onDestroy: (callback) => {
      callbacks.push(callback);
      return () => {};
    },
  };
  work.track(
    () =>
      hasChatUnsentWork({
        draft: draft(),
        pending: pending(),
        pendingConversation: null,
      }),
    lifetime,
  );
  expect(work.present()).toBe(false);
  draft.set("question");
  expect(work.present()).toBe(true);
  pending.set({ turnId: "original-request" });
  draft.set("");
  expect(work.present()).toBe(true);
  pending.set(null); // Acknowledged: the running turn is now recoverable from the server.
  expect(work.present()).toBe(false);
  draft.set("another draft");
  expect(work.present()).toBe(true);
  for (const callback of callbacks) callback();
  expect(work.present()).toBe(false);
});
