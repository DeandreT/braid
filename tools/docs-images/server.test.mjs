import assert from "node:assert/strict";
import { mkdtemp, mkdir, open, rm, symlink, writeFile } from "node:fs/promises";
import http from "node:http";
import net from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { startDocsServer } from "./server.mjs";

const index =
  '<!doctype html><link rel="stylesheet" href="/styles.css"><weft-root></weft-root><script type="module" src="/main.js"></script>';
const deep =
  "/catalog/organizations/10000000-0000-4000-8000-000000000001/projects/20000000-0000-4000-8000-000000000001/mappings/30000000-0000-4000-8000-000000000001/revisions/40000000-0000-4000-8000-000000000001";

async function fixture(t) {
  const scratch = await mkdtemp(join(tmpdir(), "weft-docs-server-test-"));
  const root = join(scratch, "browser");
  await mkdir(root);
  await writeFile(join(root, "index.html"), index);
  await writeFile(join(root, "main.js"), "export const app = true;");
  await writeFile(join(root, "styles.css"), "body { color: green; }");
  const host = await startDocsServer(root, 0);
  t.after(async () => {
    await host.stop();
    await rm(scratch, { recursive: true, force: true });
  });
  return { scratch, root, host };
}

async function request(host, path, method = "GET", headers = {}) {
  const origin = new URL(host.origin);
  return await new Promise((fulfil, reject) => {
    const req = http.request(
      {
        hostname: origin.hostname,
        port: origin.port,
        path,
        method,
        headers,
        agent: false,
        timeout: 2000,
      },
      (response) => {
        const parts = [];
        response.on("data", (part) => parts.push(part));
        response.once("error", reject);
        response.once("end", () =>
          fulfil({
            status: response.statusCode,
            headers: response.headers,
            body: Buffer.concat(parts).toString(),
          }),
        );
      },
    );
    req.once("error", reject);
    req.once("timeout", () =>
      req.destroy(new Error("Test HTTP exchange timed out.")),
    );
    req.end();
  });
}

test(
  "root and explicit deep document routes serve root-relative assets under the unchanged policy",
  { timeout: 10000 },
  async (t) => {
    const { host } = await fixture(t);
    for (const path of [
      "/",
      "/index.html",
      "/catalog",
      "/catalog/search?q=order",
      deep,
      deep.replace(/\/revisions\/[^/]+$/, "/comparisons?left=1&right=3"),
      "/chat",
      "/help",
      "/administration",
    ]) {
      const response = await request(host, path, "GET", {
        Accept: "text/html,application/xhtml+xml",
      });
      assert.equal(response.status, 200, path);
      assert.equal(response.body, index);
      assert.match(response.body, /src="\/main.js"/);
      assert.match(response.body, /href="\/styles.css"/);
      assert.doesNotMatch(response.body, /<base\b/);
      assert.equal(
        response.headers["content-type"],
        "text/html; charset=utf-8",
      );
    }
    for (const [path, media] of [
      ["/main.js", "text/javascript; charset=utf-8"],
      ["/styles.css", "text/css; charset=utf-8"],
    ]) {
      const response = await request(host, path);
      assert.equal(response.status, 200);
      assert.equal(response.headers["content-type"], media);
      assert.equal(response.headers["cache-control"], "no-store");
    }
  },
);

test(
  "missing assets, APIs, unknown routes and non-document requests never receive fallback HTML",
  { timeout: 10000 },
  async (t) => {
    const { host } = await fixture(t);
    for (const path of [
      "/missing.js",
      "/missing.css",
      "/catalog/missing.js",
      "/api",
      "/api/auth/session",
      "/api/v1/health",
      "/unknown",
      "/nested/index.html",
      "/private.dll",
    ]) {
      const response = await request(host, path, "GET", {
        Accept: "text/html",
      });
      assert.equal(response.status, 404, path);
      assert.equal(response.body, "");
    }
    assert.equal((await request(host, deep)).status, 404);
  },
);

