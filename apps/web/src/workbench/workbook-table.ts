/** Read-only display of Ferrule's stored portable worksheet. Never inflate,
 * resolve relationships, interpret formulas, or use this result for download. */
export const workbookMedia =
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
const parts = [
  "[Content_Types].xml",
  "_rels/.rels",
  "xl/workbook.xml",
  "xl/_rels/workbook.xml.rels",
  "xl/worksheets/sheet1.xml",
];
const utf8 = new TextDecoder("utf-8", { fatal: true });
export interface WorkbookTable {
  header: string[];
  rows: string[][];
  total: number;
}

export function workbookTable(bytes: Uint8Array): WorkbookTable | null {
  if (bytes.length > 256 * 1024) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let p = 0,
    sheet = "";
  try {
    for (const name of parts) {
      if (
        p + 30 > bytes.length ||
        view.getUint32(p, true) !== 0x04034b50 ||
        view.getUint32(p + 6, true) !== 0 ||
        view.getUint16(p + 26, true) !== name.length ||
        view.getUint16(p + 28, true) !== 0
      )
        return null;
      const size = view.getUint32(p + 18, true),
        start = p + 30 + name.length;
      if (
        view.getUint32(p + 22, true) !== size ||
        start + size > bytes.length ||
        utf8.decode(bytes.subarray(p + 30, start)) !== name
      )
        return null;
      p = start + size;
      if (name === parts[4]) sheet = utf8.decode(bytes.subarray(start, p));
    }
    if (
      p + 22 > bytes.length ||
      view.getUint32(p, true) !== 0x02014b50 ||
      view.getUint32(bytes.length - 22, true) !== 0x06054b50 ||
      view.getUint32(bytes.length - 6, true) !== p
    )
      return null;
  } catch {
    return null;
  }
  // Match only the emitted lexical subset. No XML parser or markup insertion.
  const prefix =
    '<?xml version="1.0" encoding="utf-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>';
  if (!sheet.startsWith(prefix)) return null;
  let i = prefix.length,
    count = 0;
  const table: WorkbookTable = { header: [], rows: [], total: 0 };
  const cell =
    /<c r="([A-Z])(\d{1,3})" (?:t="inlineStr"><is><t xml:space="preserve">((?:[^<&]|&amp;|&gt;|&lt;)+)<\/t><\/is>|t="n"><v>(-?\d{1,15})<\/v>|t="b"><v>([01])<\/v>)<\/c>/y;
  while (sheet.startsWith(`<row r="${count + 1}">`, i)) {
    if (count > 256) return null;
    i += `<row r="${count + 1}">`.length;
    const row: string[] = [];
    while (!sheet.startsWith("</row>", i)) {
      cell.lastIndex = i;
      const m = cell.exec(sheet);
      if (
        !m ||
        row.length >= 26 ||
        m[1] !== String.fromCharCode(65 + row.length) ||
        m[2] !== String(count + 1) ||
        (!count && m[3] === undefined)
      )
        return null;
      row.push(
        m[3] === undefined
          ? (m[4] ?? (m[5] === "1" ? "true" : "false"))
          : m[3]
              .replace(/&lt;/g, "<")
              .replace(/&gt;/g, ">")
              .replace(/&amp;/g, "&"),
      );
      i = cell.lastIndex;
    }
    if (!row.length || (count && row.length !== table.header.length))
      return null;
    if (!count) table.header = row;
    else if (count <= 50) table.rows.push(row);
    count++;
    i += 6;
  }
  table.total = count - 1;
  return count && sheet.slice(i) === "</sheetData></worksheet>" ? table : null;
}
