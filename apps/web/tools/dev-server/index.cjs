"use strict";
// The stock dev server drops `deployUrl`, and `base-uri 'none'` forbids a base
// element, so relative entry assets would resolve under a nested deep link.
const { createRequire } = require("node:module");
const { executeDevServerBuilder } = require("@angular/build");
const { join } = require("node:path");
const { editorAssets } = require("./editor-assets.cjs");
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
    indexHtmlTransformer: async (html) => rootAbsoluteEntryAssets(html),
    middleware: [
      editorAssets(
        join(context.workspaceRoot, "apps/web/public/edit"),
        options.host ?? "localhost",
        options.port ?? 4200,
      ),
    ],
  }),
);
module.exports.rootAbsoluteEntryAssets = rootAbsoluteEntryAssets;
