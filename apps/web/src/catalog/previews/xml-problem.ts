/** Closed, numeric-only XML preflight diagnostics. Never keep an HTTP body or parser text. */
export interface XmlPreflightProblem {
  readonly code: "xmlPreflight";
  readonly kind: "invalidInput" | "resourceLimit" | "unsupportedCapability";
  readonly reason: string;
  readonly location: { readonly line: number; readonly column: number } | null;
  readonly statistics: {
    readonly originalBytes: number;
    readonly physicalNodes: number;
    readonly maximumDepth: number;
    readonly maximumAttributes: number;
  } | null;
  readonly observed: string | null;
  readonly limit: string | null;
}

const reasons: Record<string, readonly [XmlPreflightProblem["kind"], string]> =
  {
    invalidUtf8: ["invalidInput", "XML must use strict UTF-8."],
    invalidXml: [
      "invalidInput",
      "XML does not meet the safe syntax policy (including DTD prohibition).",
    ],
    invalidDeclaration: [
      "invalidInput",
      "XML requires an XML 1.0 declaration with UTF-8 or no declared encoding.",
    ],
    documentRequired: [
      "invalidInput",
      "XML requires one document with one root element.",
    ],
    unsupportedName: ["invalidInput", "XML names use unsupported characters."],
    byteLimit: ["resourceLimit", "XML exceeds the original byte limit."],
    depthLimit: ["resourceLimit", "XML exceeds the physical depth limit."],
    nodeLimit: ["resourceLimit", "XML exceeds the physical node limit."],
    attributeLimit: [
      "resourceLimit",
      "XML exceeds the physical attribute limit.",
    ],
    documentCountLimit: [
      "resourceLimit",
      "XML exceeds the document count limit.",
    ],
    invalidMetadata: ["invalidInput", "XML boundary metadata is invalid."],
    metadataTooLarge: [
      "resourceLimit",
      "XML boundary metadata exceeds the encoded budget.",
    ],
    invalidNativeMetadata: [
      "unsupportedCapability",
      "The saved revision does not offer admitted XML boundaries.",
    ],
    nativeByteLimit: [
      "resourceLimit",
      "The saved native artifact exceeds the byte limit.",
    ],
  };

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}
function keys(value: Record<string, unknown>, names: string[]): boolean {
  return (
    Object.keys(value).length === names.length &&
    names.every((name) => Object.hasOwn(value, name))
  );
}
function count(value: unknown): value is number {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2147483647
  );
}
function decimal(value: unknown): value is string | null {
  return (
    value === null ||
    (typeof value === "string" &&
      /^(0|[1-9][0-9]{0,18})$/.test(value) &&
      BigInt(value) <= 9223372036854775807n)
  );
}

export function xmlPreflightProblem(
  value: unknown,
): XmlPreflightProblem | null {
  const body = record(value);
  if (
    !body ||
    !keys(body, [
      "code",
      "kind",
      "reason",
      "location",
      "statistics",
      "observed",
      "limit",
    ]) ||
    body["code"] !== "xmlPreflight" ||
    typeof body["reason"] !== "string" ||
    !Object.hasOwn(reasons, body["reason"]) ||
    reasons[body["reason"]]![0] !== body["kind"] ||
    !decimal(body["observed"]) ||
    !decimal(body["limit"])
  )
    return null;
  const location = record(body["location"]);
  if (
    body["location"] !== null &&
    (!location ||
      !keys(location, ["line", "column"]) ||
      !count(location["line"]) ||
      location["line"] < 1 ||
      !count(location["column"]) ||
      location["column"] < 1)
  )
    return null;
  const stats = record(body["statistics"]);
  const statKeys = [
    "originalBytes",
    "physicalNodes",
    "maximumDepth",
    "maximumAttributes",
  ];
  if (
    body["statistics"] !== null &&
    (!stats ||
      !keys(stats, statKeys) ||
      !statKeys.every((key) => count(stats[key])))
  )
    return null;
  // Copy just the admitted facts, never an attacker-supplied object/prototype.
  return {
    code: "xmlPreflight",
    kind: body["kind"] as XmlPreflightProblem["kind"],
    reason: body["reason"],
    location: location
      ? {
          line: location["line"] as number,
          column: location["column"] as number,
        }
      : null,
    statistics: stats
      ? {
          originalBytes: stats["originalBytes"] as number,
          physicalNodes: stats["physicalNodes"] as number,
          maximumDepth: stats["maximumDepth"] as number,
          maximumAttributes: stats["maximumAttributes"] as number,
        }
      : null,
    observed: body["observed"],
    limit: body["limit"],
  };
}

export function xmlPreflightText(value: XmlPreflightProblem): string {
  const parts = [
    reasons[value.reason]?.[1] ?? "XML preflight refused the input.",
  ];
  if (value.location)
    parts.push(`Line ${value.location.line}, column ${value.location.column}.`);
  if (value.observed !== null) parts.push(`Observed ${value.observed}.`);
  if (value.limit !== null) parts.push(`Limit ${value.limit}.`);
  if (value.statistics)
    parts.push(
      `Bytes ${value.statistics.originalBytes}, nodes ${value.statistics.physicalNodes}, depth ${value.statistics.maximumDepth}, attributes ${value.statistics.maximumAttributes}.`,
    );
  parts.push("No preview run was created.");
  return parts.join(" ");
}
