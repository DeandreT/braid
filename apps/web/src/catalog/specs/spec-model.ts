import type { components } from "../../api/generated";
import { isCatalogId } from "../catalog-types";

// Display contracts only. Upload admission still uses the current server formats.
const formats = [
  "Unsupported",
  "Text",
  "Json",
  "OpenApiJson",
  "Docx",
  "Xlsx",
] as const;
const outcomes = [
  "Complete",
  "Unsupported",
  "Malformed",
  "LimitExceeded",
  "Partial",
] as const;
export type SourceFormat = (typeof formats)[number];
export type ExtractionOutcome = (typeof outcomes)[number];
export type SpecObject = Omit<
  components["schemas"]["SpecObjectV1"],
  "format" | "outcome"
> & {
  format: SourceFormat;
  outcome: ExtractionOutcome | null;
};
export type Inventory = Omit<
  components["schemas"]["SpecInventoryV1"],
  "objects"
> & { objects: SpecObject[] };
export type Upload = components["schemas"]["SpecUploadV1"];
export type Status = components["schemas"]["SpecStatusV1"];
export type Receipt = components["schemas"]["SpecIntakeV1"];
export type ByteLocator = {
  kind: "byte-range";
  pointer: string | null;
  offset: number;
  length: number;
  start: number;
  end: number;
};
const valueKinds = [
  "Blank",
  "Number",
  "Boolean",
  "Error",
  "SharedString",
  "InlineString",
  "String",
  "IsoDate",
] as const;
export type Locator =
  | ByteLocator
  | {
      kind: "docx-paragraph";
      part: string;
      paragraph: number;
      table: number | null;
      row: number | null;
      cell: number | null;
    }
  | {
      kind: "xlsx-cell";
      part: string;
      sheet: string;
      cell: string;
      valueKind: (typeof valueKinds)[number];
      hasCachedValue: boolean;
    }
  | { kind: "pdf-page"; page: number };
export type Chunk = {
  ordinal: number;
  text: string;
  locator: Locator;
  formula: string | null;
};
export type Evidence = { version: 1 | 2; code: string; chunks: Chunk[] };
export const maximumEvidenceBytes = 10 * 1024 * 1024;
const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const positive = (v: unknown): v is number =>
  Number.isSafeInteger(v) && (v as number) > 0;
const nonnegative = (v: unknown): v is number =>
  Number.isSafeInteger(v) && (v as number) >= 0;
const member = <T extends string>(
  values: readonly T[],
  value: unknown,
): value is T => typeof value === "string" && values.includes(value as T);
const coordinate = (v: unknown): v is number => positive(v) && v <= 2147483647;
const textValue = (v: unknown, maximum = maximumEvidenceBytes): v is string =>
  typeof v === "string" &&
  v.length <= maximum &&
  !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(
    v,
  );
