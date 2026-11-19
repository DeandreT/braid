// Explicit future-run admission only. No child process, resource inventory, database,
// browser, certificate import or provider request runs here. Never prints the record.
import {
  constants,
  closeSync,
  existsSync,
  fstatSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  realpathSync,
  readdirSync,
  writeFileSync,
} from "node:fs";
import { createHash } from "node:crypto";
import {
  delimiter,
  dirname,
  isAbsolute,
  join,
  relative,
  resolve,
} from "node:path";

const demand = (value) => {
  if (!value) throw new Error("Owned HTTPS admission failed.");
};
const hash = (bytes) => createHash("sha256").update(bytes).digest("hex");
const ordinary = (path) => {
  const actual = realpathSync(path),
    stat = lstatSync(actual);
  demand(isAbsolute(actual) && stat.isFile() && !stat.isSymbolicLink());
  return actual;
};
const executable = (name) => {
  for (const directory of (process.env.PATH ?? "").split(delimiter)) {
    if (!isAbsolute(directory)) continue;
    const path = join(directory, name);
    if (existsSync(path)) {
      const actual = ordinary(path);
      demand((lstatSync(actual).mode & 0o111) !== 0);
      return actual;
    }
  }
  throw new Error("Owned HTTPS admission failed.");
};
try {
  demand(process.argv.length === 3 && process.argv[2] === "--approved");
  const sourceRoot = realpathSync(process.cwd());
  const probeAssembly = ordinary(
    process.env.BRAID_HOST_PRIVACY_PROBE_PATH ?? "",
  );
  const serverAssembly = ordinary(
    process.env.BRAID_HOST_TEST_SERVER_PATH ?? "",
  );
  const chromium = ordinary(process.env.BRAID_CHROMIUM_EXECUTABLE ?? "");
  demand(process.versions.bun === "1.4.5");
  const bun = ordinary(process.execPath),
    certutil = executable("certutil");
  // Required existing Xvfb lane tools, without executing or silently installing them.
  executable("xvfb-run");
  executable("Xvfb");
  executable("xauth");
  let root = process.env.BRAID_AUTH_BROWSER_ADMISSION_ROOT;
  if (root) {
    demand(isAbsolute(root) && realpathSync(root) === root);
  } else {
    demand(
      process.env.CI === "true" &&
        process.env.RUNNER_TEMP &&
        isAbsolute(process.env.RUNNER_TEMP),
    );
    root = mkdtempSync(join(process.env.RUNNER_TEMP, "braid-ah-"));
    // mkdtemp creates 0700; do not repair an existing selected root.
  }
  const directory = lstatSync(root);
  demand(
    directory.isDirectory() &&
      !directory.isSymbolicLink() &&
      (directory.mode & 0o777) === 0o700 &&
      directory.uid === process.geteuid(),
  );
  demand((root + "/c-12345678/p.sock").length < 100);
  const web = join(dirname(serverAssembly), "wwwroot"),
    assets = Object.create(null);
  let total = 0;
  const walk = (path) => {
    for (const entry of readdirSync(path, { withFileTypes: true })) {
      demand(!entry.isSymbolicLink());
      const child = join(path, entry.name);
      if (entry.isDirectory()) walk(child);
      else {
        const actual = ordinary(child),
          bytes = readFileSync(actual),
          key = relative(web, actual).replaceAll("\\", "/");
        demand(
          !key.startsWith("../") &&
            !isAbsolute(key) &&
            bytes.length <= 16777216,
        );
        total += bytes.length;
        demand(total <= 134217728 && Object.keys(assets).length < 256);
        assets[key] = hash(bytes);
      }
    }
  };
  walk(web);
  demand(assets["index.html"]);
  const record = {
    version: 1,
    approved: true,
    root,
    sourceRoot,
    probeAssembly,
    probeSha256: hash(readFileSync(probeAssembly)),
    serverAssembly,
    serverSha256: hash(readFileSync(serverAssembly)),
    assets,
    bun,
    bunSha256: hash(readFileSync(bun)),
    chromium,
    chromiumSha256: hash(readFileSync(chromium)),
    certutil,
    certutilSha256: hash(readFileSync(certutil)),
  };
  const bytes = Buffer.from(JSON.stringify(record));
  demand(bytes.length <= 65536);
  const path = join(root, "admission.json"),
    fd = openSync(
      path,
      constants.O_WRONLY |
        constants.O_CREAT |
        constants.O_EXCL |
        constants.O_NOFOLLOW,
      0o600,
    );
  try {
    demand((fstatSync(fd).mode & 0o777) === 0o600);
    writeFileSync(fd, bytes);
  } finally {
    bytes.fill(0);
    closeSync(fd);
  }
  if (process.env.GITHUB_ENV) {
    // Only nonsecret paths enter the workflow environment; the JSON is never logged.
    demand(!root.includes("\n") && !root.includes("\r"));
    writeFileSync(
      process.env.GITHUB_ENV,
      `BRAID_AUTH_BROWSER_ADMISSION_ROOT=${root}\nBRAID_AUTH_BROWSER_ADMISSION_FILE=${path}\n`,
      { flag: "a" },
    );
  } else demand(process.env.BRAID_AUTH_BROWSER_ADMISSION_FILE === path);
  process.stdout.write("BraidAuthBrowserAdmissionPrepared\n");
} catch {
  process.stderr.write("BraidAuthBrowserAdmissionFailed\n");
  process.exitCode = 1;
}
