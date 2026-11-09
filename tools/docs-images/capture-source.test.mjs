import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { sourceFingerprint } from "./capture-source.mjs";

test(
  "fingerprints tracked and untracked ordinary capture inputs, excluding ignored output",
  { timeout: 10000 },
  async (t) => {
    const root = await mkdtemp(join(tmpdir(), "weft-docs-source-test-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    execFileSync("git", ["init", "--quiet", root], { timeout: 5000 });
    const write = async (path, value) => {
      await mkdir(join(root, path, ".."), { recursive: true });
      await writeFile(join(root, path), value);
    };
    await write("apps/web/src/tracked.ts", "tracked");
    execFileSync("git", ["-C", root, "add", "apps/web/src/tracked.ts"], {
      timeout: 5000,
    });
    await write("apps/web/src/untracked.ts", "untracked");
    await write("tools/docs-images/server.mjs", "server one");
    await write("tools/docs-images/capture-source.mjs", "fingerprint helper");
    await write("tools/docs-images/session.mjs", "session one");
    await write(".git/info/exclude", "apps/web/src/ignored.ts\n");
    await write("apps/web/src/ignored.ts", "ignored build artifact");
    await write("docs/images/not-a-source.png", "old image");
    const first = await sourceFingerprint(root);
    assert.deepEqual(first.files, [
      "apps/web/src/tracked.ts",
      "apps/web/src/untracked.ts",
      "tools/docs-images/capture-source.mjs",
      "tools/docs-images/server.mjs",
      "tools/docs-images/session.mjs",
    ]);
    assert.equal(first.files.length, new Set(first.files).size);
    await write("tools/docs-images/server.mjs", "server two");
    const second = await sourceFingerprint(root);
    assert.notEqual(second.sourceSha256, first.sourceSha256);
    await write("tools/docs-images/session.mjs", "session two");
    const third = await sourceFingerprint(root);
    assert.notEqual(third.sourceSha256, second.sourceSha256);
    await write("apps/web/src/ignored.ts", "different ignored output");
    assert.equal(
      (await sourceFingerprint(root)).sourceSha256,
      third.sourceSha256,
    );
    // Ordinary staging is confined to the disposable test repo; the helper itself
    // only reads Git and must not stage untracked capture inputs.
    assert.equal(
      execFileSync("git", ["-C", root, "diff", "--cached", "--name-only"], {
        encoding: "utf8",
        timeout: 5000,
      }).trim(),
      "apps/web/src/tracked.ts",
    );
  },
);
