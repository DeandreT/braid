const standalone = /^\/(?:chat|notifications|administration|audit|usage|quotas|retention)(?=[/?#]|$)/;
const search = /^\/catalog\/search(?=[?#]|$)/;

/**
 * Keeps an application link in the organization it was created for. The root organization's
 * standalone pages keep their unqualified, root-only routes; every other organization's links
 * name that organization. Catalog resource links already carry their organization.
 */
export function organizationLink(
  path: string,
  organizationId: string | null,
  rootOrganizationId: string | null,
): string {
  if (organizationId === null || organizationId === rootOrganizationId)
    return path;
  if (search.test(path))
    return `/catalog/organizations/${organizationId}/search${path.slice("/catalog/search".length)}`;
  return standalone.test(path)
    ? `/organizations/${organizationId}${path}`
    : path;
}
