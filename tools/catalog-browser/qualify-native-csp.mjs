// This entry point is intentionally not used by the ordinary catalog suite.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  readdirSync,
  readSync,
} from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const schema = "braid.catalog-native-csp-pair.v1";
const driverRequire = "./index.js";
// The pinned Playwright Core package declares its entry through conditional
// exports, not through main. Condition order is part of Node's resolution.
const driverRootExports = Object.freeze({
  types: "./index.d.ts",
  import: "./index.mjs",
  require: driverRequire,
  default: driverRequire,
});
const driverSubpathExports = Object.freeze({
  "./package.json": "./package.json",
  "./lib/bootstrap": "./lib/bootstrap.js",
  "./lib/coreBundle": "./lib/coreBundle.js",
  "./lib/utilsBundle": "./lib/utilsBundle.js",
  "./lib/tools/cli-client/program": "./lib/tools/cli-client/program.js",
});
const driverPath = (entry) => "driver/" + entry.slice(2);
const requiredFiles = Object.freeze([
  "chrome/chrome",
  ...new Set(
    [
      ...Object.values(driverRootExports),
      ...Object.values(driverSubpathExports),
    ].map(driverPath),
  ),
]);
const digest = (value) =>
  typeof value === "string" && value.length === 64 && /^[0-9a-f]+$/.test(value);
const refusal = (reason) => new Error(`catalog native CSP: ${reason}`);

export function requestFrom(args) {
  if (args[0] !== "--qualify")
    throw refusal("disabled; explicit --qualify required");
  const allowed = new Set(["--pair", "--pair-sha256", "--origin"]);
  const options = new Map();
  for (let i = 1; i < args.length; i += 2) {
    const key = args[i];
    const value = args[i + 1];
    if (
      !allowed.has(key) ||
      options.has(key) ||
      !value ||
      value.startsWith("--")
    )
      throw refusal("unexpected, duplicate or incomplete launch option");
    options.set(key, value);
  }
  const record = options.get("--pair");
  const pin = options.get("--pair-sha256");
  const origin = options.get("--origin");
  if (
    !record ||
    !isAbsolute(record) ||
    resolve(record) !== record ||
    !digest(pin)
  )
    throw refusal(
      "canonical absolute pair path and lowercase SHA-256 required",
    );
  if (!isLoopbackOrigin(origin))
    throw refusal("exact nonprivileged 127.0.0.1 HTTP origin required");
  return Object.freeze({ record, pin, origin });
}

export function isLoopbackOrigin(value) {
  if (
    typeof value !== "string" ||
    !/^http:\/\/127\.0\.0\.1:[1-9][0-9]{3,4}$/.test(value)
  )
    return false;
  try {
    const url = new URL(value);
    return url.origin === value && Number(url.port) >= 1024;
  } catch {
    return false;
  }
}

const unescapeMount = (value) =>
  value.replace(/\\([0-7]{3})/g, (_, digits) =>
    String.fromCharCode(Number.parseInt(digits, 8)),
  );
const contains = (parent, child) =>
  child === parent || child.startsWith(parent === "/" ? "/" : parent + "/");

export function snapshotMount(mountinfo, root) {
  const entries = mountinfo
    .trim()
    .split("\n")
    .map((line) => {
      const [left, right] = line.split(" - ");
      const [id, , , , encoded, options] = left.split(" ");
      const [type, , superOptions] = right.split(" ");
      return { id, path: unescapeMount(encoded), options, type, superOptions };
    });
  const found = entries
    .filter(({ path }) => contains(path, root))
    .sort((a, b) => b.path.length - a.path.length);
  // Do not silently accept an overmount or a writable submount inside the pair.
  const entry = found[0];
  if (
    !entry ||
    entry.path === "/" ||
    found[1]?.path === entry.path ||
    entry.type !== "ext4" ||
    !entry.options.split(",").includes("ro") ||
    !entry.superOptions.split(",").includes("ro") ||
    entries.some(({ path }) => path !== entry.path && contains(root, path))
  )
    throw refusal("pair is not on a dedicated read-only ext4 snapshot");
  return entry.id;
}

