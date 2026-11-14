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

async function* run(options, context) {
  const projectName = context.target?.project;
  if (
    !projectName ||
    options.buildTarget !== `${projectName}:build:devServer`
  ) {
    throw new Error(
      "Use the devServer build configuration so editor assets stay inside their dedicated policy boundary.",
    );
  }
  const project = await context.getProjectMetadata(projectName);
  yield* executeDevServerBuilder(options, context, {
    indexHtmlTransformer: async (html) => rootAbsoluteEntryAssets(html),
    middleware: [
      editorAssets(
        join(context.workspaceRoot, project.root ?? "", "public/edit"),
        options.host ?? "localhost",
        options.ssl ? "https" : "http",
      ),
    ],
  });
}
module.exports = createBuilder(run);
module.exports.rootAbsoluteEntryAssets = rootAbsoluteEntryAssets;
