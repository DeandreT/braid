import type { CaseDefinition } from "./regression-api";

function stable(value: unknown): string {
  if (Array.isArray(value)) return "[" + value.map(stable).join(",") + "]";
  if (value && typeof value === "object")
    return (
      "{" +
      Object.entries(value)
        .sort(([a], [b]) => (a === b ? 0 : a < b ? -1 : 1))
        .map(([key, item]) => JSON.stringify(key) + ":" + stable(item))
        .join(",") +
      "}"
    );
  return JSON.stringify(value);
}

/** Compare the server's canonical ordering without modifying the retained request. */
export function canonicalDefinition(value: CaseDefinition): string {
  const order = <T extends { name: string | null }>(items: T[]) =>
    [...items].sort((a, b) => {
      if (a.name === b.name) return 0;
      if (a.name === null) return -1;
      if (b.name === null) return 1;
      return a.name < b.name ? -1 : 1;
    });
  return stable({
    ...value,
    namedInputs: order(value.namedInputs),
    expected: order(value.expected),
    parameters: order(value.parameters),
  });
}
