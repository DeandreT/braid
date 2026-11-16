/** Native settings from the versioned artifact metadata, never inferred from a filename or payload. */
export interface CsvDocumentDialect {
  schema: "braid.csv-dialect.v1";
  delimiter: string;
  quote: string | null;
  quoteDisabled: boolean;
  hasHeaderRow: boolean;
  utf8Bom: boolean;
  preserveEmptyStrings: boolean;
}

/** UI limits are intentionally narrower than the native document execution limits. */
export const csvDocumentLimits = Object.freeze({
  maximumBytes: 1024 * 1024,
  maximumCellBytes: 64 * 1024,
  maximumColumns: 256,
  maximumRecords: 10_000,
  maximumNodes: 100_000,
  maximumDisplayedRows: 200,
});

export type CsvDocumentRefusal =
  | "invalidDialect"
  | "byteLimit"
  | "invalidUtf8"
  | "invalidCsv"
  | "cellLimit"
  | "columnLimit"
  | "recordLimit"
  | "nodeLimit";

export interface CsvDocumentTable {
  kind: "table";
  dialect: CsvDocumentDialect;
  byteCount: number;
  /** The first physical record only when the authoritative dialect declares a header. */
  header: readonly string[] | null;
  /** Raw decoded cell values, including empty strings. Ragged records are not padded. */
  rows: readonly (readonly string[])[];
  /** All physical records, including a header and records omitted from the display. */
  recordCount: number;
  /** Data records only, including records omitted from the display. */
  rowCount: number;
  /** The largest record width, not a guessed schema. */
  columnCount: number;
  truncated: boolean;
}

export type CsvDocumentResult =
  CsvDocumentTable | { kind: "unavailable"; reason: CsvDocumentRefusal };

class CsvRefusal extends Error {
  readonly reason: CsvDocumentRefusal;

  constructor(reason: CsvDocumentRefusal) {
    super("CSV display is unavailable.");
    this.reason = reason;
  }
}

function readDialect(value: unknown): CsvDocumentDialect | null {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return null;
  const dialect = value as Record<string, unknown>;
  const {
    schema,
    delimiter,
    quote,
    quoteDisabled,
    hasHeaderRow,
    utf8Bom,
    preserveEmptyStrings,
  } = dialect;
  if (
    schema !== "braid.csv-dialect.v1" ||
    typeof delimiter !== "string" ||
    delimiter.length !== 1 ||
    !/^[\x01-\x09\x0b\x0c\x0e-\x7f]$/.test(delimiter) ||
    typeof quoteDisabled !== "boolean" ||
    typeof hasHeaderRow !== "boolean" ||
    typeof utf8Bom !== "boolean" ||
    typeof preserveEmptyStrings !== "boolean" ||
    (quoteDisabled
      ? quote !== null
      : typeof quote !== "string" ||
        quote.length !== 1 ||
        !/^[\x21-\x7e]$/.test(quote) ||
        quote === delimiter)
  )
    return null;
  return {
    schema,
    delimiter,
    quote: quote as string | null,
    quoteDisabled,
    hasHeaderRow,
    utf8Bom,
    preserveEmptyStrings,
  };
}

/**
 * Display only: never qualifies, normalizes, or rewrites an artifact. The caller
 * must retain the exact bytes for downloads and comparisons. Native empty/null
 * and BOM output settings do not convert displayed cells or require an input BOM.
 * A single optional leading UTF-8 BOM is skipped; embedded BOMs remain cell text.
 */
