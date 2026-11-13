import { expect, type Page } from "@playwright/test";

/**
 * A browser load or history event is not Angular NavigationEnd. Observe the real
 * router in the development build; never replace a URL or mutate application state.
 */
export async function waitForUnifiedRoute(
  page: Page,
  expected?: string,
): Promise<string> {
  let settled = "";
  await expect
    .poll(async () => {
      const route = await page.evaluate((expected) => {
        const debug = (
          window as unknown as {
            ng?: { getComponent(element: Element): unknown };
          }
        ).ng;
        const header = document.querySelector("weft-header-search");
        const component = (header && debug?.getComponent(header)) as {
          router?: {
            navigated: boolean;
            url: string;
            getCurrentNavigation(): unknown;
            lastSuccessfulNavigation(): {
              finalUrl?: { toString(): string };
            } | null;
          };
        } | null;
        const router = component?.router;
        const browser = location.pathname + location.search + location.hash;
        const last =
          router?.lastSuccessfulNavigation()?.finalUrl?.toString() ?? null;
        return {
          settled:
            !!router?.navigated &&
            router.getCurrentNavigation() === null &&
            router.url === browser &&
            last === browser &&
            (expected === undefined || browser === expected),
          browser,
          router: router?.url ?? null,
          current: !!router?.getCurrentNavigation(),
          last,
        };
      }, expected);
      settled = route.browser;
      return route;
    }, "Wait for the actual Angular router, successful route and browser URL to agree")
    .toMatchObject({ settled: true });
  return settled;
}

/** Preserve the original six-step bound, but settle each real history navigation. */
export async function backToUnifiedChat(page: Page): Promise<void> {
  let route = await waitForUnifiedRoute(page);
  for (let steps = 0; steps < 6 && !route.startsWith("/chat"); steps++) {
    await page.goBack();
    route = await waitForUnifiedRoute(page);
  }
}
