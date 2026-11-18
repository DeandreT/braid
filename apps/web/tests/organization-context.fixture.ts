import type { Page, Route } from "@playwright/test";

export const organizationContextPath = (organizationId: string): string =>
  `/api/v1/organizations/${organizationId}/context`;

/** A stable synthetic incarnation; tests of re-admission supply a new version. */
export const organizationContext = (
  catalog: unknown,
  admissionVersion = "1",
) => ({
  schemaVersion: "braid.organization-context.v1",
  admissionVersion,
  catalog,
});

export function organizationContextReply<
  T extends { json?: unknown; status?: number },
>(reply: T): T {
  return reply.json !== undefined && (reply.status ?? 200) === 200
    ? { ...reply, json: organizationContext(reply.json) }
    : reply;
}

/** Override authoritative admission without consuming the legacy discovery response. */
export async function routeOrganizationContext(
  page: Page,
  handler: (route: Route) => unknown | Promise<unknown>,
  options?: { times?: number; discovery?: boolean },
): Promise<void> {
  if (options?.discovery)
    await page.route(
      "**/api/v1/catalog/context",
      async (route) => {
        await handler(route);
      },
      options,
    );
  await page.route(
    /\/api\/v1\/organizations\/[0-9a-f-]{36}\/context$/,
    async (route) => {
      await handler(
        new Proxy(route, {
          get(target, key) {
            if (key === "fulfill")
              return (reply: Parameters<Route["fulfill"]>[0] = {}) =>
                target.fulfill(organizationContextReply(reply));
            const value = Reflect.get(target, key);
            return typeof value === "function" ? value.bind(target) : value;
          },
        }),
      );
    },
    options,
  );
}
