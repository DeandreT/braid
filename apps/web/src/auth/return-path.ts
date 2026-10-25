/** Where sign-in returns when no valid path was requested. */
export const defaultReturnPath = "/catalog";

const maximumLength = 1024;
// Application sections a return path may open. Sign-in, API and static file paths are never sections.
const sections = ["catalog", "help", "administration"];
const allowed = /^[A-Za-z0-9\-._~!$&'()*+,;=:@/?]$/;

/**
 * The server's return-path rule (Weft.Identity.BrowserReturnPath): a same-origin path of an
 * application section, never another origin, an API route or the sign-in page itself. Both test
 * suites read the shared vectors in contracts/browser-return-paths.json.
 */
export function isReturnPath(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > maximumLength ||
    value[0] !== "/"
  )
    return false;
  const fragment = value.indexOf("#");
  if (
    fragment >= 0 &&
    (!value.startsWith("/help/") ||
      !/^[A-Za-z0-9_-]+$/.test(value.slice(fragment + 1)))
  )
    return false;
  for (let index = 0; index < value.length; index++) {
    const character = value[index];
    if (index === fragment) continue;
    if (character === "%") {
      const hex = value.slice(index + 1, index + 3);
      if (!/^[0-9A-Fa-f]{2}$/.test(hex)) return false;
      // An encoded octet must not hide a separator (/ \), a dot segment (.), another escape (%)
      // or a control or non-ASCII byte.
      const octet = parseInt(hex, 16);
      if (
        octet < 0x20 ||
        octet >= 0x7f ||
        octet === 0x2f ||
        octet === 0x5c ||
        octet === 0x2e ||
        octet === 0x25
      )
        return false;
      index += 2;
    } else if (!allowed.test(character)) return false;
  }
  const query = value.indexOf("?");
  const end = Math.min(
    query < 0 ? value.length : query,
    fragment < 0 ? value.length : fragment,
  );
  const segments = value.slice(0, end).slice(1).split("/");
  return (
    sections.includes(segments[0]) &&
    segments.every(
      (segment) => segment.length > 0 && segment !== "." && segment !== "..",
    )
  );
}

/** A requested return path, or the catalog when it is missing or not a valid return path. */
export function returnPathOrDefault(value: unknown): string {
  return isReturnPath(value) ? value : defaultReturnPath;
}
