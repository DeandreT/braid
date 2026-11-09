import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { checkDocsImages, imageReferences } from "./check.mjs";
import "./server.test.mjs";
import "./capture-source.test.mjs";
import "./session.test.mjs";

const spec = `test("catalog projects", async ({ page }, testInfo) => {
  await capture(page, testInfo, "projects.png");
});
`;
const manifest = (embeddedIn = ["README.md", "docs/catalog.md"]) =>
  JSON.stringify({
    images: [
      {
        file: "projects.png",
        screen: "Projects",
        route: "/catalog/organizations/:organizationId/projects",
        capture: "catalog projects",
        embeddedIn,
      },
    ],
  });

async function layout(files) {
  const root = await mkdtemp(join(tmpdir(), "weft-docs-images-"));
  const all = {
    "README.md": "![Projects](docs/images/projects.png)\n",
    "docs/catalog.md": "![Projects](images/projects.png)\n",
    "docs/user-guide/overview.md": "Plain text.\n",
    "docs/images/projects.png": "png",
    "docs/images/manifest.json": manifest(),
    "apps/web/tests/docs-screenshots.spec.ts": spec,
    ...files,
  };
  for (const [path, text] of Object.entries(all)) {
    if (text === null) continue;
    await mkdir(dirname(join(root, path)), { recursive: true });
    await writeFile(join(root, path), text);
  }
  return root;
}

async function problems(files = {}) {
  const root = await layout(files);
  try {
    return await checkDocsImages(root);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

test("a consistent layout has no problems", async () => {
  assert.deepEqual(await problems(), []);
});

test("references are read outside code fences and ignore remote images", () => {
  assert.deepEqual(
    imageReferences(
      '![a](images/a.png "t")\n```\n![b](b.png)\n```\n<img src="c.png">\n![d](https://example.test/d.png)\n',
    ),
    ["images/a.png", "c.png"],
  );
});

test("a missing referenced image is reported", async () => {
  const found = await problems({
    "docs/catalog.md":
      "![Projects](images/projects.png)\n![Gone](images/gone.png)\n",
  });
  assert.ok(found.some((p) => p.includes("missing image images/gone.png")));
});

test("a listed image that is not produced or not embedded is reported", async () => {
  const found = await problems({
    "docs/images/projects.png": null,
    "README.md": "No images.\n",
  });
  assert.ok(found.some((p) => p.includes("listed but not produced")));
  assert.ok(found.some((p) => p.includes("README.md in embeddedIn")));
});

test("unlisted images, captures and embeds are reported", async () => {
  const found = await problems({
    "docs/images/extra.png": "png",
    "docs/catalog.md":
      "![Projects](images/projects.png)\n![Extra](images/extra.png)\n",
    "apps/web/tests/docs-screenshots.spec.ts":
      spec +
      'test("x", async () => { await capture(page, testInfo, "x.png"); });\n',
  });
  assert.ok(found.some((p) => p.includes("extra.png is not listed")));
  assert.ok(found.some((p) => p.includes("extra.png is embedded but not")));
  assert.ok(found.some((p) => p.includes("captures x.png")));
});

test("a capture step that is not a test is reported", async () => {
  const found = await problems({
    "apps/web/tests/docs-screenshots.spec.ts": spec.replace(
      "catalog projects",
      "renamed",
    ),
  });
  assert.ok(found.some((p) => p.includes('capture step "catalog projects"')));
});

test("guide sections cannot embed images", async () => {
  const found = await problems({
    "docs/user-guide/overview.md": "![Projects](../images/projects.png)\n",
    "docs/images/manifest.json": manifest([
      "README.md",
      "docs/catalog.md",
      "docs/user-guide/overview.md",
    ]),
  });
  assert.ok(found.some((p) => p.includes("guide sections cannot contain")));
});

test("a retained capture digest must match the image bytes", async () => {
  const withProvenance = JSON.parse(manifest());
  withProvenance.images[0].provenance = { imageSha256: "0".repeat(64) };
  const found = await problems({
    "docs/images/manifest.json": JSON.stringify(withProvenance),
  });
  assert.ok(found.some((p) => p.includes("captured image digest")));
});

test("matching retained capture bytes are accepted", async () => {
  const withProvenance = JSON.parse(manifest());
  withProvenance.images[0].provenance = {
    imageSha256: createHash("sha256").update("png").digest("hex"),
  };
  assert.deepEqual(
    await problems({
      "docs/images/manifest.json": JSON.stringify(withProvenance),
    }),
    [],
  );
});
