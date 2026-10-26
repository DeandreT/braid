/** Only work whose original contents or request identity still live solely in this tab. */
export function hasChatUnsentWork(work: {
  readonly draft: string;
  readonly pending: unknown | null;
  readonly pendingConversation: string | null;
}): boolean {
  return (
    work.draft.trim().length > 0 ||
    work.pending !== null ||
    work.pendingConversation !== null
  );
}
