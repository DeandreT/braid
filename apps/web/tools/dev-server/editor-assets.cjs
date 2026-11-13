"use strict";
const { lstatSync, readFileSync } = require("node:fs");
const { join } = require("node:path");

const shellPolicy =
  "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
const files = new Map([
  ["/edit/worker.js", "text/javascript; charset=utf-8"],
  ["/edit/core.js", "text/javascript; charset=utf-8"],
  ["/edit/core.wasm", "application/wasm"],
  ["/edit/third-party/README.md", "text/plain; charset=utf-8"],
  [
    "/edit/third-party/dotnet-xml-name-tables-MIT.txt",
    "text/plain; charset=utf-8",
  ],
  ["/edit/third-party/ferrule-GPL-3.0-only.txt", "text/plain; charset=utf-8"],
]);

/** Only the excluded, shipped editor package gets the dedicated-worker policy. */
function editorAssets(root, host, port) {
  return (request, response, next) => {
    const raw = request.url ?? "";
    let path;
    try {
      path = decodeURIComponent(raw.split("?")[0]);
    } catch {
      response.statusCode = 400;
      response.end();
      return;
    }
    if (path !== "/edit" && !path.startsWith("/edit/")) {
      next();
      return;
    }

    const headers = {
      "Content-Security-Policy": shellPolicy,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer",
      "Cross-Origin-Opener-Policy": "same-origin",
      "Cross-Origin-Resource-Policy": "same-origin",
      "Cache-Control": "no-store",
    };
    for (const [name, value] of Object.entries(headers))
      response.setHeader(name, value);
    const deny = (status) => {
      response.statusCode = status;
      response.end();
    };
    const boundPort = port || request.socket.localPort;
    const authority = `${host.includes(":") ? `[${host}]` : host}:${boundPort}`;
    const origin = `http://${authority}`;
    if (
      !["127.0.0.1", "localhost", "::1"].includes(host) ||
      request.headers.host !== authority ||
      (request.headers.origin && request.headers.origin !== origin)
    ) {
      deny(403);
      return;
    }
    if (raw.split("?")[0] !== path) {
      deny(400);
      return;
    }
    if (!["GET", "HEAD"].includes(request.method)) {
      response.setHeader("Allow", "GET, HEAD");
      deny(405);
      return;
    }
    const type = files.get(path);
    if (!type) {
      deny(404);
      return;
    }
    try {
      const rootInfo = lstatSync(root);
      if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) {
        deny(404);
        return;
      }
      let file = root;
      const parts = path.slice("/edit/".length).split("/");
      for (const [index, part] of parts.entries()) {
        file = join(file, part);
        const info = lstatSync(file);
        if (
          info.isSymbolicLink() ||
          (index === parts.length - 1 ? !info.isFile() : !info.isDirectory())
        ) {
          deny(404);
          return;
        }
      }
      const bytes = readFileSync(file);
      response.setHeader(
        "Content-Security-Policy",
        `default-src 'none'; script-src ${origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
      );
      response.setHeader("Content-Type", type);
      response.setHeader("Content-Length", bytes.length);
      response.statusCode = 200;
      response.end(request.method === "HEAD" ? undefined : bytes);
    } catch (error) {
      deny(error.code === "ENOENT" || error.code === "ENOTDIR" ? 404 : 500);
    }
  };
}
module.exports = { editorAssets };
