/** The .NET whitespace set used by catalog name admission, including NEL and excluding BOM. */
export function trimCatalogName(value: string): string {
  let start = 0,
    end = value.length;
  while (start < end && whitespace(value.charCodeAt(start))) ++start;
  while (end > start && whitespace(value.charCodeAt(end - 1))) --end;
  return value.slice(start, end);
}

function whitespace(code: number): boolean {
  return (
    (code >= 0x09 && code <= 0x0d) ||
    code === 0x20 ||
    code === 0x85 ||
    code === 0xa0 ||
    code === 0x1680 ||
    (code >= 0x2000 && code <= 0x200a) ||
    code === 0x2028 ||
    code === 0x2029 ||
    code === 0x202f ||
    code === 0x205f ||
    code === 0x3000
  );
}
