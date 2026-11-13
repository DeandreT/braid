import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, symlinkSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createServer } from "node:http";
import { createRequire } from "node:module";
import test from "node:test";
const { editorMiddleware } = createRequire(import.meta.url)(
  "../../apps/web/tools/dev-server/editor-policy.cjs",
);

test("only shipped editor assets receive wasm permission and missing paths never serve the shell", async () => {
  const root = mkdtempSync(join(tmpdir(), "weft-editor-policy-"));
  mkdirSync(join(root, "edit"));
  writeFileSync(join(root, "edit/worker.js"), "/* worker */");
  writeFileSync(join(root, "edit/core.wasm"), "wasm");
  symlinkSync(join(root, "edit/worker.js"), join(root, "edit/core.js"));
  const middleware = editorMiddleware(root);
  const server = createServer((request, response) => {
    response.setHeader("Content-Security-Policy", "default-src 'none'");
    middleware(request, response, () => response.end("shell"));
  });
  try {
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    for (const name of ["worker.js", "core.wasm"]) {
      const response = await fetch(`${origin}/edit/${name}`);
      assert.equal(response.status, 200);
      assert.match(response.headers.get("Content-Security-Policy"), /'wasm-unsafe-eval'/);
      assert.ok(response.headers.get("Content-Security-Policy").includes(`${origin}/edit/`));
      await response.arrayBuffer();
    }
    for (const suffix of ["", "/", "/missing.js", "/core.js", "/private.txt"]) {
      const response = await fetch(`${origin}/edit${suffix}`);
      assert.equal(response.status, 404);
      assert.doesNotMatch(response.headers.get("Content-Security-Policy"), /unsafe-eval/);
      assert.equal(await response.text(), "");
    }
    const shell = await fetch(`${origin}/catalog/deep/link`);
    assert.equal(await shell.text(), "shell");
    assert.doesNotMatch(shell.headers.get("Content-Security-Policy"), /unsafe-eval/);
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
    rmSync(root, { recursive: true, force: true });
  }
});

test("the dev builder owns editor assets instead of Angular's earlier static middleware", () => {
  const config = JSON.parse(readFileSync(new URL("../../apps/web/angular.json", import.meta.url), "utf8"));
  assert.deepEqual(config.projects.weft.architect.build.configurations.devServer.assets[0].ignore, ["edit/**"]);
  assert.doesNotMatch(readFileSync(new URL("../../apps/web/index.dev.html", import.meta.url), "utf8"), /<base\b/i);
});
