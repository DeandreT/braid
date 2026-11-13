"use strict";
// The stock dev server drops `deployUrl`, and `base-uri 'none'` forbids a base
// element, so relative entry assets would resolve under a nested deep link.
const { createRequire } = require("node:module");
const path = require("node:path");
const { editorMiddleware } = require("./editor-policy.cjs");
const { executeDevServerBuilder } = require("@angular/build");
// Use the exact architect instance @angular/build itself was built against.
const { createBuilder } = createRequire(
  require.resolve("@angular/build/package.json"),
)("@angular-devkit/architect");

const rootAbsoluteEntryAssets = (html) =>
  html.replace(
    /(<(?:script|link)\b[^>]*?\s(?:src|href)=")(?![a-z][a-z\d+.-]*:|\/)/gi,
    "$1/",
  );

module.exports = createBuilder((options, context) =>
  executeDevServerBuilder(options, context, {
    middleware: [
      editorMiddleware(path.join(context.workspaceRoot, "public"), options.ssl),
    ],
    indexHtmlTransformer: async (html) => rootAbsoluteEntryAssets(html),
  }),
);
module.exports.rootAbsoluteEntryAssets = rootAbsoluteEntryAssets;
