// Keeps docs/images, its manifest, the capture spec and the Markdown that
// embeds the screenshots consistent. Run by `npm run check`.
import { readFile, readdir, stat } from "node:fs/promises";
import { createHash } from "node:crypto";
import { dirname, join, posix, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const imageDirectory = "docs/images";
export const manifestPath = "docs/images/manifest.json";
export const captureSpec = "apps/web/tests/docs-screenshots.spec.ts";
export const maximumTotalBytes = 4 * 1024 * 1024;
const imageExtensions = /\.(png|webp|jpe?g|gif|svg)$/i;
// Guide sections are embedded by the server, whose Markdown subset refuses images.
const imageFreeDirectory = "docs/user-guide";

async function markdownFiles(root) {
  const files = ["README.md"];
  async function walk(directory) {
    for (const entry of await readdir(join(root, directory), {
      withFileTypes: true,
    })) {
      const path = posix.join(directory, entry.name);
      if (entry.isDirectory()) await walk(path);
      else if (entry.name.endsWith(".md")) files.push(path);
    }
  }
  await walk("docs");
  return files;
}

/** Image targets of `![alt](target)` and `<img src="target">`, outside code fences. */
export function imageReferences(markdown) {
  const references = [];
  let fenced = false;
  for (const line of markdown.split(/\r?\n/)) {
    if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
    if (fenced) continue;
    for (const match of line.matchAll(/!\[[^\]]*\]\(\s*<?([^)\s>]+)>?/g))
      references.push(match[1]);
    for (const match of line.matchAll(/<img\b[^>]*\bsrc=["']([^"']+)["']/gi))
      references.push(match[1]);
  }
  return references.filter((target) => !/^[a-z][a-z0-9+.-]*:/i.test(target));
}

/** Returns a list of problems; an empty list means the images are consistent. */
export async function checkDocsImages(root) {
  const problems = [];
  const exists = (path) =>
    stat(join(root, path)).then(
      (value) => value.isFile(),
      () => false,
    );

  const embedded = new Map();
  for (const file of await markdownFiles(root)) {
    const text = await readFile(join(root, file), "utf8");
    for (const target of imageReferences(text)) {
      const path = posix.normalize(
        posix.join(posix.dirname(file), target.split(/[?#]/)[0]),
      );
      if (file.startsWith(imageFreeDirectory + "/"))
        problems.push(
          `${file} embeds ${target}; guide sections cannot contain images.`,
        );
      if (!(await exists(path)))
        problems.push(`${file} references missing image ${target}.`);
      if (path.startsWith(imageDirectory + "/")) {
        const name = path.slice(imageDirectory.length + 1);
        if (!embedded.has(name)) embedded.set(name, new Set());
        embedded.get(name).add(file);
      }
    }
  }

  let manifest;
  try {
    manifest = JSON.parse(await readFile(join(root, manifestPath), "utf8"));
  } catch (error) {
    return [...problems, `${manifestPath} is unreadable: ${error.message}`];
  }
  const entries = Array.isArray(manifest.images) ? manifest.images : [];
  if (entries.length === 0) problems.push(`${manifestPath} lists no images.`);

  const spec = await readFile(join(root, captureSpec), "utf8").catch(() => "");
  const titles = new Set(
    [...spec.matchAll(/^test\(\s*"([^"]+)"/gm)].map((match) => match[1]),
  );
  const captured = new Set(
    [...spec.matchAll(/capture\([^)]*?"([^"]+\.(?:png|webp))"\)/g)].map(
      (match) => match[1],
    ),
  );

  const listed = new Set();
  for (const entry of entries) {
    const name = entry?.file;
    if (typeof name !== "string" || !imageExtensions.test(name)) {
      problems.push(`${manifestPath} has an entry without an image file.`);
      continue;
    }
    if (listed.has(name)) problems.push(`${manifestPath} lists ${name} twice.`);
    listed.add(name);
    for (const key of ["screen", "route", "capture"])
      if (typeof entry[key] !== "string" || !entry[key])
        problems.push(`${manifestPath}: ${name} has no ${key}.`);
    if (!(await exists(posix.join(imageDirectory, name))))
      problems.push(
        `${name} is listed but not produced; run npm run docs:screenshots.`,
      );
    else if (entry.provenance) {
      const image = await readFile(join(root, imageDirectory, name));
      const digest = createHash("sha256").update(image).digest("hex");
      if (entry.provenance.imageSha256 !== digest)
        problems.push(`${name} does not match its captured image digest.`);
    }
    if (!captured.has(name))
      problems.push(
        `${name} is listed but ${captureSpec} does not capture it.`,
      );
    if (typeof entry.capture === "string" && !titles.has(entry.capture))
      problems.push(
        `${name}: capture step "${entry.capture}" is not a test in ${captureSpec}.`,
      );
    const declared = new Set(
      Array.isArray(entry.embeddedIn) ? entry.embeddedIn : [],
    );
    const actual = embedded.get(name) ?? new Set();
    if (actual.size === 0)
      problems.push(`${name} is listed but no document embeds it.`);
    for (const file of actual)
      if (!declared.has(file))
        problems.push(`${name} is embedded in ${file}; add it to embeddedIn.`);
    for (const file of declared)
      if (!actual.has(file))
        problems.push(
          `${name} lists ${file} in embeddedIn but is not used there.`,
        );
  }
  for (const name of captured)
    if (!listed.has(name))
      problems.push(
        `${captureSpec} captures ${name}, which the manifest omits.`,
      );
  for (const name of embedded.keys())
    if (!listed.has(name))
      problems.push(
        `${imageDirectory}/${name} is embedded but not in the manifest.`,
      );

  let total = 0;
  const files = await readdir(join(root, imageDirectory)).catch(() => []);
  for (const name of files) {
    if (!imageExtensions.test(name)) continue;
    total += (await stat(join(root, imageDirectory, name))).size;
    if (!listed.has(name))
      problems.push(`${imageDirectory}/${name} is not listed in the manifest.`);
  }
  if (total > maximumTotalBytes)
    problems.push(
      `${imageDirectory} holds ${total} bytes of images; keep it under ${maximumTotalBytes}.`,
    );
  return problems;
}

if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const problems = await checkDocsImages(root);
  for (const problem of problems) console.error(problem);
  if (problems.length) {
    console.error(
      `Documentation images are inconsistent; see ${manifestPath}.`,
    );
    process.exitCode = 1;
  }
}
