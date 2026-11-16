import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { createServer, request } from "node:http";
import { createServer as createHttp2Server, connect } from "node:http2";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createRequire } from "node:module";
import { runInNewContext } from "node:vm";

const require = createRequire(import.meta.url);
const {
  editorAssets,
} = require("../../apps/web/tools/dev-server/editor-assets.cjs");
const shell =
  "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

test("builder resolves project editor assets and pins its configured transport", async () => {
  const root = mkdtempSync(join(tmpdir(), "braid-editor-builder-"));
  const source = readFileSync(
    new URL("../../apps/web/tools/dev-server/index.cjs", import.meta.url),
    "utf8",
  );
  try {
    for (const projectRoot of ["", "nested-project"]) {
      const assets = join(root, projectRoot, "public/edit");
      mkdirSync(assets, { recursive: true });
      writeFileSync(join(assets, "worker.js"), "export {}; ");
      for (const ssl of [false, true]) {
        const exported = { exports: {} };
        let called = false;
        const mockRequire = (name) => {
          if (name === "node:module")
            return {
              createRequire: () => () => ({
                createBuilder: (handler) => handler,
              }),
            };
          if (name === "node:path") return { join };
          if (name === "./editor-assets.cjs") return { editorAssets };
          if (name === "@angular/build")
            return {
              async *executeDevServerBuilder(options, context, extensions) {
                called = true;
                assert.equal(options.ssl, ssl);
                assert.equal(context.workspaceRoot, root);
                assert.equal(
                  await extensions.indexHtmlTransformer(
                    '<script src="main.js"></script>',
                  ),
                  '<script src="/main.js"></script>',
                );
                const headers = new Map();
                const response = {
                  setHeader: (name, value) => headers.set(name, value),
                  end() {},
                };
                extensions.middleware[0](
                  {
                    url: "/edit/worker.js",
                    method: "GET",
                    headers: {
                      host: "127.0.0.1:5254",
                      origin: `${ssl ? "https" : "http"}://127.0.0.1:5254`,
                    },
                    socket: { localPort: 5254 },
                  },
                  response,
                  () => assert.fail("Builder asset lookup fell through"),
                );
                assert.equal(response.statusCode, 200);
                assert.ok(
                  headers
                    .get("Content-Security-Policy")
                    .includes(
                      `script-src ${ssl ? "https" : "http"}://127.0.0.1:5254/edit/`,
                    ),
                );
                yield { success: true };
              },
            };
          throw new Error(`Unexpected module: ${name}`);
        };
        mockRequire.resolve = () => "angular-build-package.json";
        runInNewContext(source, { require: mockRequire, module: exported });
        const context = {
          workspaceRoot: root,
          target: { project: "braid" },
          async getProjectMetadata(name) {
            assert.equal(name, "braid");
            return { root: projectRoot };
          },
        };
        const options = {
          buildTarget: "braid:build:devServer",
          host: "127.0.0.1",
          // Angular can override the requested port; trust only the listener.
          port: 4200,
          ssl,
        };
        for await (const result of exported.exports(options, context))
          assert.equal(result.success, true);
        assert.equal(called, true);
        for (const buildTarget of [
          "braid:build:production",
          "braid:build:development",
          "braid:build:devServer,production",
          undefined,
        ]) {
          await assert.rejects(async () => {
            for await (const result of exported.exports(
              { ...options, buildTarget },
              context,
            ))
              void result;
          }, /dedicated policy boundary/);
        }
      }
    }
  } finally {
    rmSync(root, { recursive: true });
  }
});

