import type { Page, Request } from "@playwright/test";

interface ReadOptions {
  recover: () => Promise<boolean>;
  onRecovery?: () => void;
  maximumRecoveries?: number;
}

/** Reopen existing state after a recognized read failure, never after a mutation. */
export async function readHostedState(
  page: Page,
  read: () => Promise<void>,
  options: ReadOptions,
): Promise<void> {
  const maximum = options.maximumRecoveries ?? 3;
  if (!Number.isInteger(maximum) || maximum < 0 || maximum > 3)
    throw new Error("Hosted read recovery must be bounded.");
  let mutationObserved = false;
  const observe = (request: Request) => {
    if (!["GET", "HEAD", "OPTIONS"].includes(request.method()))
      mutationObserved = true;
  };
  page.on("request", observe);
  try {
    for (let attempt = 0; ; attempt++) {
      try {
        await read();
        if (mutationObserved)
          throw new Error("A hosted read unexpectedly sent a mutation.");
        return;
      } catch (error) {
        if (mutationObserved || attempt === maximum) throw error;
        const recovered = await options.recover();
        if (mutationObserved || !recovered) throw error;
        options.onRecovery?.();
      }
    }
  } finally {
    page.off("request", observe);
  }
}
