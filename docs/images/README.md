# Documentation screenshots

The screenshots in this directory are captured from the Angular app by
`apps/web/tests/docs-screenshots.spec.ts`, which answers every API request with
self-authored demo data at a fixed 1440x900 viewport, clock and light theme.
They illustrate the UI; they are not qualification evidence.

[`manifest.json`](manifest.json) lists each image with its screen, route, the
spec test that captures it and the documents that embed it. Each regenerated
image also records its source commit, source/fixture digest, local source edits,
capture-spec and image digests, browser/runtime versions and capture settings.
The source digest hashes the sorted Git-tracked Angular source, browser tests,
capture/build configuration, guide fixtures and package inputs listed in
`capture` in the spec, using each path and its actual working-tree bytes.

## Updating

After a frontend change that affects a captured screen, regenerate the images
and commit them with the change:

```sh
npm run docs:screenshots
```

For an isolated screen change, pass a Playwright filter through both npm scripts,
for example `npm run docs:screenshots -- -- --grep "workspace chat"`. Only the
captured entries receive new provenance; other entries retain their own source.

Set `WEFT_CHROMIUM_EXECUTABLE` to the pinned Chrome for Testing, as for the
other browser checks. To add a screenshot, add a test that calls `capture` in
the spec, embed the image in the relevant document and list it in the manifest.
`npm run check` fails when a document references a missing image, or when the
manifest, the spec's captures, the files here and the embedding documents
disagree. Guide sections in `docs/user-guide` cannot embed images.
