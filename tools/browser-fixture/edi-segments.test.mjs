import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

const source = await readFile(
  new URL(
    "../../apps/web/src/catalog/previews/edi-segments.ts",
    import.meta.url,
  ),
  "utf8",
);
const { outputText } = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
  },
});
const { ediSegments } = await import(
  `data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`
);
const isa =
  "ISA*00*          *00*          *ZZ*BRAIDSENDER    *ZZ*BRAIDRECEIVER  *261024*1200*^*00501*000000330*0*T*:~";

test("mixed X12 declarations are consumed whole before switching body separators", () => {
  const first = isa + "BEG*ONE*A:B^C:D~IEA*0*000000330~";
  const second = [...first.replace("ONE", "TWO")]
    .map(
      (value) => ({ "*": "|", ":": "~", "~": ":", "^": "!" })[value] ?? value,
    )
    .join("");
  const segments = ediSegments(first + "\n" + second, "application/edi-x12");
  assert.equal(segments.length, 6);
  assert.equal(segments[3].length, 106);
  assert.equal(segments[4], "BEG|TWO|A~B!C~D:");
});

test("headerless and malformed X12 never acquire guessed segment boundaries", () => {
  for (const text of [
    "BGM|220|ONE!BGM|220|TWO!",
    "ST*850*0330~BEG*00*SA*ONE~SE*3*0330~",
    "ISA*short~BEG*00*SA*ONE~",
    isa.replace("00501", "abcde"),
    isa.replace(":~", "**"),
  ])
    assert.equal(ediSegments(text, "application/edi-x12"), null);
});

test("fixed-width ISA declares standard, custom and newline segment syntax", () => {
  for (const text of [
    isa + "IEA*0*000000330~",
    (isa + "IEA*0*000000330~").replaceAll("*", "|").replaceAll("~", "!"),
    (isa + "IEA*0*000000330~").replaceAll("~", "\n"),
  ])
    assert.equal(ediSegments(text, "application/edi-x12").length, 2);
});

test("EDIFACT release syntax and non-EDI raw display are unchanged", () => {
  assert.deepEqual(
    ediSegments("BGM+220+ONE?'TWO'UNT+2+1'", "application/edifact"),
    ["BGM+220+ONE?'TWO'", "UNT+2+1'"],
  );
  assert.equal(ediSegments("{}", "application/json"), null);
});

function edifact(reference, body, count, custom = false) {
  return custom
    ? `UNA;|.! $UNB|UNOC;3|S|R|261026;1200|${reference}$UNH|M${reference}|ORDERS;D;96A;UN$${body}UNT|${count + 2}|M${reference}$UNZ|1|${reference}$`
    : `UNB+UNOC:3+S+R+261026:1200+${reference}'UNH+M${reference}+ORDERS:D:96A:UN'${body}UNT+${count + 2}+M${reference}'UNZ+1+${reference}'`;
}

test("each EDIFACT interchange uses its own UNA and release without changing bytes", () => {
  const first = edifact("A", "BGM+220+FIRST?'VALUE'", 1);
  const body = `BGM|220|${"X".repeat(100)}!!VALUE!$LITERAL$`.repeat(180);
  const second = edifact("B", body, 180, true);
  assert.ok(second.length > 16384);
  const segments = ediSegments(
    first + "\r\n \t" + second,
    "application/edifact",
  );
  assert.equal(segments.length, 190);
  assert.equal(segments[2], "BGM+220+FIRST?'VALUE'");
  assert.equal(segments[5], "UNA;|.! $");
  assert.equal(segments[8], `BGM|220|${"X".repeat(100)}!!VALUE!$LITERAL$`);
  const crossRole = second
    .replace("UNA;", "UNA'")
    .replace("UNOC;3", "UNOC'3")
    .replace("ORDERS;D;96A;UN", "ORDERS'D'96A'UN")
    .replaceAll("$", "?");
  const cross = ediSegments(first + "\r\n" + crossRole, "application/edifact");
  assert.equal(cross.length, 190);
  assert.equal(cross[5], "UNA'|.! ?");
  assert.equal(cross[8], `BGM|220|${"X".repeat(100)}!!VALUE!?LITERAL?`);
});

test("default EDIFACT syntax resumes after UNZ and a standalone UNT", () => {
  const second = edifact("B", "BGM+220+DEFAULT?'VALUE'", 1);
  for (const first of [
    edifact("A", "BGM|220|FIRST!!VALUE!$LITERAL$", 1, true),
    "UNA;|.! $UNH|S|ORDERS;D;96A;UN$BGM|220|ONE$UNT|3|S$",
  ]) {
    const segments = ediSegments(
      first + "\r\n\t" + second,
      "application/edifact",
    );
    assert.deepEqual(segments.slice(-5), [
      "UNB+UNOC:3+S+R+261026:1200+B'",
      "UNH+MB+ORDERS:D:96A:UN'",
      "BGM+220+DEFAULT?'VALUE'",
      "UNT+3+MB'",
      "UNZ+1+B'",
    ]);
  }
});

test("UNA-looking fields and released delimiters never introduce a new syntax", () => {
  const field = "BGM|220|ONE!!TWO!$LITERAL'UNA:+.? '$";
  const bytes = edifact("A", field, 1, true);
  assert.equal(ediSegments(bytes, "application/edifact")[3], field);
  const literal = "BEFOREUNA;|.! $AFTER";
  const standard = edifact("A", `BGM+220+${literal}'`, 1);
  assert.equal(
    ediSegments(standard, "application/edifact")[2],
    `BGM+220+${literal}'`,
  );
  assert.equal(
    ediSegments(
      edifact("A", `BGM+220+${literal.repeat(1000)}'`, 1),
      "application/edifact",
    ),
    null,
  );
});

test("malformed advice, unfinished released segments and oversized displays stay raw", () => {
  for (const text of [
    "UNA:++? $UNB+bad'",
    "UNA;|.! $UNH|A$BGM|VALUE!$",
    "X".repeat(65537),
    " ".repeat(65537),
    edifact("A", `BGM+220+${"X".repeat(16384)}'`, 1),
  ]) {
    assert.equal(ediSegments(text, "application/edifact"), null);
  }
  assert.equal(
    ediSegments(
      edifact("A", "BGM+220+X'".repeat(2100), 2100),
      "application/edifact",
    ).length,
    2000,
  );
});
