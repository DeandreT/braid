import type { Route } from "@playwright/test";
import { expect, type ControlledSession } from "./fixture";

/** Two public reads only; never broadens the protected fixture's admission. */
export async function withExampleReads(
  app: ControlledSession,
  work: (reads: { health: number; list: number }) => Promise<void>,
): Promise<void> {
  const reads = { health: 0, list: 0 };
  const originals: Promise<void>[] = [];
  const observed: Promise<PromiseSettledResult<void>[]>[] = [];
  let accepting = true;
  let failures = 0;
  const matches = (url: URL) =>
    url.origin === app.origin &&
    ["/api/v1/health", "/api/v1/examples/mappings"].includes(url.pathname);
  const serve = async (route: Route): Promise<void> => {
    const request = route.request();
    const url = new URL(request.url());
    const headers = request.headers();
    if (
      !accepting ||
      !matches(url) ||
      request.url() !== app.origin + url.pathname ||
      request.method() !== "GET" ||
      url.search !== "" ||
      (request.postDataBuffer()?.length ?? 0) !== 0 ||
      Object.keys(headers).some(
        (name) =>
          name === "authorization" ||
          name === "x-xsrf-token" ||
          name === "idempotency-key" ||
          name === "identitycontext" ||
          name === "identity-context" ||
          name.startsWith("x-weft-"),
      )
    ) {
      ++failures;
      await route.abort("blockedbyclient");
      return;
    }
    const health = url.pathname === "/api/v1/health";
    await route.fulfill({
      status: 200,
      json: health
        ? {
            status: "ok",
            service: "weft-api",
            version: "0.1.0",
            apiVersion: "v1",
          }
        : [],
    });
    if (health) ++reads.health;
    else ++reads.list;
  };
  const handler = (route: Route): Promise<void> => {
    const original = serve(route);
    originals.push(original);
    observed.push(Promise.allSettled([original]));
    return original;
  };
  try {
    await app.page.route(matches, handler);
    await work(reads);
  } finally {
    accepting = false;
    let removed = false;
    try {
      await app.page.unroute(matches, handler);
      removed = true;
    } finally {
      const results = await Promise.allSettled(originals);
      await Promise.all(observed);
      expect(
        removed &&
          failures === 0 &&
          results.length === originals.length &&
          results.every((result) => result.status === "fulfilled"),
      ).toBe(true);
    }
  }
}