function protectedOrdinary(path, directory) {
  const st = lstatSync(path);
  if (
    st.uid !== 0 ||
    (st.mode & 0o022) !== 0 ||
    !(directory ? st.isDirectory() : st.isFile())
  )
    throw refusal("unprotected or linked pair input");
  return st;
}

function verifyDirectory(path) {
  for (let current = path; ; current = dirname(current)) {
    protectedOrdinary(current, true);
    if (current === "/") return;
  }
}

function openInput(path, max) {
  const fd = openSync(
    path,
    constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
  );
  try {
    const st = fstatSync(fd);
    if (
      !st.isFile() ||
      st.uid !== 0 ||
      (st.mode & 0o022) !== 0 ||
      st.size > max
    )
      throw refusal("unprotected pair file or unexpected size");
    return fd;
  } catch (error) {
    closeSync(fd);
    throw error;
  }
}

function inputBytes(path, max) {
  const fd = openInput(path, max);
  try {
    const bytes = readFileSync(fd);
    if (bytes.length > max) throw refusal("pair input exceeds limit");
    return bytes;
  } finally {
    closeSync(fd);
  }
}

function inputDigest(path) {
  const fd = openInput(path, 1024 ** 3);
  try {
    const hash = createHash("sha256");
    const block = Buffer.alloc(1024 * 1024);
    for (let n; (n = readSync(fd, block)) > 0;)
      hash.update(block.subarray(0, n));
    return hash.digest("hex");
  } finally {
    closeSync(fd);
  }
}

const exactKeys = (object, keys) =>
  object !== null &&
  typeof object === "object" &&
  !Array.isArray(object) &&
  Object.keys(object).length === keys.length &&
  keys.every((key) => Object.hasOwn(object, key));

export function driverEntryFrom(driverPackage, version) {
  const exports = driverPackage?.exports;
  const subpaths = Object.entries(driverSubpathExports);
  const root = exports?.["."];
  const conditions = Object.entries(driverRootExports);
  if (
    driverPackage?.name !== "playwright-core" ||
    driverPackage.version !== version ||
    Object.hasOwn(driverPackage, "main") ||
    Object.hasOwn(driverPackage, "type") ||
    !exactKeys(exports, [".", ...Object.keys(driverSubpathExports)]) ||
    subpaths.some(([name, target]) => exports[name] !== target) ||
    !exactKeys(root, Object.keys(driverRootExports)) ||
    Object.keys(root).some(
      (name, index) =>
        name !== conditions[index][0] || root[name] !== conditions[index][1],
    )
  )
    throw refusal("paired driver package entry layout or version differs");
  // Keep selecting the pinned CJS file explicitly; don't resolve an installed
  // package by name or switch the runner's driver instance to the ESM entry.
  return driverPath(driverRequire);
}

export function pairFrom(bytes, pin, inventory, readDigest) {
  if (!digest(pin) || createHash("sha256").update(bytes).digest("hex") !== pin)
    throw refusal("pair record digest mismatch");
  const record = JSON.parse(
    new TextDecoder("utf-8", { fatal: true }).decode(bytes),
  );
  if (
    !exactKeys(record, [
      "schemaVersion",
      "chromeVersion",
      "driverVersion",
      "files",
    ]) ||
    record.schemaVersion !== schema ||
    record.chromeVersion !== "157.0.8090.0" ||
    record.driverVersion !== "1.64.0" ||
    !exactKeys(record.files, inventory) ||
    !requiredFiles.every((part) => inventory.includes(part))
  )
    throw refusal("unsupported pair record");
  for (const path of inventory) {
    if (
      !/^(?:chrome|driver)\/[A-Za-z0-9_.@-]+(?:\/[A-Za-z0-9_.@-]+)*$/.test(
        path,
      ) ||
      path.split("/").some((part) => part === "." || part === "..") ||
      !digest(record.files[path]) ||
      readDigest(path) !== record.files[path]
    )
      throw refusal(`pair artifact digest mismatch: ${path}`);
  }
  return Object.freeze({ ...record, files: Object.freeze(record.files) });
}

