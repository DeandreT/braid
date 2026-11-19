// Synthetic driver for the capture argv contract. No runtime-selected modules or libraries.
import { createHash } from "node:crypto";
import {
  openSync,
  closeSync,
  fstatSync,
  readSync,
  constants,
  lstatSync,
  writeFileSync,
} from "node:fs";
import { isAbsolute, join } from "node:path";

const abi = new URL("./mock.abi", import.meta.url);
const digest =
  "3f6d94c059b776721441ba4e623ca3ab2a7c6a75c1cd22044be8374885d07d3c";
export const fits = (n) =>
  Number.isSafeInteger(n) && n >= 0 && n <= 2 ** 26 - 1;

export function admit(fd) {
  if (!fits(fd)) throw Error("descriptorRange");
  const stat = fstatSync(fd);
  const bytes = Buffer.alloc(31);
  if (
    !stat.isFile() ||
    stat.size !== bytes.length ||
    readSync(fd, bytes, 0, bytes.length, 0) !== bytes.length ||
    createHash("sha256").update(bytes).digest("hex") !== digest
  )
    throw Error("mockArtifactMismatch");
  return digest;
}

// Each invocation is a single-use driver. One query, one slot; never schedule a replacement.
export function createSupervisor() {
  let used = false;
  return async (options) => {
    if (used) throw Error("slotUsed");
    used = true;
    return query(options);
  };
}

async function query({ signal, timeout = 2000, hold = false } = {}) {
  if (
    signal?.aborted ||
    !Number.isInteger(timeout) ||
    timeout < 1 ||
    timeout > 2000
  )
    throw Error("notAdmitted");
  const end = performance.now() + timeout;
  const worker = new Worker(new URL("./worker.mjs", import.meta.url).href);
  let timer, cancel;
  try {
    return await new Promise((resolve, reject) => {
      cancel = () => reject(Error("cancelled"));
      signal?.addEventListener("abort", cancel, { once: true });
      timer = setTimeout(
        () => reject(Error("deadline")),
        Math.max(0, end - performance.now()),
      );
      worker.onerror = () => reject(Error("workerFailed"));
      worker.onmessage = ({ data }) => {
        if (signal?.aborted || performance.now() >= end)
          reject(Error("expired"));
        else if (
          data instanceof Uint8Array &&
          data.length === 4 &&
          new DataView(data.buffer, data.byteOffset, 4).getUint32(0, true) ===
            42
        )
          resolve(42);
        else reject(Error("invalidReply"));
      };
      worker.postMessage({ bytes: Uint8Array.of(41, 0, 0, 0), hold });
    });
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", cancel);
    worker.terminate();
  }
}

if (import.meta.main) {
  const [option, output, ...extra] = process.argv.slice(2);
  if (
    process.getuid() === 0 ||
    process.env.BRAID_MOCK_CAPTURE !== "1" ||
    option !== "--output" ||
    !output ||
    !isAbsolute(output) ||
    extra.length
  )
    throw Error("fixtureOnly");
  const dir = lstatSync(output);
  if (
    !dir.isDirectory() ||
    dir.uid !== process.getuid() ||
    (dir.mode & 0o777) !== 0o700
  )
    throw Error("privateOutputRequired");
  const fd = openSync(abi, constants.O_RDONLY | constants.O_NOFOLLOW);
  let identity;
  try {
    identity = admit(fd);
  } finally {
    closeSync(fd);
  }
  const queryOnce = createSupervisor();
  const result = await queryOnce();
  writeFileSync(
    join(output, "bun-mock.json"),
    JSON.stringify({
      kind: "synthetic-only",
      nativeLoaded: false,
      identity,
      result,
    }),
    { flag: "wx", mode: 0o600 },
  );
}
