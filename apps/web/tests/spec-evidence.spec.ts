import { expect, test } from "@playwright/test";
import { createHash } from "node:crypto";
import {
  evidence,
  inventory,
  maximumEvidenceBytes,
  readEvidence,
  type SpecObject,
} from "../src/catalog/specs/spec-model";

const id = "50000000-0000-4000-8000-000000000001";
const word = ["docx-paragraph", "word/document.xml", 2, null, null, null];
const cell = [
  "xlsx-cell",
  "xl/worksheets/sheet1.xml",
  "Données",
  "XFD1048576",
  "String",
  false,
];
const v2 = (
  locator: unknown = word,
  text = "café 東京",
  formula: unknown = null,
) => [
  "braid.spec-evidence.v2",
  "office-body-text-only",
  [0, text, locator, formula],
];
function bytesOf(body: string): ArrayBuffer {
  return new TextEncoder().encode(body).buffer;
}
function objectFor(
  bytes: ArrayBuffer,
  overrides: Partial<SpecObject> = {},
): SpecObject {
  return {
    objectId: id,
    sourceId: id,
    sourceVersion: 1,
    kind: "Extraction",
    format: "Docx",
    outcome: "Partial",
    state: "published",
    byteCount: bytes.byteLength,
    digest: createHash("sha256").update(new Uint8Array(bytes)).digest("hex"),
    ...overrides,
  };
}

test("v1 and v2 dispatch preserve coordinates, formulas and empty cache distinctions", () => {
  expect(
    evidence([
      "braid.spec-evidence.v1",
      "complete",
      [0, "é", "/a", 5, 2, 2, 2],
    ]),
  ).toEqual({
    version: 1,
    code: "complete",
    chunks: [
      {
        ordinal: 0,
        text: "é",
        formula: null,
        locator: {
          kind: "byte-range",
          offset: 5,
          length: 2,
          start: 2,
          end: 2,
          pointer: "/a",
        },
      },
    ],
  });
  expect(evidence(v2()).chunks[0]!.locator).toEqual({
    kind: "docx-paragraph",
    part: "word/document.xml",
    paragraph: 2,
    table: null,
    row: null,
    cell: null,
  });
  expect(
    evidence(v2(["docx-paragraph", "word/document.xml", 3, 2, 4, 5])).chunks[0]!
      .locator,
  ).toMatchObject({ table: 2, row: 4, cell: 5, paragraph: 3 });
  for (const cached of [false, true]) {
    expect(
      evidence(v2([...cell.slice(0, 5), cached], "", '""')).chunks[0],
    ).toMatchObject({
      text: "",
      formula: '""',
      locator: { kind: "xlsx-cell", hasCachedValue: cached },
    });
  }
  expect(
    evidence(v2(["byte-range", 5, 2, 2, 2, null])).chunks[0]!.locator,
  ).toMatchObject({ kind: "byte-range", length: 2 });
});

