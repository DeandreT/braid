/** Wire bounds shared with BrowserReturnPath and contracts/browser-return-paths.json. */
export const defaultReturnPath = "/catalog";
export const maximumReturnPathLength = 7 * 1024;
export const maximumApplicationPathLength = 1024;
const sections = ["catalog", "help", "administration", "chat", "notifications"];
const encoder = new TextEncoder();
const wire = /^[A-Za-z0-9\-._~!$&'()*+,;=:@/?%#]*$/;
const pathCharacter = /^[A-Za-z0-9\-._~!$&'()*+,;=:@]$/;
const id = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    value,
  ) && value !== "00000000-0000-0000-0000-000000000000";
const token = (value: string, maximum: number) =>
  value.length > 0 && value.length <= maximum && /^[A-Za-z0-9_-]+$/.test(value);
const keys = (values: Map<string, string>, names: string[]) =>
  [...values.keys()].every((key) => names.includes(key));
const trimQuery = (value: string) =>
  value.replace(/^\p{White_Space}+|\p{White_Space}+$/gu, "");

/** Validate without rewriting: only path segments use path-separator rules. Query/fragment data is decoded once. */
export function isReturnPath(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > maximumReturnPathLength ||
    value[0] !== "/" ||
    !wire.test(value)
  )
    return false;
  const fragmentAt = value.indexOf("#");
  let queryAt = value.indexOf("?");
  if (queryAt >= 0 && fragmentAt >= 0 && queryAt > fragmentAt) queryAt = -1;
  const pathEnd = Math.min(
    queryAt < 0 ? value.length : queryAt,
    fragmentAt < 0 ? value.length : fragmentAt,
  );
  const path = value.slice(0, pathEnd);
  if (path.length > maximumApplicationPathLength) return false;
  const raw = path.slice(1).split("/");
  if (!sections.includes(raw[0])) return false;
  const segments: string[] = [];
  for (const part of raw) {
    const decoded = pathSegment(part);
    if (decoded === null) return false;
    segments.push(decoded);
  }
  const values = query(
    queryAt < 0
      ? ""
      : value.slice(queryAt + 1, fragmentAt < 0 ? value.length : fragmentAt),
  );
  if (values === null || !allowedQuery(segments, values)) return false;
  if (fragmentAt < 0) return true;
  const fragment = decode(value.slice(fragmentAt + 1), false);
  if (fragment === null) return false;
  if (segments[0] === "help" && segments.length >= 2)
    return token(fragment, 128);
  if (!revision(segments) || !values.has("projectionDerivationId"))
    return false;
  const prefix = fragment.startsWith("field:")
    ? 6
    : fragment.startsWith("rule:")
      ? 5
      : 0;
  const resource = fragment.slice(prefix);
  return (
    prefix !== 0 &&
    resource.length > 0 &&
    encoder.encode(resource).length <= 2048
  );
}

function allowedQuery(path: string[], values: Map<string, string>): boolean {
  if (path.length === 1 && path[0] === "chat") {
    if (
      !keys(values, [
        "conversationId",
        "projectId",
        "mappingId",
        "revisionId",
      ]) ||
      ![...values.values()].every(id)
    )
      return false;
    if (values.size === 0) return true;
    const scoped = ["projectId", "mappingId", "revisionId"].filter((key) =>
      values.has(key),
    ).length;
    return values.has("conversationId") && (scoped === 0 || scoped === 3);
  }
  if (path.length === 2 && path[0] === "chat" && path[1] === "resources") {
    if (
      !keys(values, [
        "kind",
        "organizationId",
        "projectId",
        "mappingId",
        "revisionId",
        "subjectId",
        "version",
      ]) ||
      !["organizationId", "projectId", "subjectId"].every((key) =>
        id(values.get(key) ?? ""),
      )
    )
      return false;
    const kind = values.get("kind"),
      mappingId = values.get("mappingId"),
      revisionId = values.get("revisionId"),
      version = values.get("version");
    const numbered =
      version !== undefined && /^[1-9][0-9]{0,19}$/.test(version);
    if (kind === "buildEnvironment")
      return mappingId === undefined && revisionId === undefined && numbered;
    if (mappingId === undefined || !id(mappingId)) return false;
    if (kind === "previewRun")
      return (
        revisionId !== undefined && id(revisionId) && version === undefined
      );
    if (kind === "regressionCase") return revisionId === undefined && numbered;
    return (
      ["regressionRun", "buildRun", "buildApproval"].includes(kind ?? "") &&
      revisionId === undefined &&
      version === undefined
    );
  }
  if (path[0] === "chat") return false;
  if (path[0] === "notifications")
    return (
      path.length === 1 &&
      keys(values, ["after"]) &&
      [...values.values()].every(id)
    );
  if (path.length === 2 && path[0] === "catalog" && path[1] === "search") {
    if (!keys(values, ["q", "cursor"])) return false;
    const text = values.get("q");
    if (text !== undefined && encoder.encode(trimQuery(text)).length > 256)
      return false;
    const after = values.get("cursor");
    return (
      after === undefined ||
      (text !== undefined &&
        trimQuery(text).length > 0 &&
        cursor(after, 4096, false))
    );
  }
  if (
    path.length === 7 &&
    path[0] === "catalog" &&
    path[1] === "organizations" &&
    id(path[2]) &&
    path[3] === "projects" &&
    id(path[4]) &&
    path[5] === "creation" &&
    id(path[6])
  )
    return (
      values.size === 1 &&
      keys(values, ["fingerprint"]) &&
      /^[0-9a-f]{64}$/.test(values.get("fingerprint") ?? "")
    );
  if (revision(path))
    return (
      keys(values, ["projectionDerivationId", "validationDerivationId"]) &&
      [...values.values()].every(id)
    );
  if (mapping(path, "comparisons")) {
    if (
      !keys(values, [
        "left",
        "right",
        "leftProjection",
        "leftValidation",
        "rightProjection",
        "rightValidation",
        "cursor",
      ])
    )
      return false;
    for (const [key, value] of values)
      if (key === "cursor" ? !cursor(value, 683, true) : !id(value))
        return false;
    return (
      ((!values.has("leftProjection") && !values.has("leftValidation")) ||
        values.has("left")) &&
      ((!values.has("rightProjection") && !values.has("rightValidation")) ||
        values.has("right"))
    );
  }
  if (pagedCatalog(path))
    return (
      keys(values, ["cursor"]) &&
      (!values.has("cursor") || cursor(values.get("cursor")!, 683, true))
    );
  if (path[0] === "help")
    return (
      keys(values, ["view", "section"]) &&
      [...values.values()].every((value) => token(value, 64))
    );
  if (path.length === 1 && path[0] === "administration")
    return (
      keys(values, ["view"]) &&
      [...values.values()].every((value) => token(value, 64))
    );
  return values.size === 0;
}

