export const xmlPreviewProfile = "braid.ferrule-xml-preview.v1";
export interface XmlMetadata {
  schema: string;
  safetyProfile: string;
  schemaHints: null | {
    noNamespaceLocation: string | null;
    locations: { namespace: string; location: string }[];
  };
}
const encoder = new TextEncoder();
function shape(
  value: unknown,
  keys: string[],
): value is Record<string, unknown> {
  return (
    !!value &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    Object.keys(value).length === keys.length &&
    keys.every((key) => Object.hasOwn(value, key))
  );
}
function token(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    encoder.encode(value).length <= 4096 &&
    !/[\u0000-\u0020\ufffe\uffff]/u.test(value) &&
    [...value].every((character) => {
      const point = character.codePointAt(0)!;
      return point < 0xd800 || point > 0xdfff;
    })
  );
}
/** Metadata describes saved hints only. No resource, schema or document is opened here. */
export function xmlMetadata(value: unknown): value is XmlMetadata {
  if (
    !shape(value, ["schema", "safetyProfile", "schemaHints"]) ||
    value.schema !== "braid.xml-boundary.v1" ||
    value.safetyProfile !== "braid.xml-safe.v1"
  )
    return false;
  const hints = value.schemaHints;
  if (hints !== null) {
    if (
      !shape(hints, ["noNamespaceLocation", "locations"]) ||
      (hints.noNamespaceLocation !== null &&
        !token(hints.noNamespaceLocation)) ||
      !Array.isArray(hints.locations) ||
      hints.locations.length > 32 ||
      (hints.noNamespaceLocation === null && hints.locations.length === 0)
    )
      return false;
    const names = new Set<string>();
    for (const pair of hints.locations) {
      if (
        !shape(pair, ["namespace", "location"]) ||
        !token(pair.namespace) ||
        !token(pair.location) ||
        names.has(pair.namespace)
      )
        return false;
      names.add(pair.namespace);
    }
  }
  return xmlMetadataBytes(value as unknown as XmlMetadata) <= 32768;
}
/** Display-side ledger for the closed metadata shape; execution admission remains server-owned. */
export function xmlMetadataBytes(value: XmlMetadata): number {
  const textBytes = (text: string): number => {
    let bytes = 2;
    for (let index = 0; index < text.length; index++) {
      const code = text.charCodeAt(index),
        character = text[index]!;
      bytes +=
        code < 32 || code >= 127 || "\"<>&'`+".includes(character)
          ? 6
          : character === "\\"
            ? 2
            : 1;
    }
    return bytes;
  };
  const hints = value.schemaHints;
  const hintBytes =
    hints === null
      ? 4
      : '{"noNamespaceLocation":,"locations":[]}'.length +
        (hints.noNamespaceLocation === null
          ? 4
          : textBytes(hints.noNamespaceLocation)) +
        Math.max(0, hints.locations.length - 1) +
        hints.locations.reduce(
          (total, pair) =>
            total +
            '{"namespace":,"location":}'.length +
            textBytes(pair.namespace) +
            textBytes(pair.location),
          0,
        );
  return (
    15 +
    '{"schema":,"safetyProfile":,"schemaHints":}'.length +
    textBytes(value.schema) +
    textBytes(value.safetyProfile) +
    hintBytes
  );
}