for (const [name, location] of [
  ["unknown kind", ["html", "<img src=x>"]],
  ["object instead of tuple", { kind: "pdf-page", page: 1 }],
  ["extra field", [...word, "private"]],
  [
    "trailing path newline",
    ["docx-paragraph", "word/document.xml\n", 1, null, null, null],
  ],
  [
    "trailing cell newline",
    ["xlsx-cell", "xl/a.xml", "Sheet", "A1\n", "Blank", false],
  ],
  [
    "absolute path",
    ["docx-paragraph", "/word/document.xml", 1, null, null, null],
  ],
  [
    "traversal",
    ["docx-paragraph", "word/../document.xml", 1, null, null, null],
  ],
  [
    "encoded path",
    ["docx-paragraph", "word/%64ocument.xml", 1, null, null, null],
  ],
  [
    "path injection",
    ["docx-paragraph", "<img onerror=alert(1)>.xml", 1, null, null, null],
  ],
  [
    "partial table coordinates",
    ["docx-paragraph", "word/document.xml", 1, 1, null, 1],
  ],
  [
    "zero paragraph",
    ["docx-paragraph", "word/document.xml", 0, null, null, null],
  ],
  [
    "integer overflow",
    ["docx-paragraph", "word/document.xml", 2147483648, null, null, null],
  ],
  ["unsafe integer", ["pdf-page", 9007199254740992]],
  ["fraction", ["pdf-page", 1.5]],
  ["page limit", ["pdf-page", 257]],
  ["page zero", ["pdf-page", 0]],
  ["uncontracted missing flag", ["pdf-page", 1, true]],
  [
    "cell overflow",
    ["xlsx-cell", "xl/a.xml", "Sheet", "MWLQKWW1", "Blank", false],
  ],
  [
    "cell column limit",
    ["xlsx-cell", "xl/a.xml", "Sheet", "XFE1", "Blank", false],
  ],
  [
    "cell row limit",
    ["xlsx-cell", "xl/a.xml", "Sheet", "A1048577", "Blank", false],
  ],
  ["cell alias", ["xlsx-cell", "xl/a.xml", "Sheet", "A01", "Blank", false]],
  [
    "unknown value kind",
    ["xlsx-cell", "xl/a.xml", "Sheet", "A1", "Calculated", false],
  ],
  [
    "nonboolean cache",
    ["xlsx-cell", "xl/a.xml", "Sheet", "A1", "String", "false"],
  ],
  [
    "invalid sheet",
    ["xlsx-cell", "xl/a.xml", "Sheet/name", "A1", "String", false],
  ],
  ["byte sum limit", ["byte-range", maximumEvidenceBytes, 1, 1, 1, null]],
  ["reversed lines", ["byte-range", 0, 1, 2, 1, null]],
] as const) {
  test(`invalid locator is refused: ${name}`, () =>
    expect(() => evidence(v2(location))).toThrow("Invalid evidence"));
}

test("version and tuple mismatches, extra fields, surrogate text and invented formula states fail closed", () => {
  for (const value of [
    ["braid.spec-evidence.v3", "complete"],
    ["braid.spec-evidence.v1", "complete", [0, "text", word, null]],
    ["braid.spec-evidence.v2", "complete", [0, "text", null, 0, 4, 1, 1]],
    [
      "braid.spec-evidence.v2",
      "complete",
      [0, "text", word, null, { missingText: false }],
    ],
    ["braid.spec-evidence.v2", "complete", [1, "text", word, null]],
    v2(word, "\ud800"),
    v2(word, "text", "=1+2"),
    v2(cell, "result", "1+2"),
    v2([...cell.slice(0, 5), true], "cache", null),
    v2(cell, "", ""),
    v2(cell, "", { html: "<script>" }),
  ])
    expect(() => evidence(value)).toThrow();
});

test("PDF tuple preserves canonical missing-text facts and blank pages", () => {
  const parsed = evidence([
    "braid.spec-evidence.v2",
    "pdf-page-text-only",
    [0, "first", ["pdf-page", 1], null, false],
    [1, "", ["pdf-page", 2], null, true],
  ]);
  expect(parsed.chunks.map((c) => [c.text, c.locator])).toEqual([
    ["first", { kind: "pdf-page", page: 1 }],
    ["", { kind: "pdf-page", page: 2 }],
  ]);
  expect(parsed.chunks[1]!.missingText).toBe(true);
  expect(() =>
    evidence({
      format: "braid.spec-pdf-extraction.v1",
      status: "needsOcr",
      pages: [],
    }),
  ).toThrow();
});

