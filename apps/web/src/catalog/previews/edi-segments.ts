/** Bounded display only. The media identity comes from verified run metadata, not a filename. */
export function ediSegments(
  text: string,
  mediaType: string,
): readonly string[] | null {
  if (
    mediaType !== "application/edi-x12" &&
    mediaType !== "application/edifact"
  )
    return null;
  if (text.length > 65536 || new TextEncoder().encode(text).length > 65536)
    return null;
  const content = text.trimStart();
  if (mediaType === "application/edi-x12") return x12Segments(content);
  return edifactSegments(content);
}

type EdifactFrame =
  | "outside"
  | "interchange"
  | "group"
  | "message"
  | "groupMessage"
  | "standalone"
  | "invalid";

/** Framing only; never read an advice-shaped substring from within a field. */
function edifactFrame(frame: EdifactFrame, tag: string): EdifactFrame {
  if (frame === "outside" && tag === "UNB") return "interchange";
  if (frame === "outside" && tag === "UNH") return "standalone";
  if (frame === "interchange" && tag === "UNG") return "group";
  if (frame === "interchange" && tag === "UNH") return "message";
  if (frame === "interchange" && tag === "UNZ") return "outside";
  if (frame === "group" && tag === "UNH") return "groupMessage";
  if (frame === "group" && tag === "UNE") return "interchange";
  if (frame === "message" && tag === "UNT") return "interchange";
  if (frame === "groupMessage" && tag === "UNT") return "group";
  if (frame === "standalone" && tag === "UNT") return "outside";
  if (["UNB", "UNG", "UNH", "UNT", "UNE", "UNZ"].includes(tag))
    return "invalid";
  return ["message", "groupMessage", "standalone"].includes(frame)
    ? frame
    : "invalid";
}

function edifactSegments(content: string): readonly string[] | null {
  let frame: EdifactFrame = "outside";
  let advice = false;
  let element = "+";
  let terminator = "'";
  let release = "?";
  const segments: string[] = [];
  let offset = 0;
  while (offset < content.length) {
    while (offset < content.length && /[ \t\r\n]/.test(content[offset]!))
      offset++;
    if (offset === content.length) break;
    if (content.startsWith("UNA", offset)) {
      if (frame !== "outside" || advice) return null;
      const header = content.slice(offset, offset + 9);
      const syntax = [...header.slice(3)].filter(
        (value, index) => index !== 4 || value !== " ",
      );
      if (
        header.length !== 9 ||
        syntax.some((value) => !/^[!-/:-@[-`{-~]$/.test(value)) ||
        new Set(syntax).size !== syntax.length
      )
        return null;
      element = header[4]!;
      release = header[6]!;
      terminator = header[8]!;
      advice = true;
      segments.push(header);
      offset += 9;
      if (segments.length === 2000) return segments;
      continue;
    }
    if (frame === "outside" && !advice) {
      element = "+";
      release = "?";
      terminator = "'";
    }
    const start = offset;
    let escaped = false;
    let ended = false;
    while (offset < content.length) {
      const character = content[offset++];
      if (escaped) escaped = false;
      else if (character === release) escaped = true;
      else if (character === terminator) {
        ended = true;
        break;
      }
    }
    if (!ended) return null;
    const segment = content.slice(start, offset);
    if (new TextEncoder().encode(segment).length > 16384) return null;
    segments.push(segment);
    const separator = segment.indexOf(element);
    const tag = segment.slice(
      0,
      separator < 0 ? segment.length - 1 : separator,
    );
    frame = edifactFrame(frame, tag);
    advice = false;
    if (segments.length === 2000) return segments;
  }
  return segments;
}

function x12Segments(content: string): readonly string[] | null {
  const segments: string[] = [];
  let offset = 0;
  let terminator: string | null = null;
  while (offset < content.length) {
    while (/\s/.test(content[offset] ?? "") && offset < content.length)
      offset++;
    if (offset === content.length) break;
    if (content.startsWith("ISA", offset)) {
      const header = content.slice(offset, offset + 106);
      const fields = header.slice(0, 105).split(header[3]!);
      const widths = [2, 10, 2, 10, 2, 15, 2, 15, 6, 4, 1, 5, 9, 1, 1, 1];
      if (
        header.length !== 106 ||
        fields.length !== 17 ||
        widths.some((width, index) => fields[index + 1]?.length !== width) ||
        !/^\d{5}$/.test(fields[12]!) ||
        !/^[\x20-\x7e]+$/.test(header.slice(0, 105)) ||
        header.slice(0, 105).includes(header[105]!)
      )
        return null;
      const syntax = [header[3]!, header[104]!, header[105]!];
      if (fields[12]! >= "00501") syntax.push(header[82]!);
      if (
        new Set(syntax).size !== syntax.length ||
        syntax.some(
          (value) =>
            !/^[!-/:-@[-`{-~]$/.test(value) &&
            !(value === "\n" && value === header[105]),
        )
      )
        return null;
      terminator = header[105]!;
      segments.push(header);
      offset += 106;
    } else {
      if (terminator === null) return null;
      const end = content.indexOf(terminator, offset);
      const next = end < 0 ? content.length : end + 1;
      segments.push(content.slice(offset, next).trim());
      offset = next;
    }
    if (segments.length === 2000) return segments;
  }
  return terminator === null ? null : segments;
}
