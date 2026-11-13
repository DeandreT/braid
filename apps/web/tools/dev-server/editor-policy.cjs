"use strict";
const fs = require("node:fs");
const path = require("node:path");

const assets = new Map([
  ["/edit/core.js", "text/javascript; charset=utf-8"],
  ["/edit/worker.js", "text/javascript; charset=utf-8"],
  ["/edit/core.wasm", "application/wasm"],
]);

// These assets are excluded from Angular's earlier static middleware so only
// actual shipped files can receive the worker's narrowly scoped wasm policy.
function editorMiddleware(publicDirectory, secure = false) {
  return (request, response, next) => {
    const pathname = new URL(request.url, "http://localhost").pathname;
    if (pathname !== "/edit" && !pathname.startsWith("/edit/")) return next();
    const mediaType = assets.get(pathname);
    const file = path.join(publicDirectory, pathname.slice(1));
    let exists = false;
    try {
      exists = mediaType !== undefined && fs.lstatSync(file).isFile();
    } catch {
      // Missing assets never fall back to the HTML shell.
    }
    if (!exists) {
      response.statusCode = 404;
      response.end();
      return;
    }
    const host = request.headers.host ?? "";
    if (!/^[a-zA-Z0-9.:[\]-]+$/.test(host)) {
      response.statusCode = 400;
      response.end();
      return;
    }
    let origin;
    try {
      origin = new URL(`${secure ? "https" : "http"}://${host}`).origin;
    } catch {
      response.statusCode = 400;
      response.end();
      return;
    }
    response.setHeader(
      "Content-Security-Policy",
      `default-src 'none'; script-src ${origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
    );
    response.setHeader("Content-Type", mediaType);
    response.setHeader("Cache-Control", "no-store");
    const stream = fs.createReadStream(file);
    stream.on("error", () => response.destroy());
    response.on("close", () => stream.destroy());
    if (request.method === "HEAD") {
      stream.destroy();
      response.end();
    } else {
      stream.pipe(response);
    }
  };
}

module.exports = { editorMiddleware };
