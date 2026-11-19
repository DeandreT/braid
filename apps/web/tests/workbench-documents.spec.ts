import "@angular/compiler";
import { expect, test } from "@playwright/test";
import {
  compareDocuments,
  displayBytes,
  displayDocument,
} from "../src/workbench/workbench-documents";

const x12 =
  "ISA*00*          *00*          *ZZ*BRAIDSENDER    *ZZ*BRAIDRECEIVER  *261024*1200*^*00501*000000330*0*T*:~IEA*0*000000330~";
const document = (text: string, mediaType = "application/edi-x12") => ({
  name: null,
  bytesBase64Url: Buffer.from(text).toString("base64url"),
  mediaType,
});

test("FlexText layout belongs to the saved artifact, including empty output", () => {
  const settings = {
    schema: "braid.flextext-settings.v1",
    optionsJson: '{"root_name":"Document"}',
  };
  for (const text of ["", "Line 1\r\nRésumé 🧵"]) {
    const artifact = {
      ...document(text, "text/plain"),
      documentSettings: settings,
    };
    const view = displayDocument(artifact);
    expect(view.flexLayout).toBe(settings.optionsJson);
    expect(view.text).toBe(text);
    expect(view.document).toBe(artifact);
    expect(view.byteCount).toBe(Buffer.byteLength(text));
  }
  expect(displayDocument(document("text", "text/plain")).flexLayout).toBeNull();
  expect(
    displayDocument({
      ...document("text", "text/plain"),
      documentSettings: { ...settings, schema: "other" },
    }).flexLayout,
  ).toBeNull();
});

test("an incomplete EDI display window is raw, not a complete segment list", () => {
  const value = displayDocument(document(x12 + " ".repeat(65536)));
  expect(value.truncated).toBe(true);
  expect(value.segments).toBeNull();
});

test("EDI representation uses declared media and keeps exact source bytes", () => {
  const source = document(x12);
  const value = displayDocument(source);
  expect(value.segments).toHaveLength(2);
  expect(value.text).toBe(x12);
  expect(value.document).toBe(source);
  expect(displayBytes(Buffer.from(x12), "orders.x12").segments).toBeNull();
  expect(
    displayBytes(Buffer.from(x12), "orders.x12", "application/json").segments,
  ).toBeNull();
  expect(displayDocument(document("ST*850*1~SE*2*1~")).segments).toBeNull();
});

test("readable segments never replace the byte-level difference", () => {
  const expected = document(x12);
  const actual = document(x12.replaceAll("~", "~\n"));
  expect(displayDocument(expected).segments).toEqual(
    displayDocument(actual).segments,
  );
  const difference = compareDocuments([expected], [actual])[0];
  expect(difference.equal).toBe(false);
  expect(difference.firstByte).not.toBeNull();
  expect(difference.actual?.document).toBe(actual);
});

test("Result and both Diff sides share interchange-local EDIFACT display without rewriting bytes", () => {
  const first =
    "UNA;|.! $UNB|UNOC;3|S|R|261026;1200|A$UNH|MA|ORDERS;D;96A;UN$BGM|220|FIRST!!VALUE!$LITERAL$UNT|3|MA$UNZ|1|A$";
  const second =
    "UNB+UNOC:3+S+R+261026:1200+B'UNH+MB+ORDERS:D:96A:UN'BGM+220+SECOND?'VALUE'UNT+3+MB'UNZ+1+B'";
  const bytes = first + "\r\n \t" + second;
  const expected = document(bytes, "application/edifact");
  const actual = document(bytes, "application/edifact");
  const result = displayDocument(actual);
  const difference = compareDocuments([expected], [actual])[0]!;
  expect(result.segments).toHaveLength(11);
  expect(result.segments?.[8]).toBe("BGM+220+SECOND?'VALUE'");
  expect(difference.expected?.segments).toEqual(result.segments);
  expect(difference.actual?.segments).toEqual(result.segments);
  expect(difference.equal).toBe(true);
  expect(difference.actual?.document.bytesBase64Url).toBe(
    actual.bytesBase64Url,
  );
  expect(difference.expected?.document.bytesBase64Url).toBe(
    expected.bytesBase64Url,
  );
});

test("display remains bounded and invalid UTF-8 stays lossless hexadecimal", () => {
  const bytes = Buffer.alloc(70_000, 0xff);
  const value = displayBytes(bytes, null, "application/edifact");
  expect(value.byteCount).toBe(70_000);
  expect(value.truncated).toBe(true);
  expect(value.format).toBe("hex");
  expect(value.segments).toBeNull();
  expect(value.text.startsWith("ff ff ff")).toBe(true);
  expect(value.text.length).toBeLessThan(200_000);
  expect(
    displayBytes(
      Buffer.from("BGM+220+ONE?'TWO'UNT+2+1'"),
      null,
      "application/edifact",
    ).segments,
  ).toEqual(["BGM+220+ONE?'TWO'", "UNT+2+1'"]);
});

test("JSON tree keeps lexical numbers and never guesses or presents an incomplete tree", () => {
  const bytes = Buffer.from('{"order":[9007199254740993]}');
  expect(displayBytes(bytes, null, "application/json").jsonOutline).toEqual([
    { depth: 0, text: "$: {1 fields}" },
    { depth: 1, text: '"order": [1 items]' },
    { depth: 2, text: "[0]: 9007199254740993" },
  ]);
  expect(displayBytes(bytes, "test.json").jsonOutline).toBeNull();
  for (const sample of [
    '{"v":1,"v":2}',
    "[1,",
    JSON.stringify(Array(1001).fill(0)),
  ]) {
    const view = displayBytes(Buffer.from(sample), null, "application/json");
    expect(view.jsonOutline).toBeNull();
    expect(view.text).toBe(sample);
  }
});