test("PDF status and missing-text facts must agree with retained evidence", async () => {
  const decode = (
    code: string,
    chunks: unknown[],
    outcome: SpecObject["outcome"],
  ) => {
    const bytes = bytesOf(
      JSON.stringify(["braid.spec-evidence.v2", code, ...chunks]),
    );
    return readEvidence(bytes, objectFor(bytes, { format: "Pdf", outcome }));
  };
  const blank = [0, "\u0085\u3000", ["pdf-page", 1], null, true];
  expect(
    (await decode("pdf-needs-ocr", [blank], "Unsupported")).chunks[0]!
      .missingText,
  ).toBe(true);
  expect(
    (await decode("pdf-encrypted", [], "Unsupported")).chunks,
  ).toHaveLength(0);
  await expect(
    decode("pdf-page-text-only", [blank], "Partial"),
  ).rejects.toThrow();
  await expect(
    decode("pdf-encrypted", [blank], "Unsupported"),
  ).rejects.toThrow();
  await expect(decode("pdf-needs-ocr", [], "Unsupported")).rejects.toThrow();
  await expect(
    decode(
      "pdf-page-text-only",
      [[0, "text", ["pdf-page", 1], null, false]],
      "Complete",
    ),
  ).rejects.toThrow();
  for (const tuple of [
    [0, "text", ["pdf-page", 1], null, true],
    [0, "", ["pdf-page", 1], null],
    [0, "text", ["pdf-page", 2], null, false],
  ])
    await expect(
      decode("pdf-page-text-only", [tuple], "Partial"),
    ).rejects.toThrow();
});

test("exact UTF-8 response bytes, escaping and framing bind the inventory digest and count", async () => {
  const body = JSON.stringify(v2());
  const bytes = bytesOf(body);
  expect(bytes.byteLength).toBeGreaterThan(body.length);
  expect((await readEvidence(bytes, objectFor(bytes))).chunks[0]!.text).toBe(
    "café 東京",
  );
  await expect(
    readEvidence(bytes, objectFor(bytes, { byteCount: body.length })),
  ).rejects.toThrow();
  const escaped = bytesOf(
    body.replace("café 東京", "caf\\u00e9 \\u6771\\u4eac"),
  );
  expect(
    (await readEvidence(escaped, objectFor(escaped))).chunks[0]!.text,
  ).toBe("café 東京");
  await expect(readEvidence(escaped, objectFor(bytes))).rejects.toThrow();
  await expect(
    readEvidence(bytes, objectFor(bytes, { digest: "a".repeat(64) })),
  ).rejects.toThrow();
  const malformed = Uint8Array.from([0xff]).buffer;
  await expect(readEvidence(malformed, objectFor(malformed))).rejects.toThrow();
  const oversized = new ArrayBuffer(maximumEvidenceBytes + 1);
  await expect(readEvidence(oversized, objectFor(oversized))).rejects.toThrow();
});

test("source format, outcome and evidence version must agree", async () => {
  const bytes = bytesOf(JSON.stringify(v2()));
  for (const overrides of [
    { format: "Xlsx" },
    { format: "Text" },
    { outcome: "Complete" },
    { outcome: "Malformed" },
    { kind: "Original" },
    { state: "retired" },
  ] as Partial<SpecObject>[])
    await expect(
      readEvidence(bytes, objectFor(bytes, overrides)),
    ).rejects.toThrow();
  const legacy = bytesOf(
    JSON.stringify(["braid.spec-evidence.v1", "office-body-text-only"]),
  );
  await expect(readEvidence(legacy, objectFor(legacy))).rejects.toThrow();
  const pdf = bytesOf(JSON.stringify(v2(["pdf-page", 1])));
  await expect(readEvidence(pdf, objectFor(pdf))).rejects.toThrow();
  const bundle = { bundleId: id, version: 1, tombstoned: false };
  expect(
    inventory({ bundle, objects: [objectFor(bytes)] }, id).objects[0]!.outcome,
  ).toBe("Partial");
  for (const overrides of [
    { format: "Unknown" },
    { outcome: "NeedsOcr" },
    { format: ["Docx"] },
    { outcome: ["Partial"] },
    { sourceVersion: 9007199254740992 },
  ])
    expect(() =>
      inventory(
        { bundle, objects: [{ ...objectFor(bytes), ...overrides }] },
        id,
      ),
    ).toThrow();
});