export function parseCsvDocument(
  bytes: Uint8Array,
  authoritativeCsvDialect: unknown,
): CsvDocumentResult {
  let dialect: CsvDocumentDialect | null;
  try {
    dialect = readDialect(authoritativeCsvDialect);
  } catch {
    return { kind: "unavailable", reason: "invalidDialect" };
  }
  if (!dialect) return { kind: "unavailable", reason: "invalidDialect" };
  if (bytes.length > csvDocumentLimits.maximumBytes)
    return { kind: "unavailable", reason: "byteLimit" };

  // Validate the entire exact view, including records that will not be shown.
  // ignoreBOM keeps a BOM inside a cell from being silently removed on decoding.
  const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
  try {
    decoder.decode(bytes);
  } catch {
    return { kind: "unavailable", reason: "invalidUtf8" };
  }

  try {
    return readTable(bytes, dialect, decoder);
  } catch (error) {
    return {
      kind: "unavailable",
      reason: error instanceof CsvRefusal ? error.reason : "invalidCsv",
    };
  }
}

function readTable(
  bytes: Uint8Array,
  dialect: CsvDocumentDialect,
  decoder: TextDecoder,
): CsvDocumentTable {
  const delimiter = dialect.delimiter.charCodeAt(0);
  const quote = dialect.quoteDisabled ? -1 : dialect.quote!.charCodeAt(0);
  let offset =
    bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf ? 3 : 0;
  let recordCount = 0;
  let columnCount = 0;
  let nodes = 0;
  let header: string[] | null = null;
  const rows: string[][] = [];

  const countNode = () => {
    if (++nodes > csvDocumentLimits.maximumNodes)
      throw new CsvRefusal("nodeLimit");
  };
  const checkCell = (start: number, end: number) => {
    // Encoded field content, including doubled quotes but not surrounding quotes.
    if (end - start > csvDocumentLimits.maximumCellBytes)
      throw new CsvRefusal("cellLimit");
  };

  while (offset < bytes.length) {
    if (++recordCount > csvDocumentLimits.maximumRecords)
      throw new CsvRefusal("recordLimit");
    countNode(); // One node per record and per cell, including undisplayed records.
    const isHeader = dialect.hasHeaderRow && recordCount === 1;
    const cells: string[] | null =
      isHeader || rows.length < csvDocumentLimits.maximumDisplayedRows
        ? []
        : null;
    let columns = 0;

    while (true) {
      if (++columns > csvDocumentLimits.maximumColumns)
        throw new CsvRefusal("columnLimit");
      countNode();
      const quoted = bytes[offset] === quote;
      if (quoted) offset++;
      const start = offset;
      let end: number;

      if (quoted) {
        while (true) {
          checkCell(start, offset);
          if (offset === bytes.length) throw new CsvRefusal("invalidCsv");
          if (bytes[offset] === quote) {
            if (bytes[offset + 1] !== quote) break;
            offset += 2;
          } else offset++;
        }
        end = offset++;
      } else {
        while (
          offset < bytes.length &&
          bytes[offset] !== delimiter &&
          bytes[offset] !== 0x0a &&
          bytes[offset] !== 0x0d
        ) {
          if (bytes[offset] === quote) throw new CsvRefusal("invalidCsv");
          offset++;
        }
        end = offset;
        checkCell(start, end);
      }

      const separator = bytes[offset];
      if (
        separator !== undefined &&
        separator !== delimiter &&
        separator !== 0x0a &&
        separator !== 0x0d
      )
        throw new CsvRefusal("invalidCsv");
      if (cells) {
        const text = decoder.decode(bytes.subarray(start, end));
        cells.push(
          quoted
            ? text.replaceAll(dialect.quote! + dialect.quote!, dialect.quote!)
            : text,
        );
      }
      if (separator === delimiter) {
        offset++;
        continue; // A final delimiter still introduces an empty final cell.
      }
      if (separator === 0x0d) {
        if (bytes[offset + 1] !== 0x0a) throw new CsvRefusal("invalidCsv");
        offset += 2;
      } else if (separator === 0x0a) offset++;
      break;
    }
    columnCount = Math.max(columnCount, columns);
    if (isHeader) header = cells;
    else if (cells) rows.push(cells);
  }

  const rowCount = recordCount - (header === null ? 0 : 1);
  return {
    kind: "table",
    dialect,
    byteCount: bytes.length,
    header,
    rows,
    recordCount,
    rowCount,
    columnCount,
    truncated: rowCount > rows.length,
  };
}
