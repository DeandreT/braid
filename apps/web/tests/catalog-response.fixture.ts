import type { Page, Request, Response } from "@playwright/test";

/**
 * Read the real bytes at Chromium's response boundary. The renderer can abort
 * Angular's fetch during subscription teardown after consuming the response,
 * at which point a Playwright response-body handle may already be unusable.
 * Continue the original response unchanged; this never refetches or fulfills it.
 */
export async function captureCatalogResponse<T>(
  page: Page,
  origin: string,
  path: string,
  action: () => Promise<unknown>,
  query: Readonly<Record<string, string>> = {},
): Promise<T> {
  const expected = new URL(path, origin);
  const browser = await page.context().newCDPSession(page);
  const diagnostic = new Set<string>();
  let matched = 0;
  const note = (request: Request, state: string) => {
    const url = new URL(request.url());
    if (
      url.origin === expected.origin &&
      url.pathname.startsWith("/api/") &&
      diagnostic.size < 24
    )
      diagnostic.add(`${request.method()} ${url.pathname} ${state}`);
  };
  const sent = (request: Request) => note(request, "sent");
  const responded = (reply: Response) =>
    note(reply.request(), String(reply.status()));
  const failed = (request: Request) => note(request, "did not complete");
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const received = new Promise<T>((ok, fail) => {
    resolve = ok;
    reject = fail;
  });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let closing = false;
  const pending = new Set<Promise<void>>();
  page.on("request", sent);
  page.on("response", responded);
  page.on("requestfailed", failed);
  browser.on("Fetch.requestPaused", (event) => {
    if (closing) return; // Detach releases any pauses arriving during teardown.
    const task = (async () => {
      const url = new URL(event.request.url);
      const matches =
        event.request.method === "GET" &&
        url.origin === expected.origin &&
        url.pathname === expected.pathname &&
        url.searchParams.size === Object.keys(query).length &&
        Object.entries(query).every(
          ([key, value]) =>
            url.searchParams.getAll(key).length === 1 &&
            url.searchParams.get(key) === value,
        );
      let value: T | undefined;
      let readError: Error | undefined;
      if (matches) {
        matched++;
        try {
          if (event.responseStatusCode !== 200 || event.responseErrorReason)
            throw new Error(
              `Expected HTTP 200, got ${event.responseStatusCode ?? "no response"}`,
            );
          const read = await browser.send("Fetch.getResponseBody", {
            requestId: event.requestId,
          });
          const bytes = Buffer.from(
            read.body,
            read.base64Encoded ? "base64" : "utf8",
          );
          if (!bytes.length || bytes.length > 1024 * 1024)
            throw new Error("Catalog body outside the evidence size limit");
          value = JSON.parse(
            new TextDecoder("utf-8", { fatal: true }).decode(bytes),
          ) as T;
          if (
            value === null ||
            typeof value !== "object" ||
            Array.isArray(value)
          )
            throw new Error("Expected a catalog response object");
        } catch {
          readError = new Error(
            `Could not observe real catalog JSON at ${expected.pathname} (HTTP ${event.responseStatusCode ?? "unavailable"})`,
          );
        }
      }
      // Omit all overrides. Even malformed and unexpected responses reach the browser.
      await browser.send("Fetch.continueResponse", {
        requestId: event.requestId,
      });
      if (readError) reject(readError);
      else if (value !== undefined) resolve(value);
    })().catch(() => {
      if (!closing)
        reject(new Error("Could not continue the original catalog response"));
    });
    pending.add(task);
    void task.then(() => pending.delete(task));
  });
  try {
    const deadline = new Promise<never>((_, fail) => {
      timer = setTimeout(() => {
        fail(
          new Error(
            `Expected usable actual catalog JSON from ${expected.pathname} within observation deadline; ` +
              `${matched} matching responses. Observed API traffic: ${[...diagnostic].join("; ") || "none"}`,
          ),
        );
      }, 10_000);
    });
    const observed = (async () => {
      await browser.send("Fetch.enable", {
        patterns: [
          { urlPattern: expected.href + "*", requestStage: "Response" },
        ],
      });
      const [value] = await Promise.all([received, action()]);
      return value;
    })();
    return await Promise.race([deadline, observed]);
  } finally {
    if (timer) clearTimeout(timer);
    closing = true;
    page.off("request", sent);
    page.off("response", responded);
    page.off("requestfailed", failed);
    await browser.detach();
    await Promise.allSettled(pending);
  }
}
