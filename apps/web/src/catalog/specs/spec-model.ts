import type { components } from "../../api/generated";
import { isCatalogId } from "../catalog-types";

export type Inventory = components["schemas"]["SpecInventoryV1"];
export type SpecObject = components["schemas"]["SpecObjectV1"];
export type Upload = components["schemas"]["SpecUploadV1"];
export type Status = components["schemas"]["SpecStatusV1"];
export type Receipt = components["schemas"]["SpecIntakeV1"];
export type Chunk = {
  ordinal: number;
  text: string;
  pointer: string | null;
  offset: number;
  length: number;
  start: number;
  end: number;
};
export type Evidence = { code: string; chunks: Chunk[] };
const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const positive = (v: unknown): v is number =>
  Number.isSafeInteger(v) && (v as number) > 0;
const nonnegative = (v: unknown): v is number =>
  Number.isSafeInteger(v) && (v as number) >= 0;
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
      !/^[a-f0-9]{64}$/.test(o["digest"]) ||
      !["Original", "Extraction"].includes(String(o["kind"])) ||
      !["Text", "Json", "OpenApiJson", "Unsupported"].includes(
        String(o["format"]),
      ) ||
      !["pending", "published", "retiring", "retired"].includes(
        String(o["state"]),
      ) ||
      !(
        o["outcome"] === null ||
        ["Complete", "Malformed", "Unsupported", "LimitExceeded"].includes(
          String(o["outcome"]),
        )
      )
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
    value[0] !== "weft.spec-evidence.v1" ||
    typeof value[1] !== "string" ||
    value[1].length > 100
  )
    throw new Error("Invalid evidence");
  const chunks: Chunk[] = [];
  for (const c of value.slice(2)) {
    if (
      !Array.isArray(c) ||
      c.length !== 7 ||
      c[0] !== chunks.length ||
      typeof c[1] !== "string" ||
      c[1].length > 2097152 ||
      !(c[2] === null || typeof c[2] === "string") ||
      !nonnegative(c[3]) ||
      !nonnegative(c[4]) ||
      !positive(c[5]) ||
      !positive(c[6]) ||
      c[6] < c[5]
    )
      throw new Error("Invalid evidence");
    chunks.push({
      ordinal: c[0],
      text: c[1],
      pointer: c[2],
      offset: c[3],
      length: c[4],
      start: c[5],
      end: c[6],
    });
  }
  return { code: value[1], chunks };
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
