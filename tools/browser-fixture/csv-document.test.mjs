import assert from "node:assert/strict";
import test from "node:test";
import {
  csvDocumentLimits,
  parseCsvDocument,
} from "../../apps/web/src/workbench/csv-document.ts";

const encoder = new TextEncoder();
const dialect = (overrides = {}) => ({
  schema: "braid.csv-dialect.v1",
  delimiter: ",",
  quote: '"',
  quoteDisabled: false,
  hasHeaderRow: false,
  utf8Bom: false,
  preserveEmptyStrings: true,
  ...overrides,
});
const parse = (text, overrides) =>
  parseCsvDocument(encoder.encode(text), dialect(overrides));
const table = (text, overrides) => {
  const result = parse(text, overrides);
  assert.equal(result.kind, "table");
  return result;
};
const refused = (result, reason) =>
  assert.deepEqual(result, { kind: "unavailable", reason });

test("zero bytes, an empty record and quoted empty strings are distinct", () => {
  const empty = table("");
  assert.deepEqual(empty.rows, []);
  assert.equal(empty.recordCount, 0);
  assert.equal(empty.columnCount, 0);
  assert.equal(empty.byteCount, 0);
  for (const text of ['""', "\n", "\r\n"]) {
    const single = table(text);
    assert.deepEqual(single.rows, [[""]]);
    assert.equal(single.recordCount, 1);
    assert.equal(single.columnCount, 1);
    assert.equal(single.truncated, false);
  }
  assert.deepEqual(table(",\n\n").rows, [["", ""], [""]]);
  assert.deepEqual(table("a,,").rows, [["a", "", ""]]);
  assert.deepEqual(table("a,,\r\n").rows, [["a", "", ""]]);
});

test("authoritative headers and ragged widths are preserved without padding", () => {
  const result = table("a,b\n1\n2,3,4\n", { hasHeaderRow: true });
  assert.deepEqual(result.header, ["a", "b"]);
  assert.deepEqual(result.rows, [["1"], ["2", "3", "4"]]);
  assert.equal(result.recordCount, 3);
  assert.equal(result.rowCount, 2);
  assert.equal(result.columnCount, 3);
  assert.deepEqual(table("\n", { hasHeaderRow: true }).header, [""]);
  const empty = table("", { hasHeaderRow: true });
  assert.equal(empty.header, null);
  assert.equal(empty.rowCount, 0);
});

test("quoted delimiters, doubled quotes and multiline cell text remain exact", () => {
  const result = table('"a,b","say ""hi""","line1\r\nline2\nline3"\r\nx,y,z\n');
  assert.deepEqual(result.rows, [
    ["a,b", 'say "hi"', "line1\r\nline2\nline3"],
    ["x", "y", "z"],
  ]);
  assert.deepEqual(table('""""').rows, [['"']]);
  assert.deepEqual(
    table("'a;b';'it''s'", { delimiter: ";", quote: "'" }).rows,
    [["a;b", "it's"]],
  );
});

test("tab and other ASCII delimiters are explicit, never sniffed", () => {
  assert.deepEqual(table("a\tb\nc\td", { delimiter: "\t" }).rows, [
    ["a", "b"],
    ["c", "d"],
  ]);
  assert.deepEqual(table("a;b\tc,d", { delimiter: ";" }).rows, [
    ["a", "b\tc,d"],
  ]);
  assert.deepEqual(table("a;b\tc,d").rows, [["a;b\tc", "d"]]);
  assert.deepEqual(table("a|b|", { delimiter: "|" }).rows, [["a", "b", ""]]);
  assert.deepEqual(table("a\x01b", { delimiter: "\x01" }).rows, [["a", "b"]]);
});

test("disabled quotes are literal text, not multiline or delimiter escapes", () => {
  assert.deepEqual(
    table('"a,b"\n"c""d",', {
      quoteDisabled: true,
      quote: null,
    }).rows,
    [
      ['"a', 'b"'],
      ['"c""d"', ""],
    ],
  );
  assert.deepEqual(table('"a\nb"', { quoteDisabled: true, quote: null }).rows, [
    ['"a'],
    ['b"'],
  ]);
});