test("HTTP/2 binds its authority to the actual listener and rejects conflicting Host", async () => {
  const root = mkdtempSync(join(tmpdir(), "braid-http2-editor-"));
  writeFileSync(join(root, "worker.js"), "export {}; ");
  const middleware = editorAssets(root, "127.0.0.1");
  const server = createHttp2Server((req, res) =>
    middleware(req, res, () => {
      res.statusCode = 404;
      res.end();
    }),
  );
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  const origin = `http://127.0.0.1:${port}`;
  const client = connect(origin);
  const get = (headers = {}) =>
    new Promise((resolve, reject) => {
      const stream = client.request({ ":path": "/edit/worker.js", ...headers });
      let response;
      const chunks = [];
      stream.on("response", (headers) => {
        response = headers;
      });
      stream.on("data", (chunk) => chunks.push(chunk));
      stream.on("end", () =>
        resolve({ headers: response, body: Buffer.concat(chunks) }),
      );
      stream.on("error", reject);
      stream.end();
    });
  try {
    const allowed = await get({ origin });
    assert.equal(allowed.headers[":status"], 200);
    assert.match(
      allowed.headers["content-security-policy"],
      new RegExp(`script-src http://127\\.0\\.0\\.1:${port}/edit/`),
    );
    assert.equal(allowed.body.toString(), "export {}; ");
    for (const headers of [
      { ":authority": "other.invalid" },
      { host: "other.invalid" },
      { origin: "https://other.invalid" },
    ]) {
      const refused = await get(headers);
      assert.equal(refused.headers[":status"], 403);
      assert.equal(refused.headers["content-security-policy"], shell);
      assert.equal(refused.body.length, 0);
    }
  } finally {
    client.destroy();
    await new Promise((resolve) => server.close(resolve));
    rmSync(root, { recursive: true });
  }
});

test("default listener ports use the browser's canonical Host and Origin", () => {
  const root = mkdtempSync(join(tmpdir(), "braid-default-port-"));
  writeFileSync(join(root, "worker.js"), "export {}; ");
  try {
    for (const [protocol, port] of [
      ["http", 80],
      ["https", 443],
    ]) {
      const headers = new Map();
      const response = {
        setHeader: (name, value) => headers.set(name, value),
        end() {},
      };
      editorAssets(root, "localhost", protocol)(
        {
          url: "/edit/worker.js",
          method: "GET",
          headers: { host: "localhost", origin: `${protocol}://localhost` },
          socket: { localPort: port },
        },
        response,
        () =>
          assert.fail("A canonical default-port request must not fall through"),
      );
      assert.equal(response.statusCode, 200);
      assert.ok(
        headers
          .get("Content-Security-Policy")
          .includes(`script-src ${protocol}://localhost/edit/`),
      );
    }
  } finally {
    rmSync(root, { recursive: true });
  }
});

test("HTTPS editor policy uses the configured transport, never forwarding headers", () => {
  const root = mkdtempSync(join(tmpdir(), "braid-https-editor-"));
  writeFileSync(join(root, "worker.js"), "export {}; ");
  const headers = new Map();
  const response = {
    setHeader: (name, value) => headers.set(name, value),
    end() {},
  };
  try {
    const middleware = editorAssets(root, "localhost", "https");
    middleware(
      {
        url: "/edit/worker.js",
        method: "GET",
        headers: {
          host: "localhost:5254",
          origin: "https://localhost:5254",
          "x-forwarded-proto": "http",
          "x-forwarded-port": "443",
        },
        socket: { localPort: 5254 },
      },
      response,
      () => assert.fail("A shipped editor asset must not fall through"),
    );
    assert.equal(response.statusCode, 200);
    assert.match(
      headers.get("Content-Security-Policy"),
      /script-src https:\/\/localhost:5254\/edit\//,
    );
    middleware(
      {
        url: "/edit/worker.js",
        method: "GET",
        headers: { host: "localhost:5254", origin: "http://localhost:5254" },
        socket: { localPort: 5254 },
      },
      response,
      () => assert.fail("An incorrect transport must not fall through"),
    );
    assert.equal(response.statusCode, 403);
    middleware(
      {
        url: "/edit/worker.js",
        method: "GET",
        headers: { host: "localhost:4200" },
        socket: { localPort: 5254 },
      },
      response,
      () =>
        assert.fail(
          "A host for the requested rather than bound port must be refused",
        ),
    );
    assert.equal(response.statusCode, 403);
    middleware(
      {
        url: "/edit/worker.js",
        method: "GET",
        headers: { host: "localhost:5254" },
        socket: {},
      },
      response,
      () => assert.fail("A missing local listener port must be refused"),
    );
    assert.equal(response.statusCode, 403);
    assert.throws(() => editorAssets(root, "localhost", "ftp"));
  } finally {
    rmSync(root, { recursive: true });
  }
});

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
  const middleware = editorAssets(root, "127.0.0.1");
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
