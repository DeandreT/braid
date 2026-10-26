import type { components } from "../../api/generated";
import { isCatalogId } from "../catalog-types";

export type CatalogSearchPage = components["schemas"]["CatalogSearchPageV1"];
export type CatalogSearchHit = components["schemas"]["CatalogSearchHitV1"];
export type CatalogSearchText = components["schemas"]["CatalogSearchTextV1"];

export function isSearchUnicode(value: string): boolean {
  for (let offset = 0; offset < value.length; offset++) {
    const code = value.charCodeAt(offset);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++offset);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function isSearchCursor(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= 4096 &&
    /^[A-Za-z0-9_-]+$/.test(value)
  );
}

function text(value: unknown): value is CatalogSearchText {
  if (
    !record(value) ||
    typeof value["text"] !== "string" ||
    !isSearchUnicode(value["text"]) ||
    new TextEncoder().encode(value["text"]).length > 4096 ||
    !Array.isArray(value["highlights"]) ||
    value["highlights"].length > 64
  )
    return false;
  const content = value["text"];
  let end = 0;
  const splitsScalar = (offset: number) =>
    offset > 0 &&
    offset < content.length &&
    /[\ud800-\udbff]/.test(content[offset - 1]!) &&
    /[\udc00-\udfff]/.test(content[offset]!);
  for (const span of value["highlights"]) {
    if (
      !record(span) ||
      !Number.isSafeInteger(span["start"]) ||
      !Number.isSafeInteger(span["length"])
    )
      return false;
    const start = span["start"] as number,
      length = span["length"] as number;
    if (
      start < end ||
      length < 1 ||
      start + length > content.length ||
      splitsScalar(start) ||
      splitsScalar(start + length)
    )
      return false;
    end = start + length;
  }
  return true;
}

export function isSearchPage(
  value: unknown,
  organizationId: string,
  query: string,
): value is CatalogSearchPage {
  if (
    !record(value) ||
    value["query"] !== query.trim() ||
    !Array.isArray(value["hits"]) ||
    value["hits"].length > 100 ||
    !(value["nextCursor"] === null || isSearchCursor(value["nextCursor"]))
  )
    return false;
  const identities = new Set<string>();
  return value["hits"].every((hit: unknown) => {
    if (
      !record(hit) ||
      hit["organizationId"] !== organizationId ||
      !isCatalogId(hit["projectId"]) ||
      !text(hit["title"]) ||
      !text(hit["description"])
    )
      return false;
    if (hit["title"].text.length === 0) return false;
    const kind = hit["kind"];
    if (
      !["project", "mapping", "revision", "field", "rule"].includes(
        String(kind),
      )
    )
      return false;
    if (
      kind === "project"
        ? hit["mappingId"] !== null || hit["revisionId"] !== null
        : !isCatalogId(hit["mappingId"]) || !isCatalogId(hit["revisionId"])
    )
      return false;
    const resource = kind === "field" || kind === "rule";
    if (
      resource
        ? !isCatalogId(hit["projectionDerivationId"]) ||
          typeof hit["resourceId"] !== "string" ||
          !isSearchUnicode(hit["resourceId"]) ||
          new TextEncoder().encode(hit["resourceId"]).length < 1 ||
          new TextEncoder().encode(hit["resourceId"]).length > 2048
        : hit["projectionDerivationId"] !== null || hit["resourceId"] !== null
    )
      return false;
    const key = JSON.stringify([
      kind,
      hit["projectId"],
      hit["mappingId"],
      kind === "mapping" ? null : hit["revisionId"],
      hit["projectionDerivationId"],
      hit["resourceId"],
    ]);
    if (identities.has(key)) return false;
    identities.add(key);
    return true;
  });
}

export function searchResultLink(hit: CatalogSearchHit): string {
  const project = `/catalog/organizations/${hit.organizationId}/projects/${hit.projectId}`;
  return hit.kind === "project"
    ? project
    : `${project}/mappings/${hit.mappingId}/revisions/${hit.revisionId}`;
}

export function searchTextParts(
  value: CatalogSearchText,
): { text: string; highlight: boolean }[] {
  const parts = [];
  let end = 0;
  for (const span of value.highlights) {
    parts.push({ text: value.text.slice(end, span.start), highlight: false });
    end = span.start + span.length;
    parts.push({ text: value.text.slice(span.start, end), highlight: true });
  }
  parts.push({ text: value.text.slice(end), highlight: false });
  return parts;
}