function revision(path: string[]): boolean {
  return (
    path.length === 9 &&
    path[0] === "catalog" &&
    path[1] === "organizations" &&
    id(path[2]) &&
    path[3] === "projects" &&
    id(path[4]) &&
    path[5] === "mappings" &&
    id(path[6]) &&
    path[7] === "revisions" &&
    id(path[8])
  );
}
function mapping(path: string[], leaf: string): boolean {
  return (
    path.length === 8 &&
    path[0] === "catalog" &&
    path[1] === "organizations" &&
    id(path[2]) &&
    path[3] === "projects" &&
    id(path[4]) &&
    path[5] === "mappings" &&
    id(path[6]) &&
    path[7] === leaf
  );
}
function pagedCatalog(path: string[]): boolean {
  return (
    path[0] === "catalog" &&
    path[1] === "organizations" &&
    id(path[2] ?? "") &&
    path[3] === "projects" &&
    (path.length === 4 ||
      (id(path[4] ?? "") &&
        (path.length === 5 ||
          (path.length === 7 && path[5] === "mappings" && id(path[6])))))
  );
}
function cursor(value: string, maximum: number, canonical: boolean): boolean {
  if (!token(value, maximum) || (canonical && value.length < 2)) return false;
  if (!canonical) return true;
  const remainder = value.length % 4;
  return (
    remainder === 0 ||
    (remainder === 2 && /[AQgw]$/.test(value)) ||
    (remainder === 3 && /[AEIMQUYcgkosw048]$/.test(value))
  );
}
function pathSegment(raw: string): string | null {
  let value = "";
  for (let index = 0; index < raw.length; index++) {
    let character = raw[index];
    if (character === "%") {
      const hex = raw.slice(index + 1, index + 3);
      if (!/^[0-9a-fA-F]{2}$/.test(hex)) return null;
      character = String.fromCharCode(parseInt(hex, 16));
      if (".%/\\".includes(character)) return null;
      index += 2;
    }
    if (!pathCharacter.test(character)) return null;
    value += character;
  }
  return value.length > 0 && value !== "." && value !== ".." ? value : null;
}
function decode(raw: string, form: boolean): string | null {
  try {
    const value = decodeURIComponent(form ? raw.replace(/\+/g, "%20") : raw);
    return /[\u0000-\u001f\u007f-\u009f]/u.test(value) ? null : value;
  } catch {
    return null;
  }
}

function query(raw: string): Map<string, string> | null {
  const values = new Map<string, string>();
  if (raw.length === 0) return values;
  for (const pair of raw.split("&")) {
    const equals = pair.indexOf("=");
    if (equals < 1 || values.size >= 7) return null;
    const key = pair.slice(0, equals);
    const value = decode(pair.slice(equals + 1), true);
    if (!/^[A-Za-z0-9]+$/.test(key) || value === null || values.has(key))
      return null;
    values.set(key, value);
  }
  return values;
}

export function returnPathOrDefault(value: unknown): string {
  return isReturnPath(value) ? value : defaultReturnPath;
}
