/**
 * User guide sections the app links to. Each must exist in docs/user-guide;
 * the .NET UserGuideTests fail when one is missing.
 */
export const helpSectionIds = [
  "administration",
  "reports",
  "comparisons",
  "previews",
  "regression-tests",
  "builds",
  "uploads",
  "creation",
  "chat",
  "cli",
] as const;

export type HelpSectionId = (typeof helpSectionIds)[number];

/** Router commands for the guide contents, or for one linked section. */
export function helpRoute(section?: HelpSectionId): string[] {
  return section === undefined ? ["/help"] : ["/help", section];
}

/** Same format as the server's section IDs. */
export function isHelpSectionId(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length <= 64 &&
    /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(value)
  );
}