test("UTF-8 is strict and a single optional leading BOM is encoding syntax", () => {
  for (const utf8Bom of [false, true]) {
    assert.deepEqual(table("é,😀,東京", { utf8Bom }).rows, [
      ["é", "😀", "東京"],
    ]);
    assert.deepEqual(table("\ufeffé,😀", { utf8Bom }).rows, [["é", "😀"]]);
  }
  assert.deepEqual(table("\ufeff").rows, []);
  assert.deepEqual(table("\ufeff\ufeffa,\ufeffb").rows, [
    ["\ufeffa", "\ufeffb"],
  ]);
  assert.deepEqual(table('"\ufeffa",b').rows, [["\ufeffa", "b"]]);
  for (const bytes of [
    [0xc0, 0xaf],
    [0xc3],
    [0xff],
    [0xed, 0xa0, 0x80],
    [0xf4, 0x90, 0x80, 0x80],
    [0xff, 0xfe, 0x61, 0x00],
  ])
    refused(parseCsvDocument(Uint8Array.from(bytes), dialect()), "invalidUtf8");
});

test("only the exact byte view is read and payload/metadata are not modified", () => {
  const backing = Uint8Array.from([0xff, ...encoder.encode("a,b"), 0xff]);
  const before = backing.slice();
  const metadata = Object.freeze(dialect());
  const result = parseCsvDocument(
    backing.subarray(1, backing.length - 1),
    metadata,
  );
  assert.equal(result.kind, "table");
  assert.deepEqual(result.rows, [["a", "b"]]);
  assert.equal(result.byteCount, 3);
  assert.deepEqual(backing, before);
  assert.deepEqual(result.dialect, metadata);
  assert.notEqual(result.dialect, metadata);
});

test("raw empty strings, whitespace, formulas and markup are plain display data", () => {
  for (const preserveEmptyStrings of [false, true]) {
    const result = table(',"",  ,NULL,=1+1,<img src=x onerror=alert(1)>', {
      preserveEmptyStrings,
    });
    assert.deepEqual(result.rows, [
      ["", "", "  ", "NULL", "=1+1", "<img src=x onerror=alert(1)>"],
    ]);
  }
});

test("malformed quotes and unsupported bare record CR refuse the whole table", () => {
  for (const text of [
    '"unterminated',
    'a"b',
    '"a"suffix',
    '"a" ',
    '"a""',
    "a\rb",
    "a\r",
  ]) {
    refused(parse(text), "invalidCsv");
  }
  assert.deepEqual(table('"a\rb"').rows, [["a\rb"]]);
});

test("metadata must be complete, versioned and match the native dialect contract", () => {
  for (const value of [
    undefined,
    null,
    {},
    [],
    "text/csv",
    dialect({ schema: "future" }),
  ])
    refused(parseCsvDocument(encoder.encode("a,b"), value), "invalidDialect");
  for (const delimiter of ["", ",,", "é", "\0", "\r", "\n", ",\n", "😀", 44])
    refused(parse("a,b", { delimiter }), "invalidDialect");
  for (const quote of [
    undefined,
    null,
    "",
    "ab",
    "é",
    "\t",
    "\r",
    " ",
    ",",
    "'\n",
    34,
  ])
    refused(parse("a,b", { quote }), "invalidDialect");
  for (const quote of [undefined, '"', ""])
    refused(parse("a,b", { quoteDisabled: true, quote }), "invalidDialect");
  for (const key of Object.keys(dialect())) {
    const incomplete = dialect();
    delete incomplete[key];
    refused(
      parseCsvDocument(encoder.encode("a,b"), incomplete),
      "invalidDialect",
    );
  }
});

test("truthy or falsey forged booleans are not native metadata", () => {
  for (const key of [
    "quoteDisabled",
    "hasHeaderRow",
    "utf8Bom",
    "preserveEmptyStrings",
  ])
    for (const value of [
      "true",
      "false",
      0,
      1,
      null,
      undefined,
      [],
      {},
      new Boolean(false),
    ])
      refused(parse("a,b", { [key]: value }), "invalidDialect");
  const throwing = dialect();
  Object.defineProperty(throwing, "delimiter", {
    get() {
      throw new Error("private metadata must never be returned");
    },
  });
  refused(parseCsvDocument(encoder.encode("a,b"), throwing), "invalidDialect");
});

