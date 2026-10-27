import {
  decodeBytes,
  type RegressionDocument,
} from "../catalog/regression/regression-api";
import type { ProjectionView } from "./projection-view";
import { ObservedJson } from "./observed-json";
import { ediSegments } from "../catalog/previews/edi-segments";

export interface DocumentRepresentation {
  name: string | null;
  byteCount: number;
  text: string;
  truncated: boolean;
  format: "utf8" | "hex";
  mediaType: string | null;
  segments: readonly string[] | null;
}
export interface DocumentDisplay extends DocumentRepresentation {
  document: RegressionDocument;
}
export interface DocumentDifference {
  key: string;
  name: string;
  expected: DocumentDisplay | null;
  actual: DocumentDisplay | null;
  firstByte: number | null;
  equal: boolean;
}

export function documentKey(name: string | null): string {
  return name === null ? "primary" : `named:${name}`;
}

/** Only display representation is bounded. Download continues to use the original exact bytes. */
export function displayDocument(document: RegressionDocument): DocumentDisplay {
  return {
    ...displayBytes(
      decodeBytes(document.bytesBase64Url),
      document.name,
      document.mediaType,
    ),
    document,
  };
}

/** A display-only bounded representation; it never becomes download or comparison input. */
export function displayBytes(
  bytes: Uint8Array,
  name: string | null,
  mediaType?: string | null,
): DocumentRepresentation {
  const bounded = bytes.subarray(0, 65536);
  let text: string,
    format: "utf8" | "hex" = "utf8";
  try {
    const decoder = new TextDecoder("utf-8", { fatal: true });
    text = decoder.decode(bounded, { stream: bounded.length < bytes.length });
  } catch {
    format = "hex";
    text = [...bounded.subarray(0, 1024)]
      .map((value) => value.toString(16).padStart(2, "0"))
      .join(" ");
  }
  return {
    name,
    byteCount: bytes.length,
    text,
    truncated: bytes.length > (format === "hex" ? 1024 : 65536),
    format,
    mediaType: mediaType ?? null,
    segments:
      format === "utf8" && mediaType ? ediSegments(text, mediaType) : null,
  };
}

export function compareDocuments(
  expected: readonly RegressionDocument[],
  actual: readonly RegressionDocument[],
): readonly DocumentDifference[] {
  const before = new Map(expected.map((value) => [value.name, value]));
  const after = new Map(actual.map((value) => [value.name, value]));
  return [...new Set([...before.keys(), ...after.keys()])].map((name) => {
    const left = before.get(name),
      right = after.get(name);
    let firstByte: number | null = null;
    if (left && right) {
      const a = decodeBytes(left.bytesBase64Url),
        b = decodeBytes(right.bytesBase64Url);
      let offset = 0;
      while (offset < Math.min(a.length, b.length) && a[offset] === b[offset])
        offset++;
      if (offset !== a.length || offset !== b.length) firstByte = offset;
    }
    return {
      key: documentKey(name),
      name: name ?? "Primary output",
      expected: left ? displayDocument(left) : null,
      actual: right ? displayDocument(right) : null,
      firstByte,
      equal: !!left && !!right && firstByte === null,
    };
  });
}

export function affectedFields(
  difference: DocumentDifference | null,
  view: ProjectionView | null,
): readonly { fieldId: string; label: string }[] {
  if (!difference?.expected || !difference.actual || !view) return [];
  const name = difference.expected.name;
  const boundaries = view.outputs.filter(
    (value) =>
      value.format === "json" &&
      (name === null
        ? value.role === "primaryOutput"
        : value.role === "namedOutput" && value.name === name),
  );
  if (boundaries.length !== 1) return [];
  const paths = ObservedJson.changedPaths(
    decodeBytes(difference.expected.document.bytesBase64Url),
    decodeBytes(difference.actual.document.bytesBase64Url),
  );
  if (!paths) return [];
  const fields = [...view.fields.values()].filter(
    (field) => field.boundaryId === boundaries[0]!.id,
  );
  const selected = new Map<string, { fieldId: string; label: string }>();
  for (const path of paths) {
    const matches = fields.filter(
      (field) =>
        field.path.length === path.length &&
        field.path.every((segment, index) => {
          const value = path[index]!;
          if (segment.kind === "anyItem") return value.kind === "index";
          if (segment.kind === "index")
            return value.kind === "index" && segment.index === value.index;
          return (
            segment.kind === "field" &&
            value.kind === "field" &&
            !segment.namespaceUri &&
            segment.name === value.name
          );
        }),
    );
    if (matches.length !== 1) continue;
    const field = matches[0]!;
    selected.set(field.id, {
      fieldId: field.id,
      label: view.label({ kind: "field", fieldId: field.id }),
    });
  }
  return [...selected.values()];
}
