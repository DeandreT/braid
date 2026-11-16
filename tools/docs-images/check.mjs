// Checks documentation image references without building or running the app.
import { readFile, readdir, stat } from "node:fs/promises";
import { dirname, join, posix, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const imageDirectory = "docs/images";
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

  const embedded = new Set();
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
        embedded.add(name);
      }
    }
  }

  let total = 0;
  const files = await readdir(join(root, imageDirectory)).catch(() => []);
  for (const name of files) {
    if (!imageExtensions.test(name)) continue;
    total += (await stat(join(root, imageDirectory, name))).size;
    if (!embedded.has(name))
      problems.push(`${imageDirectory}/${name} is not used by any document.`);
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
    console.error("Documentation images are inconsistent.");
    process.exitCode = 1;
  }
}
