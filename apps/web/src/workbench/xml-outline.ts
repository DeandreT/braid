export interface XmlOutlineLine {
  depth: number;
  text: string;
}
/** A bounded lexical outline, not an XML parser or canonicalizer. Never creates a DOM or resolves entities/URLs. */
export function xmlOutline(text: string): readonly XmlOutlineLine[] | null {
  if (text.length > 65536) return null;
  const lines: XmlOutlineLine[] = [],
    stack: string[] = [];
  let offset = text.charCodeAt(0) === 0xfeff ? 1 : 0,
    roots = 0;
  while (offset < text.length) {
    if (lines.length >= 1000 || stack.length > 64) return null;
    let end: number;
    const tail = text.slice(offset);
    if (tail.startsWith("<!--")) {
      end = text.indexOf("-->", offset + 4);
      if (end < 0) return null;
      end += 3;
    } else if (tail.startsWith("<![CDATA[")) {
      end = text.indexOf("]]>", offset + 9);
      if (end < 0 || stack.length === 0) return null;
      end += 3;
    } else if (tail.startsWith("<?")) {
      end = text.indexOf("?>", offset + 2);
      if (end < 0) return null;
      end += 2;
    } else if (tail.startsWith("<!")) return null;
    else if (tail.startsWith("<")) {
      let quote: string | null = null;
      end = offset + 1;
      for (; end < text.length; end++) {
        const char = text[end]!;
        if (quote) {
          if (char === quote) quote = null;
        } else if (char === '"' || char === "'") quote = char;
        else if (char === ">") break;
      }
      if (end === text.length) return null;
      end++;
    } else {
      end = text.indexOf("<", offset);
      if (end < 0) end = text.length;
    }
    const part = text.slice(offset, end);
    const tag =
      part.startsWith("<!") || part.startsWith("<?")
        ? null
        : /^<(\/?)([^\s<>/]+)(?:\s[^<>]*)?\/?\s*>$/u.exec(part);
    let depth = stack.length;
    if (tag) {
      const name = tag[2]!;
      if (tag[1]) {
        if (stack.pop() !== name) return null;
        depth = stack.length;
      } else {
        if (stack.length === 0 && ++roots > 1) return null;
        if (!/\/\s*>$/u.test(part)) stack.push(name);
      }
    } else if (
      !part.startsWith("<") &&
      stack.length === 0 &&
      part.trim().length !== 0
    )
      return null;
    if (part.trim().length !== 0) lines.push({ depth, text: part });
    offset = end;
  }
  return stack.length === 0 && roots === 1 ? lines : null;
}
