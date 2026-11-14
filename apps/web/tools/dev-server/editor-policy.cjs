"use strict";
const path = require("node:path");
const { editorAssets } = require("./editor-assets.cjs");

// These assets are excluded from Angular's earlier static middleware so only
// actual shipped files can receive the worker's narrowly scoped wasm policy.
function editorMiddleware(publicDirectory, secure = false, host = "127.0.0.1") {
  // Keep the compatibility entry point on the same configured-host, actual-port
  // admission and no-symlink checks as the architect builder.
  return editorAssets(
    path.join(publicDirectory, "edit"),
    host,
    secure ? "https" : "http",
  );
}

module.exports = { editorMiddleware };
