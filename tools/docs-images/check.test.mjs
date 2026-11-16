import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import {
  checkDocsImages,
  imageReferences,
  maximumTotalBytes,
} from "./check.mjs";

async function layout(files) {
  const root = await mkdtemp(join(tmpdir(), "braid-docs-images-"));
  const all = {
    "README.md": "![Projects](docs/images/projects.png)\n",
    "docs/catalog.md": "![Projects](images/projects.png)\n",
    "docs/user-guide/overview.md": "Plain text.\n",
    "docs/images/projects.png": "png",
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

test("consistent images need no capture tooling or manifest", async () => {
  assert.deepEqual(await problems(), []);
});

test("documentation without images needs no image directory", async () => {
  assert.deepEqual(
    await problems({
      "README.md": "Project.\n",
      "docs/catalog.md": "Catalog.\n",
      "docs/images/projects.png": null,
    }),
    [],
  );
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

test("a missing image reports each referring document", async () => {
  const found = await problems({ "docs/images/projects.png": null });
  assert.ok(found.some((p) => p.startsWith("README.md references missing")));
  assert.ok(
    found.some((p) => p.startsWith("docs/catalog.md references missing")),
  );
});

test("an image not embedded in any document is reported", async () => {
  const found = await problems({ "docs/images/extra.png": "png" });
  assert.ok(found.some((p) => p.includes("extra.png is not used")));
});

test("guide sections cannot embed images", async () => {
  const found = await problems({
    "docs/user-guide/overview.md": "![Projects](../images/projects.png)\n",
  });
  assert.ok(found.some((p) => p.includes("guide sections cannot contain")));
});

test("image references resolve relative paths, queries and fragments", async () => {
  assert.deepEqual(
    await problems({
      "docs/nested/example.md":
        '![Projects](../images/projects.png?size=small#view)\n<img src="../images/projects.png">\n',
    }),
    [],
  );
});

test("oversized documentation images are reported", async () => {
  const found = await problems({
    "docs/images/projects.png": Buffer.alloc(maximumTotalBytes + 1),
  });
  assert.ok(found.some((p) => p.includes("keep it under")));
});