function invalid(): never {
  throw new Error("Invalid evidence");
}
function part(value: unknown): value is string {
  return (
    textValue(value, 240) &&
    value.length > 0 &&
    !/[^A-Za-z0-9_\-.[\]/]/.test(value) &&
    value.split("/").every((p) => p !== "" && p !== "." && p !== "..")
  );
}
function cellAddress(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = /^([A-Z]{1,3})([1-9][0-9]{0,6})$/.exec(value);
  if (!match || match[0] !== value) return false;
  const column = [...match[1]!].reduce(
    (n, c) => n * 26 + c.charCodeAt(0) - 64,
    0,
  );
  return column <= 16384 && Number(match[2]) <= 1048576;
}
function byteLocator(
  offset: unknown,
  length: unknown,
  start: unknown,
  end: unknown,
  pointer: unknown,
): ByteLocator {
  if (
    !nonnegative(offset) ||
    !nonnegative(length) ||
    offset + length > maximumEvidenceBytes ||
    !coordinate(start) ||
    !coordinate(end) ||
    end < start ||
    !(pointer === null || textValue(pointer, 2097152))
  )
    invalid();
  return { kind: "byte-range", offset, length, start, end, pointer };
}
function locator(value: unknown): Locator {
  if (!Array.isArray(value)) invalid();
  switch (value[0]) {
    case "byte-range":
      if (value.length !== 6) invalid();
      return byteLocator(value[1], value[2], value[3], value[4], value[5]);
    case "docx-paragraph": {
      if (
        value.length !== 6 ||
        !part(value[1]) ||
        !coordinate(value[2]) ||
        !(
          value.slice(3).every((v) => v === null) ||
          value.slice(3).every(coordinate)
        )
      )
        invalid();
      return {
        kind: "docx-paragraph",
        part: value[1],
        paragraph: value[2],
        table: value[3],
        row: value[4],
        cell: value[5],
      };
    }
    case "xlsx-cell":
      if (
        value.length !== 6 ||
        !part(value[1]) ||
        !textValue(value[2], 31) ||
        value[2].length === 0 ||
        /[\[\]:*?/\\]/.test(value[2]) ||
        !cellAddress(value[3]) ||
        !member(valueKinds, value[4]) ||
        typeof value[5] !== "boolean"
      )
        invalid();
      return {
        kind: "xlsx-cell",
        part: value[1],
        sheet: value[2],
        cell: value[3],
        valueKind: value[4],
        hasCachedValue: value[5],
      };
    case "pdf-page":
      if (value.length !== 2 || !coordinate(value[1]) || value[1] > 256)
        invalid();
      return { kind: "pdf-page", page: value[1] };
    default:
      return invalid();
  }
}
export function inventory(value: unknown, bundleId: string): Inventory {
  if (
    !record(value) ||
    !record(value["bundle"]) ||
    value["bundle"]["bundleId"] !== bundleId ||
    !positive(value["bundle"]["version"]) ||
    typeof value["bundle"]["tombstoned"] !== "boolean" ||
    !Array.isArray(value["objects"]) ||
    value["objects"].length > 128
  )
    throw new Error("Invalid inventory");
  const ids = new Set<string>();
  for (const o of value["objects"]) {
    if (
      !record(o) ||
      !isCatalogId(o["objectId"]) ||
      ids.has(o["objectId"]) ||
      !isCatalogId(o["sourceId"]) ||
      !positive(o["sourceVersion"]) ||
      !nonnegative(o["byteCount"]) ||
      o["byteCount"] > 10485760 ||
      typeof o["digest"] !== "string" ||
      o["digest"].length !== 64 ||
      !/^[a-f0-9]{64}$/.test(o["digest"]) ||
      !member(["Original", "Extraction"], o["kind"]) ||
      !member(formats, o["format"]) ||
      !member(["pending", "published", "retiring", "retired"], o["state"]) ||
      !(o["outcome"] === null || member(outcomes, o["outcome"]))
    )
      throw new Error("Invalid inventory");
    ids.add(o["objectId"]);
  }
  return value as Inventory;
}
export function evidence(value: unknown): Evidence {
  if (
    !Array.isArray(value) ||
    value.length < 2 ||
    value.length > 10002 ||
    !["weft.spec-evidence.v1", "weft.spec-evidence.v2"].includes(value[0]) ||
    !textValue(value[1], 100)
  )
    throw new Error("Invalid evidence");
  const chunks: Chunk[] = [];
  const version = value[0] === "weft.spec-evidence.v1" ? 1 : 2;
  for (const c of value.slice(2)) {
    if (
      !Array.isArray(c) ||
      c.length !== (version === 1 ? 7 : 4) ||
      c[0] !== chunks.length ||
      !textValue(c[1], version === 1 ? 2097152 : maximumEvidenceBytes)
    )
      throw new Error("Invalid evidence");
    const location =
      version === 1 ? byteLocator(c[3], c[4], c[5], c[6], c[2]) : locator(c[2]);
    const formula: unknown = version === 1 ? null : c[3];
    if (
      !(formula === null || (textValue(formula) && formula.length > 0)) ||
      (location.kind !== "xlsx-cell" && formula !== null)
    )
      invalid();
    if (
      location.kind === "xlsx-cell" &&
      ((location.hasCachedValue && formula === null) ||
        (formula !== null && !location.hasCachedValue && c[1] !== "") ||
        (location.valueKind === "Blank" &&
          (c[1] !== "" || location.hasCachedValue)) ||
        (location.valueKind === "InlineString" && formula !== null))
    )
      invalid();
    chunks.push({ ordinal: c[0], text: c[1], locator: location, formula });
  }
  return { version, code: value[1], chunks };
}

// Verify exact received bytes, never a reserialized JSON representation. This is
// an integrity check against the authorized inventory, not an authorization grant.
export async function readEvidence(
  bytes: ArrayBuffer,
  object: SpecObject,
): Promise<Evidence> {
  if (
    bytes.byteLength > maximumEvidenceBytes ||
    bytes.byteLength !== object.byteCount ||
    object.kind !== "Extraction" ||
    object.state !== "published"
  )
    invalid();
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  if (
    Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("") !==
    object.digest
  )
    invalid();
  const result = evidence(
    JSON.parse(
      new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes),
    ),
  );
  const office = object.format === "Docx" || object.format === "Xlsx";
  const expected =
    object.format === "Docx"
      ? "docx-paragraph"
      : object.format === "Xlsx"
        ? "xlsx-cell"
        : "byte-range";
  if (
    result.chunks.some((c) => c.locator.kind !== expected) ||
    (object.outcome !== "Complete" &&
      object.outcome !== "Partial" &&
      result.chunks.length > 0) ||
    (office && object.outcome === "Complete") ||
    (!office && object.outcome === "Partial") ||
    (office &&
      object.outcome === "Partial" &&
      (result.version !== 2 ||
        result.code !==
          (object.format === "Docx"
            ? "office-body-text-only"
            : "office-stored-cells-only")))
  )
    invalid();
  return result;
}
export function recovery(
  upload: Upload,
  current: Inventory,
): "complete" | "retry" | "conflict" {
  const original = current.objects.find(
    (o) => o.objectId === upload.originalObjectId,
  );
  const extracted = current.objects.find(
    (o) => o.objectId === upload.extractionObjectId,
  );
  if (
    (original && original.kind !== "Original") ||
    (extracted && extracted.kind !== "Extraction")
  )
    return "conflict";
  if (
    [original, extracted].some(
      (o) =>
        o &&
        (o.sourceId !== upload.sourceId ||
          o.sourceVersion !== upload.sourceVersion ||
          o.state === "retiring" ||
          o.state === "retired"),
    )
  )
    return "conflict";
  return original?.state === "published" && extracted?.state === "published"
    ? "complete"
    : "retry";
}
export function outcome(value: string | null): string {
  switch (value) {
    case "Complete":
      return "Extraction complete — not a validation or generation result";
    case "Partial":
      return "Partial structural extraction — document content and layout may be missing";
    case "LimitExceeded":
      return "Extraction limit exceeded — no partial evidence; original retained";
    case "Malformed":
      return "Could not parse the source — original retained";
    case "Unsupported":
      return "Extraction unsupported — original retained";
    default:
      return "Extraction not yet acknowledged";
  }
}