test("byte and encoded cell limits are inclusive and bounded", () => {
  const { maximumBytes, maximumCellBytes } = csvDocumentLimits;
  const column = "x".repeat(1023);
  const exact = (column + "\n").repeat(maximumBytes / 1024);
  assert.equal(encoder.encode(exact).length, maximumBytes);
  assert.equal(table(exact).rowCount, maximumBytes / 1024);
  refused(parse(exact + "x"), "byteLimit");
  assert.equal(
    table("x".repeat(maximumCellBytes)).rows[0][0].length,
    maximumCellBytes,
  );
  refused(parse("x".repeat(maximumCellBytes + 1)), "cellLimit");
  assert.equal(
    table('"' + "é".repeat(maximumCellBytes / 2) + '"').rows[0][0].length,
    maximumCellBytes / 2,
  );
  refused(parse('"' + "é".repeat(maximumCellBytes / 2 + 1) + '"'), "cellLimit");
  refused(
    parse('"' + '""'.repeat(maximumCellBytes / 2 + 1) + '"'),
    "cellLimit",
  );
});

test("column, record and total node limits apply to all parsed records", () => {
  const { maximumColumns, maximumRecords, maximumNodes } = csvDocumentLimits;
  assert.equal(
    table(",".repeat(maximumColumns - 1)).columnCount,
    maximumColumns,
  );
  refused(parse(",".repeat(maximumColumns)), "columnLimit");
  assert.equal(table("\n".repeat(maximumRecords)).recordCount, maximumRecords);
  refused(parse("\n".repeat(maximumRecords + 1)), "recordLimit");
  // Nine cells plus one record are ten nodes, with no large cell allocations.
  assert.equal(
    table((",".repeat(8) + "\n").repeat(maximumNodes / 10)).recordCount,
    maximumNodes / 10,
  );
  // Ten cells plus one record reach the node cap before the record cap.
  refused(
    parse((",".repeat(9) + "\n").repeat(Math.floor(maximumNodes / 11) + 1)),
    "nodeLimit",
  );
});

test("shown rows are capped while total counts and the header remain exact", () => {
  const { maximumDisplayedRows } = csvDocumentLimits;
  const text = Array.from(
    { length: maximumDisplayedRows + 2 },
    (_, i) => `r${i},${i}`,
  ).join("\n");
  const result = table("name,value\n" + text, { hasHeaderRow: true });
  assert.deepEqual(result.header, ["name", "value"]);
  assert.equal(result.recordCount, maximumDisplayedRows + 3);
  assert.equal(result.rowCount, maximumDisplayedRows + 2);
  assert.equal(result.rows.length, maximumDisplayedRows);
  assert.deepEqual(result.rows.at(-1), [
    `r${maximumDisplayedRows - 1}`,
    String(maximumDisplayedRows - 1),
  ]);
  assert.equal(result.truncated, true);
  assert.equal(table("x\n".repeat(maximumDisplayedRows)).truncated, false);
  assert.equal(table("x\n".repeat(maximumDisplayedRows + 1)).truncated, true);
});

test("unshown records still undergo UTF-8, quote, width and cell validation", () => {
  const prefix = "a\n".repeat(csvDocumentLimits.maximumDisplayedRows);
  refused(parse(prefix + '"unterminated-private-data'), "invalidCsv");
  refused(parse(prefix + '"a"bad'), "invalidCsv");
  refused(
    parse(prefix + ",".repeat(csvDocumentLimits.maximumColumns)),
    "columnLimit",
  );
  refused(
    parse(prefix + "x".repeat(csvDocumentLimits.maximumCellBytes + 1)),
    "cellLimit",
  );
  const good = encoder.encode(prefix);
  const invalid = new Uint8Array(good.length + 1);
  invalid.set(good);
  invalid[good.length] = 0xff;
  refused(parseCsvDocument(invalid, dialect()), "invalidUtf8");
});
