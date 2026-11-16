import "@angular/compiler";
import { Injector, runInInjectionContext, signal } from "@angular/core";
import { expect, test } from "@playwright/test";
import { DocumentViewComponent } from "../src/workbench/document-view.component";
import { RevisionRegressionComponent } from "../src/catalog/regression/revision-regression.component";
import { RegressionError } from "../src/catalog/regression/regression-api";
import type { DocumentDraft } from "../src/catalog/regression/regression-view-recovery";
import {
  compareDocuments,
  displayBytes,
  displayDocument,
  type DocumentRepresentation,
  type WorkbenchDocument,
} from "../src/workbench/workbench-documents";
import {
  csvDocumentLimits,
  type CsvDocumentDialect,
} from "../src/workbench/csv-document";

const dialect: CsvDocumentDialect = {
  schema: "braid.csv-dialect.v1",
  delimiter: ",",
  quote: '"',
  quoteDisabled: false,
  hasHeaderRow: true,
  utf8Bom: false,
  preserveEmptyStrings: true,
};

test("restored regression drafts validate generated dialect data before retaining it", () => {
  // This boundary helper needs only its local sequence, not an injected API or browser.
  const component = Object.create(RevisionRegressionComponent.prototype) as {
    sequence: number;
    savedDraft(
      name: string | null,
      encoded: string,
      media: string,
      metadata: unknown,
    ): DocumentDraft;
  };
  component.sequence = 0;
  const restore = (metadata: unknown, media = "text/csv") =>
    component.savedDraft(null, "YQ", media, metadata);
  for (const metadata of [
    null,
    {},
    { ...dialect, schema: "unknown" },
    { ...dialect, delimiter: "\t" },
    { ...dialect, guessed: true },
  ]) {
    expect(() => restore(metadata)).toThrow(RegressionError);
  }
  expect(() => restore(dialect, "application/json")).toThrow(RegressionError);
  const restored = restore(dialect);
  expect(restored.csvDialect).toEqual(dialect);
  expect(restored.file?.encoded).toBe("YQ");
  expect(restored.file?.length).toBe(1);
  expect(restore(undefined, "application/json").csvDialect).toBeUndefined();
});

function document(
  text: string,
  metadata: unknown = dialect,
  mediaType: string | null = "text/csv",
): WorkbenchDocument {
  return {
    name: "Exact Named Output.CSV",
    bytesBase64Url: Buffer.from(text).toString("base64url"),
    mediaType,
    authoritativeCsvDocumentDialect: metadata,
  };
}

function view(value: DocumentRepresentation) {
  const data = signal(value);
  const component = runInInjectionContext(
    Injector.create({ providers: [] }),
    () => new DocumentViewComponent(),
  );
  // Use the same signal reads as an Angular input without starting a DOM renderer.
  Object.defineProperty(component, "data", { value: data });
  return { component, data };
}

test("validated CSV enables table and raw modes without replacing source bytes or names", () => {
  const text =
    'code,description\r\nA,"comma, doubled ""quote""\r\nand next line"\r\n';
  const source = document(text);
  const display = displayDocument(source);
  const { component } = view(display);

  expect(display.name).toBe(source.name);
  expect(display.mediaType).toBe("text/csv");
  expect(display.document).toBe(source);
  expect(display.text).toBe(text);
  expect(component.tableView()).toBe(true);
  expect(component.segmentView()).toBe(false);
  expect(component.csvTable()).toMatchObject({
    header: ["code", "description"],
    rows: [["A", 'comma, doubled "quote"\r\nand next line']],
    rowCount: 1,
    truncated: false,
  });
  component.mode.set("raw");
  expect(component.tableView()).toBe(false);
  expect(component.data().text).toBe(text);
  component.mode.set("formatted");
  expect(component.tableView()).toBe(true);
  expect(source.bytesBase64Url).toBe(Buffer.from(text).toString("base64url"));
});

test("declared TSV uses its dialect and does not invent a header or pad ragged rows", () => {
  const text = 'first\t"comma,value\nand line"\t\nlast\n';
  const display = displayDocument(
    document(
      text,
      { ...dialect, delimiter: "\t", hasHeaderRow: false },
      "text/tab-separated-values",
    ),
  );
  const { component } = view(display);
  expect(component.tableView()).toBe(true);
  expect(component.csvTable()).toMatchObject({
    header: null,
    rows: [["first", "comma,value\nand line", ""], ["last"]],
    columnCount: 3,
  });
  expect(display.text).toBe(text);
});

test("CSV quote-disabled and custom-delimiter metadata is authoritative", () => {
  const display = displayDocument(
    document('first;second\n"literal,quote";\n', {
      ...dialect,
      delimiter: ";",
      quote: null,
      quoteDisabled: true,
    }),
  );
  expect(display.csvDocument).toMatchObject({
    kind: "table",
    header: ["first", "second"],
    rows: [['"literal,quote"', ""]],
  });
});

