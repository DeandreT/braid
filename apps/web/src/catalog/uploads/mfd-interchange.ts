import type { components } from "../../api/generated";

export type MfdReview = components["schemas"]["CatalogInterchangeReviewV1"];
const encoder = new TextEncoder();
export const mfdLimits = {
  files: 128,
  file: 4 * 1024 * 1024,
  total: 16 * 1024 * 1024,
  path: 256,
} as const;

export function mfdPath(path: string): string {
  if (
    !path ||
    encoder.encode(path).length > mfdLimits.path ||
    /[\\:\u0000-\u001f\u007f-\u009f]/u.test(path) ||
    path.split("/").some((part) => !part || part === "." || part === "..") ||
    /[\ud800-\udfff]/u.test(path)
  )
    throw new Error("Use safe relative file paths, at most 256 UTF-8 bytes.");
  return path;
}

/** Same UTF-8 byte ordering and framing as InterchangePackage; never normalizes uploaded bytes. */
export async function mfdPackage(
  files: readonly File[],
  designPath: string,
): Promise<Blob> {
  if (!files.length || files.length > mfdLimits.files)
    throw new Error("Choose between 1 and 128 files.");
  const entries = files.map((file) => ({
    file,
    path: mfdPath(file.webkitRelativePath || file.name),
  }));
  if (new Set(entries.map((entry) => entry.path)).size !== entries.length)
    throw new Error("Duplicate package paths are not allowed.");
  if (
    !entries.some((entry) => entry.path === designPath) ||
    !designPath.toLowerCase().endsWith(".mfd")
  )
    throw new Error("Choose the .mfd design entry.");
  if (
    entries.some(({ file }) => file.size > mfdLimits.file) ||
    entries.reduce((total, { file }) => total + file.size, 0) > mfdLimits.total
  )
    throw new Error(
      "Each file is limited to 4 MiB; the complete package is limited to 16 MiB.",
    );
  entries.sort((left, right) => {
    const a = encoder.encode(left.path),
      b = encoder.encode(right.path);
    for (let i = 0; i < Math.min(a.length, b.length); i++)
      if (a[i] !== b[i]) return a[i]! - b[i]!;
    return a.length - b.length;
  });
  const parts: BlobPart[] = [];
  const number = (value: number) => {
    const buffer = new ArrayBuffer(4);
    new DataView(buffer).setUint32(0, value, false);
    parts.push(buffer);
  };
  const text = (value: string) => {
    const bytes = encoder.encode(value);
    number(bytes.length);
    parts.push(bytes);
  };
  text("weft.interchange-package.v1");
  text(designPath);
  number(entries.length);
  for (const entry of entries) {
    text(entry.path);
    number(entry.file.size);
    parts.push(await entry.file.arrayBuffer());
  }
  return new Blob(parts, { type: "application/vnd.weft.mfd-package.v1" });
}

const id = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const digest = /^[0-9a-f]{64}$/u;
const record = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown, bytes: number): value is string =>
  typeof value === "string" &&
  value.length > 0 &&
  !value.includes("\0") &&
  encoder.encode(value).length <= bytes;

/** Non-success HTTP bodies can describe a known ingestion result, never a saved revision. */
export function mfdFailureOutcome(
  status: number,
  value: unknown,
): Record<string, unknown> | null {
  if (!record(value) || typeof value["outcome"] !== "string") return null;
  const outcomes: Readonly<Record<number, readonly string[]>> = {
    400: ["rejected"],
    413: ["rejected"],
    422: ["rejected"],
    404: ["notAuthorizedOrNotFound"],
    409: [
      "needsSource",
      "idempotencyConflict",
      "headConflict",
      "attemptExhausted",
      "claimLost",
    ],
    503: ["retryableFailure"],
  };
  return outcomes[status]?.includes(value["outcome"]) ? value : null;
}

export function readMfdReview(value: unknown): MfdReview {
  if (
    !record(value) ||
    !text(value["reviewId"], 36) ||
    !id.test(value["reviewId"]) ||
    value["reviewId"] === "00000000-0000-0000-0000-000000000000" ||
    !["reviewDigest", "packageDigest", "nativeDigest"].every(
      (key) =>
        typeof value[key] === "string" && digest.test(value[key] as string),
    ) ||
    !text(value["summary"], 8192) ||
    !Array.isArray(value["warnings"]) ||
    value["warnings"].length > 256 ||
    !value["warnings"].every(
      (warning) =>
        record(warning) &&
        text(warning["code"], 128) &&
        text(warning["message"], 2048),
    ) ||
    !record(value["engine"]) ||
    !text(value["engine"]["backendId"], 128) ||
    !text(value["engine"]["engineRevision"], 256) ||
    !text(value["engine"]["backendBuild"], 2048) ||
    !text(value["engine"]["adapterBuild"], 256) ||
    !(
      value["targetMappingId"] === null ||
      (typeof value["targetMappingId"] === "string" &&
        id.test(value["targetMappingId"]))
    ) ||
    !(
      value["expectedHeadRevisionId"] === null ||
      (typeof value["expectedHeadRevisionId"] === "string" &&
        id.test(value["expectedHeadRevisionId"]))
    ) ||
    (value["targetMappingId"] === null) !==
      (value["expectedHeadRevisionId"] === null) ||
    !(value["mappingName"] === null || text(value["mappingName"], 800)) ||
    typeof value["expiresAt"] !== "string" ||
    !Number.isFinite(Date.parse(value["expiresAt"]))
  )
    throw new Error("The server returned an incompatible interchange review.");
  return structuredClone(value) as MfdReview;
}
