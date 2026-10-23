import type { Page, Request, Response } from "@playwright/test";

interface ConfirmationOptions {
  confirm: () => Promise<void>;
  recoverUnsentAction?: () => Promise<boolean>;
  onRecovery?: () => void;
  responseTimeoutMs?: number;
}

/** Recover an unavailable control only before any matching mutation has started. */
export async function confirmHostedMutation(
  page: Page,
  suffix: RegExp,
  action: () => Promise<unknown>,
  options: ConfirmationOptions,
): Promise<Response> {
  const matches = (request: Request) =>
    ["PUT", "POST"].includes(request.method()) &&
    suffix.test(new URL(request.url()).pathname);
  let started = false;
  let resolve!: (response: Response) => void;
  let reject!: (error: Error) => void;
  const received = new Promise<Response>((accept, refuse) => {
    resolve = accept;
    reject = refuse;
  });
  // A failed control action may exit before this promise is awaited.
  void received.catch(() => {});
  const request = (value: Request) => {
    if (matches(value)) started = true;
  };
  const response = (value: Response) => {
    if (matches(value.request())) {
      started = true;
      resolve(value);
    }
  };
  const closed = () => reject(new Error("The mutation page closed."));
  let timer: ReturnType<typeof setTimeout> | undefined;
  page.on("request", request);
  page.on("response", response);
  page.on("close", closed);
  try {
    for (let attempt = 1; ; attempt++) {
      try {
        await action();
        break;
      } catch (error) {
        if (started || attempt >= 3 || !options.recoverUnsentAction)
          throw error;
        const recovered = await options.recoverUnsentAction();
        if (started || !recovered) throw error;
        options.onRecovery?.();
      }
    }
    // A confirmation or response failure is never retried by this helper.
    await options.confirm();
    timer = setTimeout(
      () => reject(new Error("No matching mutation response was observed.")),
      options.responseTimeoutMs ?? 120_000,
    );
    return await received;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    page.off("request", request);
    page.off("response", response);
    page.off("close", closed);
  }
}
