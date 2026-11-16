import { parseCsvDocument } from "../../workbench/csv-document";
import type { components } from "../../api/generated";

export const csvPreviewProfile = "braid.ferrule-csv-preview.v1";
export const csvRowsPolicy = "braid.regression.csv-rows.v1";
export const csvBuildProfile = "braid.ferrule-csharp-csv.v1";
export const csvMaximumRecords = 100_000;
export const csvMaximumColumns = 256;

/** The admitted version of the generated contract; validation stays mandatory. */
export type CsvDocumentDialect =
  components["schemas"]["CsvDocumentDialectV1"] & {
    schema: "braid.csv-dialect.v1";
  };

export function csvMedia(value: unknown): boolean {
  return value === "text/csv" || value === "text/tab-separated-values";
}

/** Response metadata is exact and versioned; source names and bytes never supply a dialect. */
export function csvDialect(value: unknown): value is CsvDocumentDialect {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const keys = [
    "schema",
    "delimiter",
    "quote",
    "quoteDisabled",
    "hasHeaderRow",
    "utf8Bom",
    "preserveEmptyStrings",
  ];
  if (
    Object.keys(value).length !== keys.length ||
    !keys.every((key) => Object.hasOwn(value, key))
  )
    return false;
  return parseCsvDocument(new Uint8Array(), value).kind === "table";
}

export function csvMetadata(media: unknown, dialect: unknown): boolean {
  return csvMedia(media)
    ? csvDialect(dialect) &&
        (dialect.delimiter === "\t") === (media === "text/tab-separated-values")
    : dialect === undefined;
}