test(
  "HEAD, method, host, URL length and raw traversal policies fail closed",
  { timeout: 10000 },
  async (t) => {
    const { host } = await fixture(t);
    const head = await request(host, "/", "HEAD");
    assert.equal(head.status, 200);
    assert.equal(head.body, "");
    assert.equal(
      Number(head.headers["content-length"]),
      Buffer.byteLength(index),
    );
    for (const method of ["POST", "PUT", "DELETE", "OPTIONS", "TRACE"]) {
      const response = await request(host, "/", method);
      assert.equal(response.status, 405);
      assert.equal(response.headers.allow, "GET, HEAD");
    }
    for (const path of [
      "/../index.html",
      "/a/../../index.html",
      "/%2e%2e/index.html",
      "/%252e%252e/index.html",
      "/%2findex.html",
      "/a\\index.html",
      "//elsewhere/index.html",
      "/.env",
      "/NUL.js",
      "/COM1.css",
      "/styles.css.",
      "/STYLE~1.css",
      "/index.html#fragment",
      "/" + "a".repeat(2048),
      "http://elsewhere/index.html",
    ]) {
      const response = await request(host, path);
      assert.equal(response.status, 400, path);
    }
    const wrongHost = await request(host, "/", "GET", { Host: "elsewhere" });
    assert.equal(wrongHost.status, 400);
    assert.equal(
      (await request(host, "/", "GET", { "Content-Length": "1" })).status,
      400,
    );
    assert.equal((await request(host, "/missing.js", "HEAD")).body, "");
  },
);

test(
  "linked roots/assets, directories and oversized files cannot escape the private build",
  { timeout: 10000 },
  async (t) => {
    const { host, root, scratch } = await fixture(t);
    const outside = join(scratch, "outside");
    await mkdir(outside);
    await writeFile(join(outside, "secret.js"), "not a build asset");
    await symlink(
      outside,
      join(root, "escape"),
      process.platform === "win32" ? "junction" : "dir",
    );
    const denied = await request(host, "/escape/secret.js");
    assert.equal(denied.status, 404);
    assert.equal(denied.body, "");
    await assert.rejects(
      startDocsServer(join(root, "escape"), 0),
      /Linked build root refused/,
    );
    await mkdir(join(root, "directory.js"));
    assert.equal((await request(host, "/directory.js")).status, 404);
    const large = await open(join(root, "large.js"), "w");
    try {
      await large.truncate(8 * 1024 * 1024 + 1);
    } finally {
      await large.close();
    }
    assert.equal((await request(host, "/large.js")).status, 404);
  },
);

test(
  "exclusive bind never adopts another listener; stop settles incomplete owned sockets",
  { timeout: 10000 },
  async (t) => {
    const { host, root } = await fixture(t);
    const port = Number(new URL(host.origin).port);
    await assert.rejects(startDocsServer(root, port), { code: "EADDRINUSE" });
    assert.equal((await request(host, "/")).status, 200);
    const socket = net.connect(port, "127.0.0.1");
    t.after(() => socket.destroy());
    await new Promise((fulfil, reject) => {
      socket.once("connect", fulfil);
      socket.once("error", reject);
    });
    socket.write("GET / HTTP/1.1\r\nHost:");
    const closed = new Promise((fulfil) => socket.once("close", fulfil));
    const stopped = host.stop();
    assert.equal(host.stop(), stopped);
    await stopped;
    await closed;
    await assert.rejects(request(host, "/"), { code: "ECONNREFUSED" });
  },
);

test(
  "HTTP parser errors close with a bare 400",
  { timeout: 10000 },
  async (t) => {
    const { host } = await fixture(t);
    const socket = net.connect(Number(new URL(host.origin).port), "127.0.0.1");
    t.after(() => socket.destroy());
    socket.setTimeout(2000, () =>
      socket.destroy(new Error("Parser test timed out.")),
    );
    const response = await new Promise((fulfil, reject) => {
      let text = "";
      socket.on("data", (bytes) => {
        text += bytes;
      });
      socket.once("error", reject);
      socket.once("end", () => fulfil(text));
      socket.once("connect", () =>
        socket.end("GET / HTTP/1.1\r\nInvalid Header: value\r\n\r\n"),
      );
    });
    assert.match(response, /^HTTP\/1.1 400 /);
  },
);
