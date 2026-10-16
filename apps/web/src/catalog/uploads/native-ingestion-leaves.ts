/** Bounded native byte/text leaves; feature intent, authority and protocol policies stay separate. */
export const nativeIngestionMaximumBytes = 4_194_304;

export type NativeIngestionSourceErrorKind =
  | "invalidInput"
  | "emptyNative"
  | "nativeLimit"
  | "fileReadUnavailable"
  | "hashUnavailable"
  | "canceled";

export class NativeIngestionSourceError extends Error {
  constructor(readonly kind: NativeIngestionSourceErrorKind) {
    super("The native source could not be prepared.");
    this.name = "NativeIngestionSourceError";
  }
}

/** Internal intent-construction material, never a view or diagnostic DTO. */
export interface NativeIngestionMaterial {
  readonly body: Blob;
  readonly digest: string;
  readonly byteCount: number;
}
const sources = new WeakMap<NativeIngestionSource, NativeIngestionMaterial>();

/** One owned byte snapshot with no public body or digest getter. */
export class NativeIngestionSource {
  private readonly sourceBrand: undefined = undefined;
  private constructor(facts: NativeIngestionMaterial) {
    sources.set(this, facts);
    Object.freeze(this);
  }
  static async capture(
    file: Blob,
    signal?: AbortSignal,
  ): Promise<NativeIngestionSource> {
    requireNotCanceled(signal);
    if (!(file instanceof Blob) || !Number.isSafeInteger(file.size))
      throw new NativeIngestionSourceError("invalidInput");
    requireNativeSize(file.size);
    const byteCount = file.size;
    if (typeof globalThis.crypto?.subtle?.digest !== "function")
      throw new NativeIngestionSourceError("hashUnavailable");
    let owned: ArrayBuffer;
    try {
      const bytes = await file.arrayBuffer();
      requireNotCanceled(signal);
      if (
        !(bytes instanceof ArrayBuffer) ||
        bytes.byteLength !== byteCount ||
        file.size !== byteCount
      )
        throw new NativeIngestionSourceError("fileReadUnavailable");
      requireNativeSize(bytes.byteLength);
      owned = bytes.slice(0);
    } catch (error: unknown) {
      if (error instanceof NativeIngestionSourceError) throw error;
      requireNotCanceled(signal);
      throw new NativeIngestionSourceError("fileReadUnavailable");
    }
    const body = new Blob([owned], { type: "application/octet-stream" });
    let digestBytes: ArrayBuffer;
    try {
      digestBytes = await globalThis.crypto.subtle.digest("SHA-256", owned);
    } catch {
      requireNotCanceled(signal);
      throw new NativeIngestionSourceError("hashUnavailable");
    }
    requireNotCanceled(signal);
    if (digestBytes.byteLength !== 32 || body.size !== byteCount)
      throw new NativeIngestionSourceError("hashUnavailable");
    const digest = Array.from(new Uint8Array(digestBytes), (value) =>
      value.toString(16).padStart(2, "0"),
    ).join("");
    return new NativeIngestionSource(
      Object.freeze({ body, digest, byteCount }),
    );
  }
  get byteCount(): number {
    if (this.sourceBrand !== undefined)
      throw new NativeIngestionSourceError("invalidInput");
    return sourceFacts(this).byteCount;
  }
  toString(): "NativeIngestionSource" {
    return "NativeIngestionSource";
  }
  toJSON(): "NativeIngestionSource" {
    return "NativeIngestionSource";
  }
}

/** @internal Invoke synchronously once; retain material only in a feature-private intent. */
export function withNativeIngestionSource<T>(
  source: NativeIngestionSource,
  use: (material: NativeIngestionMaterial) => T,
): T {
  return use(sourceFacts(source));
}

export interface NativeIngestionTextFacts {
  readonly scalars: number;
  readonly bytes: number;
  readonly error: "invalidUnicode" | "nul" | null;
}
export type NativeIngestionFilenameError =
  "required" | "invalidUnicode" | "nul" | "unsafeFilename" | "byteLimit";
export type NativeIngestionWireTargetError = "queryLimit" | "requestLineLimit";

export function encodeNativeIngestionQueryValue(value: string): string {
  return encodeURIComponent(value).replace(
    /[!'()*]/g,
    (character) => "%" + character.charCodeAt(0).toString(16).toUpperCase(),
  );
}
export function nativeIngestionFilenameError(
  value: string,
): NativeIngestionFilenameError | null {
  if (value.length === 0) return "required";
  const facts = inspectNativeIngestionText(value);
  return (
    facts.error ??
    (value === "." ||
    value === ".." ||
    /[/\\\u0000-\u001f\u007f-\u009f]/.test(value)
      ? "unsafeFilename"
      : facts.bytes > 255
        ? "byteLimit"
        : null)
  );
}
export function inspectNativeIngestionText(
  value: string,
): NativeIngestionTextFacts {
  let scalars = 0,
    bytes = 0;
  for (let index = 0; index < value.length; ++index) {
    const code = value.charCodeAt(index);
    if (code === 0) return { scalars, bytes, error: "nul" };
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++index);
      if (!(low >= 0xdc00 && low <= 0xdfff))
        return { scalars, bytes, error: "invalidUnicode" };
      bytes += 4;
    } else if (code >= 0xdc00 && code <= 0xdfff)
      return { scalars, bytes, error: "invalidUnicode" };
    else bytes += code <= 0x7f ? 1 : code <= 0x7ff ? 2 : 3;
    ++scalars;
  }
  return { scalars, bytes, error: null };
}

/** Call only with a canonical ASCII path and a strictly admitted once-encoded ASCII query. */
export function nativeIngestionWireTargetError(
  path: string,
  query: string,
): NativeIngestionWireTargetError | null {
  if (query.length > 65536) return "queryLimit";
  return ("POST " + path + query + " HTTP/1.1\r\n").length > 65536
    ? "requestLineLimit"
    : null;
}

function sourceFacts(source: NativeIngestionSource): NativeIngestionMaterial {
  const facts = sources.get(source);
  if (!facts) throw new NativeIngestionSourceError("invalidInput");
  return facts;
}
function requireNotCanceled(signal?: AbortSignal): void {
  if (signal?.aborted) throw new NativeIngestionSourceError("canceled");
}
function requireNativeSize(size: number): void {
  if (size < 1) throw new NativeIngestionSourceError("emptyNative");
  if (size > nativeIngestionMaximumBytes)
    throw new NativeIngestionSourceError("nativeLimit");
}