function inventoryPair(root) {
  const files = [];
  let entries = 0;
  let bytes = 0;
  function visit(part, level) {
    const dir = join(root, part);
    protectedOrdinary(dir, true);
    for (const item of readdirSync(dir, { withFileTypes: true })) {
      if (++entries > 4096 || level > 24)
        throw refusal("pair inventory exceeds limit");
      const child = part + "/" + item.name;
      if (item.isDirectory()) visit(child, level + 1);
      else {
        const { size } = protectedOrdinary(join(root, child), false);
        if ((bytes += size) > 2 * 1024 ** 3)
          throw refusal("pair inventory exceeds limit");
        files.push(child);
      }
    }
  }
  for (const part of ["chrome", "driver"]) visit(part, 0);
  return files.sort();
}

export function assertLoopbackOnly(interfaces) {
  const [loopback] = Array.isArray(interfaces) ? interfaces : [];
  if (
    interfaces?.length !== 1 ||
    loopback?.ifname !== "lo" ||
    !Array.isArray(loopback.flags) ||
    !loopback.flags.includes("LOOPBACK") ||
    !loopback.flags.includes("UP")
  )
    throw refusal("isolated network with reachable loopback required");
}

function inspectInput(request) {
  if (
    process.platform !== "linux" ||
    process.arch !== "x64" ||
    process.getuid?.() === 0
  )
    throw refusal(
      "unsupported host; use unprivileged Linux x64 in the offline guest",
    );
  assertLoopbackOnly(
    JSON.parse(
      execFileSync("/usr/bin/ip", ["-j", "link", "show"], {
        encoding: "utf8",
        env: { PATH: "/usr/bin", LC_ALL: "C" },
        stdio: ["ignore", "pipe", "ignore"],
        timeout: 2000,
        maxBuffer: 65536,
      }),
    ),
  );
  const root = dirname(request.record);
  const mountinfo = readFileSync("/proc/self/mountinfo", "utf8");
  snapshotMount(mountinfo, root);
  verifyDirectory(root);
  const inventory = inventoryPair(root);
  const pair = pairFrom(
    inputBytes(request.record, 1024 * 1024),
    request.pin,
    inventory,
    (part) => {
      const path = join(root, part);
      verifyDirectory(dirname(path));
      const st = protectedOrdinary(path, false);
      if (part === "chrome/chrome" && (st.mode & 0o111) === 0)
        throw refusal("pair browser is not executable");
      return inputDigest(path);
    },
  );
  const driverPackage = JSON.parse(
    inputBytes(join(root, "driver/package.json"), 65536),
  );
  const driverEntry = driverEntryFrom(driverPackage, pair.driverVersion);
  return Object.freeze({
    chrome: join(root, "chrome/chrome"),
    driverEntry: join(root, driverEntry),
    pairDigest: request.pin,
    origin: request.origin,
  });
}

export function sandboxLaunchOptions(plan, home, temporary) {
  if (
    ![home, temporary].every(
      (path) => typeof path === "string" && isAbsolute(path),
    ) ||
    home === temporary
  )
    throw refusal("distinct private launch directories are required");
  return {
    executablePath: plan.chrome,
    chromiumSandbox: true,
    timeout: 10_000,
    env: { HOME: home, TMPDIR: temporary, PATH: "/usr/bin" },
  };
}

/** Future adapter must pass the module and Chromium object actually used by the test runner. */
export function assertDriverInstance(
  plan,
  admittedModule,
  runnerEntry,
  runnerModule,
  launchedBy,
) {
  if (
    runnerEntry !== plan.driverEntry ||
    runnerModule !== admittedModule ||
    !admittedModule?.chromium ||
    launchedBy !== admittedModule.chromium
  )
    throw refusal("test runner did not use the admitted driver instance");
}

export function requireNativeCutoff() {
  // No packaged transport has been reviewed. Do not load a caller-supplied
  // adapter, call experimental document CDP commands, or start any child.
  throw refusal(
    "unsupported: native generation and browser-wide cutoff adapter is unavailable",
  );
}

export function main(args, inspect = inspectInput) {
  const request = requestFrom(args);
  inspect(request);
  requireNativeCutoff();
}

if (
  process.argv[1] &&
  pathToFileURL(resolve(process.argv[1])).href === import.meta.url
) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    // Never print package contents, OS path diagnostics or environment data.
    console.error(
      error instanceof Error && error.message.startsWith("catalog native CSP: ")
        ? error.message
        : "catalog native CSP: launch inputs could not be verified",
    );
    process.exitCode = 1;
  }
}