test("missing or malformed metadata retains raw view with a fixed explanation", () => {
  const text = '<script>alert("not HTML")</script>,value\n';
  const missing = document(text);
  delete missing.authoritativeCsvDocumentDialect;
  const inputs = [
    missing,
    document(text, null),
    document(text, { ...dialect, schema: "untrusted-payload" }),
    document(text, { ...dialect, quoteDisabled: "false" }),
    document(text, { ...dialect, delimiter: "||" }),
  ];
  for (const source of inputs) {
    const display = displayDocument(source);
    const { component } = view(display);
    expect(component.tableView()).toBe(false);
    expect(component.csvTable()).toBeNull();
    expect(component.csvUnavailableMessage()).toBe(
      "CSV/TSV table unavailable: authoritative versioned dialect metadata is missing or invalid. Raw view and the original download remain available.",
    );
    expect(display.text).toBe(text);
    expect(display.document).toBe(source);
  }
});

test("filename and payload never grant CSV identity and media never supplies a delimiter", () => {
  for (const media of [null, "text/plain", "application/json"]) {
    const { component } = view(
      displayDocument(document("a,b\n1,2", dialect, media)),
    );
    expect(component.csvTable()).toBeNull();
    expect(component.tableView()).toBe(false);
    expect(component.csvUnavailableMessage()).toBeNull();
  }
  for (const source of [
    document("a,b\n1,2", dialect, "text/tab-separated-values"),
    document("a\tb\n1\t2", { ...dialect, delimiter: "\t" }, "text/csv"),
  ]) {
    expect(displayDocument(source).csvDocument).toEqual({
      kind: "unavailable",
      reason: "invalidDialect",
    });
  }
});

test("200-row display cap parses the rest and a malformed hidden row refuses the table", () => {
  const text =
    "value\n" + Array.from({ length: 203 }, (_, i) => `${i}\n`).join("");
  const display = displayDocument(document(text));
  expect(display.csvDocument).toMatchObject({
    kind: "table",
    rowCount: 203,
    recordCount: 204,
    truncated: true,
  });
  const { component } = view(display);
  expect(component.csvTable()?.rows).toHaveLength(200);
  expect(component.csvTable()?.rows[199]).toEqual(["199"]);
  expect(display.text).toBe(text);

  const invalid = displayDocument(document(text + '"unterminated'));
  const { component: refused } = view(invalid);
  expect(invalid.csvDocument).toEqual({
    kind: "unavailable",
    reason: "invalidCsv",
  });
  expect(refused.tableView()).toBe(false);
  expect(refused.csvUnavailableMessage()).toContain(
    "does not match its declared dialect",
  );
  expect(invalid.text).toBe(text + '"unterminated');
});

test("display bounds refuse oversized CSV while retaining the bounded raw representation", () => {
  const bytes = Buffer.alloc(csvDocumentLimits.maximumBytes + 1, 0x61);
  const display = displayBytes(bytes, "oversized.csv", "text/csv", dialect);
  const { component } = view(display);
  expect(display.csvDocument).toEqual({
    kind: "unavailable",
    reason: "byteLimit",
  });
  expect(component.tableView()).toBe(false);
  expect(component.csvUnavailableMessage()).toContain("exceeds display limits");
  expect(display.byteCount).toBe(bytes.length);
  expect(display.truncated).toBe(true);
  expect(display.text).toBe("a".repeat(65536));

  const invalidUtf8 = displayBytes(
    Buffer.from([0xff]),
    null,
    "text/csv",
    dialect,
  );
  expect(invalidUtf8.csvDocument).toEqual({
    kind: "unavailable",
    reason: "invalidUtf8",
  });
  expect(invalidUtf8.format).toBe("hex");
  expect(invalidUtf8.text).toBe("ff");
});

test("table equality does not change the exact-byte comparison or either original artifact", () => {
  const expected = document("code,value\r\nA,1\r\n");
  const actual = document("code,value\nA,1\n");
  const difference = compareDocuments([expected], [actual])[0]!;
  const before = difference.expected?.csvDocument;
  const after = difference.actual?.csvDocument;
  expect(before?.kind).toBe("table");
  expect(after?.kind).toBe("table");
  if (before?.kind !== "table" || after?.kind !== "table")
    throw new Error("Expected display tables");
  expect(before.rows).toEqual(after.rows);
  expect(difference.equal).toBe(false);
  expect(difference.firstByte).not.toBeNull();
  expect(difference.expected?.document).toBe(expected);
  expect(difference.actual?.document).toBe(actual);
});

test("existing JSON and EDI display modes remain independent of CSV metadata", () => {
  const json = displayDocument(
    document('{"value":1}', dialect, "application/json"),
  );
  const { component, data } = view(json);
  expect(component.tableView()).toBe(false);
  expect(component.segmentView()).toBe(false);
  expect(json.csvDocument).toBeNull();

  const edi = displayBytes(
    Buffer.from("UNH+1+ORDERS:D:96A:UN'UNT+2+1'"),
    null,
    "application/edifact",
    dialect,
  );
  data.set(edi);
  expect(component.segmentView()).toBe(true);
  expect(component.tableView()).toBe(false);
  expect(edi.csvDocument).toBeNull();
  component.mode.set("raw");
  expect(component.segmentView()).toBe(false);
  expect(component.data().text).toBe("UNH+1+ORDERS:D:96A:UN'UNT+2+1'");
});
