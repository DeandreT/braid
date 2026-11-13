import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { createServer, request } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const {
  editorAssets,
} = require("../../apps/web/tools/dev-server/editor-assets.cjs");
const shell =
  "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

test("development editor assets retain their confined worker boundary", async (t) => {
  const root = mkdtempSync(join(tmpdir(), "braid-dev-editor-"));
  const worker = "export const controlled = true;";
  writeFileSync(join(root, "worker.js"), worker);
  writeFileSync(join(root, "core.js"), "export default {}; ");
  writeFileSync(
    join(root, "core.wasm"),
    Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]),
  );
  writeFileSync(join(root, "unshipped.js"), "must not be served");
  const middleware = editorAssets(root, "127.0.0.1", 0);
  const server = createServer((req, res) =>
    middleware(req, res, () => {
      res.setHeader("Content-Security-Policy", shell);
      res.end("ordinary route");
    }),
  );
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  const origin = `http://127.0.0.1:${port}`;
  const policy = `default-src 'none'; script-src ${origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`;
  const get = (path, options = {}) =>
    new Promise((resolve, reject) => {
      const req = request(
        { hostname: "127.0.0.1", port, path, ...options },
        (res) => {
          const chunks = [];
          res.on("data", (chunk) => chunks.push(chunk));
          res.on("end", () =>
            resolve({
              status: res.statusCode,
              headers: res.headers,
              body: Buffer.concat(chunks),
            }),
          );
        },
      );
      req.on("error", reject);
      req.end();
    });
  try {
    await t.test(
      "only shipped files get Wasm permission and the common headers",
      async () => {
        for (const path of [
          "/edit/worker.js",
          "/edit/core.js",
          "/edit/core.wasm",
        ]) {
          const response = await get(path);
          assert.equal(response.status, 200);
          assert.equal(response.headers["content-security-policy"], policy);
          assert.equal(response.headers["x-content-type-options"], "nosniff");
          assert.equal(response.headers["referrer-policy"], "no-referrer");
          assert.equal(
            response.headers["cross-origin-opener-policy"],
            "same-origin",
          );
          assert.equal(
            response.headers["cross-origin-resource-policy"],
            "same-origin",
          );
        }
        assert.equal((await get("/edit/worker.js")).body.toString(), worker);
        assert.equal(
          (await get("/edit/core.wasm")).headers["content-type"],
          "application/wasm",
        );
      },
    );
    await t.test(
      "HEAD retains headers without disclosing body bytes",
      async () => {
        const response = await get("/edit/worker.js", { method: "HEAD" });
        assert.equal(response.status, 200);
        assert.equal(response.headers["content-security-policy"], policy);
        assert.equal(response.body.length, 0);
      },
    );
    await t.test(
      "missing, unshipped and traversal paths never fall back to HTML",
      async () => {
        for (const path of [
          "/edit",
          "/edit/",
          "/edit/missing.js",
          "/edit/unshipped.js",
          "/edit/../index.html",
          "/edit/nested/index.html",
        ]) {
          const response = await get(path);
          assert.equal(response.status, 404);
          assert.equal(response.headers["content-security-policy"], shell);
          assert.equal(response.body.length, 0);
        }
        assert.equal((await get("/edit/%63ore.js")).status, 400);
      },
    );
    await t.test(
      "caller-supplied hosts and foreign origins cannot set policy origins",
      async () => {
        assert.equal(
          (await get("/edit/core.js", { headers: { host: "other.invalid" } }))
            .status,
          403,
        );
        assert.equal(
          (
            await get("/edit/core.js", {
              headers: { origin: "https://other.invalid" },
            })
          ).status,
          403,
        );
        assert.equal(
          (await get("/edit/core.js", { headers: { origin } })).status,
          200,
        );
      },
    );
    await t.test(
      "mutations are refused and ordinary routes stay under shell policy",
      async () => {
        assert.equal(
          (await get("/edit/core.js", { method: "POST" })).status,
          405,
        );
        const response = await get("/catalog");
        assert.equal(response.body.toString(), "ordinary route");
        assert.equal(response.headers["content-security-policy"], shell);
      },
    );
    await t.test(
      "a directory substituted for a shipped file is refused",
      async () => {
        rmSync(join(root, "core.js"));
        mkdirSync(join(root, "core.js"));
        assert.equal((await get("/edit/core.js")).status, 404);
      },
    );
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
    rmSync(root, { recursive: true });
  }
});
