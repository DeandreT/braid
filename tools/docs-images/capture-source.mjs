import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { resolve } from "node:path";

const sourcePaths = [
  "apps/web/src",
  "apps/web/public",
  "apps/web/tests",
  "apps/web/angular.json",
  "apps/web/index.html",
  "apps/web/package.json",
  "apps/web/playwright.docs-screenshots.config.ts",
  "apps/web/tsconfig*.json",
  "docs/user-guide",
  "package.json",
  "package-lock.json",
  ".node-version",
  ".npmrc",
  "tools/docs-images/server.mjs",
  "tools/docs-images/server.test.mjs",
  "tools/docs-images/session.mjs",
  "tools/docs-images/teardown.mjs",
  "tools/docs-images/session.test.mjs",
  "tools/docs-images/lifecycle.fixture.mjs",
  "tools/docs-images/check.test.mjs",
  "tools/docs-images/capture-source.mjs",
  "tools/docs-images/capture-source.d.mts",
  "tools/docs-images/capture-source.test.mjs",
];
const git = (root, ...args) =>
  execFileSync("git", args, {
    cwd: root,
    encoding: "utf8",
    timeout: 5000,
    maxBuffer: 2 * 1024 * 1024,
  }).trim();
const records = (text) => text.split("\0").filter(Boolean);

/** Include unstaged ordinary helper sources without staging the user's files. */
export async function sourceFingerprint(root) {
  const files = [
    ...new Set(
      records(
        git(
          root,
          "ls-files",
          "--cached",
          "--others",
          "--exclude-standard",
          "-z",
          "--",
          ...sourcePaths,
        ),
      ),
    ),
  ].sort();
  const source = createHash("sha256");
  for (const file of files) {
    const path = resolve(root, file);
    const info = await lstat(path);
    if (!info.isFile() || info.isSymbolicLink())
      throw new Error("Capture sources must be ordinary files.");
    source
      .update(file + "\0")
      .update(await readFile(path))
      .update("\0");
  }
  return { files, sourceSha256: source.digest("hex") };
}

export async function captureProvenance(root) {
  const { sourceSha256 } = await sourceFingerprint(root);
  return {
    sourceCommit: git(root, "rev-parse", "HEAD"),
    sourceChanges: [
      ...new Set([
        ...records(
          git(root, "diff", "HEAD", "--name-only", "-z", "--", ...sourcePaths),
        ),
        ...records(
          git(
            root,
            "ls-files",
            "--others",
            "--exclude-standard",
            "-z",
            "--",
            ...sourcePaths,
          ),
        ),
      ]),
    ].sort(),
    sourceSha256,
    captureSpecSha256: createHash("sha256")
      .update(
        await readFile(
          resolve(root, "apps/web/tests/docs-screenshots.spec.ts"),
        ),
      )
      .digest("hex"),
  };
}
