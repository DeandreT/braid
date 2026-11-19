import { sha256 } from "@noble/hashes/sha2.js";
import { bytesToHex } from "@noble/hashes/utils.js";
import { HttpErrorResponse } from "@angular/common/http";
import { PreviewError, type PreviewOutput } from "./preview-types";

/** The Blob is private staging. No URL is created until EOF, digest and authority agree. */
export async function verifiedDownload(
  path: string,
  output: PreviewOutput,
  signal: AbortSignal,
  current: () => void,
  progress: (received: number) => void,
): Promise<Blob> {
  const check = () => {
    if (signal.aborted) throw new PreviewError("unavailable");
    current();
  };
  check();
  const response = await fetch(path, {
    signal,
    credentials: "same-origin",
    mode: "same-origin",
    redirect: "error",
    cache: "no-store",
  });
  check();
  if (response.status !== 200)
    throw new HttpErrorResponse({ status: response.status });
  const declared = response.headers.get("Content-Length");
  if (
    response.headers.get("Content-Type")?.split(";")[0]?.trim() !==
      output.mediaType ||
    (declared !== null &&
      !response.headers.has("Content-Encoding") &&
      declared !== String(output.byteCount))
  )
    throw new PreviewError("integrity");
  if (
    !response.body?.getReader ||
    typeof TransformStream === "undefined" ||
    typeof response.body.pipeThrough !== "function"
  ) {
    // Older Fetch implementations: only accept an explicitly sized, unencoded small attachment.
    if (
      output.byteCount > 1024 * 1024 ||
      declared !== String(output.byteCount) ||
      response.headers.has("Content-Encoding")
    )
      throw new PreviewError("incompatible");
    const bytes = new Uint8Array(await response.arrayBuffer());
    check();
    if (
      bytes.length !== output.byteCount ||
      bytesToHex(sha256(bytes)) !== output.sha256
    )
      throw new PreviewError("integrity");
    return new Blob([bytes], { type: output.mediaType });
  }
  let received = 0;
  // Some Blob consumers replace a stream's rejection with a generic TypeError.
  let integrityFailure = false;
  const rejectBytes = (): never => {
    integrityFailure = true;
    throw new PreviewError("integrity");
  };
  const hash = sha256.create();
  try {
    const stream = response.body.pipeThrough(
      new TransformStream<Uint8Array, Uint8Array>({
        transform(chunk, sink) {
          check();
          if (chunk.length > output.byteCount - received) rejectBytes();
          hash.update(chunk);
          received += chunk.length;
          progress(received);
          sink.enqueue(chunk);
        },
        flush() {
          check();
          if (
            received !== output.byteCount ||
            bytesToHex(hash.digest()) !== output.sha256
          )
            rejectBytes();
        },
      }),
      { signal },
    );
    const verified = await new Response(stream, {
      headers: { "Content-Type": output.mediaType },
    }).blob();
    check();
    return verified;
  } catch (error) {
    if (integrityFailure) throw new PreviewError("integrity");
    throw error;
  } finally {
    hash.destroy();
  }
}
