// Run after the production Angular build, from the repository root.
import { copyFile, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

const base = "apps/web/dist/braid";
const web = join(base, "browser");
const [version, archive] = process.argv.slice(2);
const items = [
  ["LICENSE.txt", "braid license"],
  ["edit/third-party/ferrule-GPL-3.0-only.txt", "Ferrule license"],
  ["edit/third-party/dotnet-xml-name-tables-MIT.txt", "XML table attribution"],
  ["edit/third-party/rust-std-COPYRIGHT.html", "Rust library notices"],
  ["3rdpartylicenses.txt", "JavaScript bundle notices"],
];

await copyFile("LICENSE", join(web, "LICENSE.txt"));
await copyFile(
  join(base, "3rdpartylicenses.txt"),
  join(web, "3rdpartylicenses.txt"),
);
for (const [path] of items) {
  if (!(await readFile(join(web, path))).length)
    throw new Error(`Missing notice: ${path}`);
}
let section = `<ul>${items.map(([path, label]) => `<li><a href="${path}">${label}</a></li>`).join("")}</ul>`;
if (version || archive) {
  const url = new URL(archive);
  if (
    !/^v(0|[1-9]\d*)(\.(0|[1-9]\d*)){2}$/.test(version) ||
    url.protocol !== "https:" ||
    url.username ||
    url.password ||
    url.hash
  )
    throw new Error("Expected release version and public HTTPS archive URL");
  const href = url.href.replaceAll("&", "&amp;").replaceAll('"', "&quot;");
  section += `<p><a href="${href}">Download corresponding source for braid ${version}</a> (compressed tar archive).</p>`;
}
const page = join(web, "licenses.html");
const html = await readFile(page, "utf8");
if (!html.includes("<!-- release-assets -->"))
  throw new Error("No release asset insertion point");
await writeFile(
  page,
  html.replace("<!-- release-assets -->", () => section),
);
