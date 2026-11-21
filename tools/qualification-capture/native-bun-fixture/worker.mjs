// Executed as admitted --eval bytes, prefixed only by the fixed `fixture` JSON.
import { createHash } from "node:crypto";
import { fstatSync, readlinkSync, readSync, writeSync } from "node:fs";

const libraryFd = 3;
const libraryPath = "/proc/self/fd/3";
const tick = () => new Promise((resolve) => setTimeout(resolve, 5));
// Fixed failure exit codes shared with supervisor.py. No raw error text.
const FAILURE = {
  identity: 40,
  stdio: 41,
  artifactDescriptor: 42,
  artifactBytes: 43,
  admissionFrame: 44,
  loadPermit: 45,
  ffiImport: 46,
  libraryOpen: 47,
  loadFrame: 48,
  queryPermit: 49,
  abi: 50,
  query: 51,
  resultFrame: 52,
};
let failure = FAILURE.identity;

async function permit(expected) {
  const until = performance.now() + 750;
  const byte = Buffer.alloc(1);
  while (performance.now() < until) {
    try {
      // Slot 0 is nonblocking. No read-ahead or outstanding reader behind C.
      return (
        readSync(0, byte, 0, 1, null) === 1 &&
        byte[0] === expected &&
        performance.now() < until
      );
    } catch (error) {
      if (error?.code !== "EAGAIN" && error?.code !== "EINTR") throw error;
      await tick();
    }
  }
  return false;
}

function send(bytes) {
  // These frames are <=5 bytes. A would-block/short write is never success.
  if (writeSync(1, bytes) !== bytes.length) throw new Error("shortFrame");
}

function verifyLibrary() {
  failure = FAILURE.artifactDescriptor;
  const stat = fstatSync(libraryFd);
  if (
    !stat.isFile() ||
    stat.size !== fixture.size ||
    fixture.size < 1 ||
    fixture.size > 262144 ||
    readlinkSync(libraryPath) !== "/memfd:braid-fixture-v1 (deleted)"
  )
    throw new Error("libraryDescriptor");
  failure = FAILURE.artifactBytes;
  const bytes = Buffer.alloc(stat.size);
  let offset = 0;
  while (offset < bytes.length) {
    const n = readSync(libraryFd, bytes, offset, bytes.length - offset, offset);
    if (n <= 0) throw new Error("libraryBytes");
    offset += n;
  }
  if (createHash("sha256").update(bytes).digest("hex") !== fixture.sha256)
    throw new Error("libraryDigest");
}

async function main() {
  if (
    process.getuid() === 0 ||
    process.platform !== "linux" ||
    process.arch !== "x64" ||
    Bun.version !== "1.4.5" ||
    ![0, 1].includes(fixture.mode)
  )
    return failure;
  failure = FAILURE.stdio;
  if (!fstatSync(0).isSocket() || !fstatSync(1).isSocket()) return failure;
  verifyLibrary();
  failure = FAILURE.admissionFrame;
  send(Buffer.from("A\n"));
  failure = FAILURE.loadPermit;
  if (!(await permit(0x47))) return failure; // G: pre-load maps/process admission.
  failure = FAILURE.ffiImport;
  const { dlopen, FFIType } = await import("bun:ffi");
  failure = FAILURE.libraryOpen;
  const library = dlopen(libraryPath, {
    braid_fixture_abi_v1: { args: [], returns: FFIType.uint32_t },
    braid_fixture_query_v1: {
      args: [FFIType.uint32_t, FFIType.uint32_t],
      returns: FFIType.int32_t,
    },
  });
  failure = FAILURE.loadFrame;
  send(Buffer.from("L\n"));
  failure = FAILURE.queryPermit;
  if (!(await permit(0x51))) return failure; // Q: exact memfd mapping admitted.
  failure = FAILURE.abi;
  if (library.symbols.braid_fixture_abi_v1() !== 0x42523401) return failure;
  failure = FAILURE.query;
  const result = library.symbols.braid_fixture_query_v1(fixture.mode, 41);
  if (![42, -1, -2, -3, -4, -5, -6].includes(result)) return failure;
  failure = FAILURE.resultFrame;
  const frame = Buffer.alloc(5);
  frame[0] = 0x52;
  frame.writeInt32LE(result, 1);
  send(frame);
  // The mapping and inherited child-only handles live through task exit.
  // Python keeps its own handles until reaping; no concurrent dlclose.
  return result === 42 ? 0 : result === -3 ? 3 : 4;
}

try {
  process.exit(await main());
} catch {
  process.exit(failure);
}
