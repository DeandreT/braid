"""Collect the public sources of the browser and published .NET dependency closures.

All package archives stay opaque on disk; never extract upstream paths into the
release worktree. index.json describes what each archive actually contains.
"""

import base64
import concurrent.futures
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import struct
import subprocess
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


MIB = 1024**2
PACKAGE_LIMIT = 128 * MIB
SOURCE_LIMIT = 160 * MIB
TOTAL_LIMIT = 900 * MIB
NPM = r"(?:@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*"
NAME = r"[a-zA-Z0-9][A-Za-z0-9._-]*"
VERSION = r"\d[\w.-]*(?:\+[\w.-]+)?"
SHA = r"[0-9a-f]{40}"


def check(condition, message):
    if not condition:
        raise ValueError(message)


def download(url, limit):
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Braid-source-collector/1"})
            with urllib.request.urlopen(req, timeout=120) as response:
                check(response.geturl().startswith("https://"), f"insecure redirect: {url}")
                length = response.headers.get("Content-Length")
                check(not length or int(length) <= limit, f"size limit exceeded: {url}")
                data = response.read(limit + 1)
                check(len(data) <= limit, f"size limit exceeded: {url}")
                return data
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            if isinstance(error, urllib.error.HTTPError) and error.code not in {429, 500, 502, 503, 504}:
                raise
            if attempt == 2:
                raise
            time.sleep(2**attempt)


def sha512(data):
    return base64.b64encode(hashlib.sha512(data).digest()).decode()


def github(repo, ref):
    check(re.fullmatch(NAME + "/" + NAME, repo) and re.fullmatch(SHA, ref), f"invalid Git source: {repo}@{ref}")
    return f"https://codeload.github.com/{repo}/tar.gz/{ref}"


def json_file(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def bun_lock(braid):
    # Bun.lock is JSONC. Let the pinned Bun parser handle escapes/comments;
    # run only our code, with no dependencies, package scripts or preloads.
    with tempfile.TemporaryDirectory() as tmp:
        version = subprocess.check_output(["bun", "--version"], cwd=tmp, text=True).strip()
        check(version == (braid / ".bun-version").read_text().strip(), "incorrect Bun version")
        code = "console.log(JSON.stringify(Bun.JSONC.parse(await Bun.file(process.argv[1]).text())))"
        raw = subprocess.check_output(["bun", "-e", code, str((braid / "bun.lock").resolve())], cwd=tmp)
    lock = json.loads(raw)
    check(lock.get("lockfileVersion") == 2, "unsupported Bun lock version")
    return lock


def browser_closure(lock, manifest):
    workspace = lock["workspaces"]["apps/web"]
    fields = ("dependencies", "optionalDependencies", "peerDependencies")
    for f in fields:
        check(workspace.get(f, {}) == manifest.get(f, {}), f"stale browser lock: {f}")
    pkgs = lock["packages"]
    # Bun encodes nested installations as parent/child, including scoped names.
    def resolve(parents, name, optional):
        check(re.fullmatch(NPM, name), f"unsupported npm dependency: {name}")
        for i in range(len(parents), -1, -1):
            path = (*parents[:i], name)
            if "/".join(path) in pkgs:
                return path
        check(optional, f"required dependency missing from Bun lock: {name}")

    pending = [((), workspace)]
    seen = set()
    result = []
    while pending:
        parents, desc = pending.pop()
        for field in fields:
            for name in desc.get(field, {}):
                optional = field == "peerDependencies" and name in desc.get("optionalPeers", [])
                path = resolve(parents or (workspace["name"],), name, optional)
                if not path or path in seen:
                    continue
                seen.add(path)
                key = "/".join(path)
                row = pkgs[key]
                check(len(row) == 4 and row[1] == "" and row[0].startswith(name + "@"), f"unsupported Bun resolution: {key}")
                version = row[0][len(name) + 1:]
                check(re.fullmatch(VERSION, version) and row[3].startswith("sha512-"), f"unverified Bun resolution: {key}")
                result.append((key, name, version, row[3]))
                pending.append((path, row[2]))
    check(result, "empty browser production closure")
    return sorted(result)


def archive(dest, repo, ref):
    url = github(repo, ref)
    path = dest / "git" / (repo.replace("/", "--").lower() + "--" + ref + ".tar.gz")
    if not path.exists():
        data = download(url, SOURCE_LIMIT)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tf:
            check(tf.next() is not None, f"empty Git source archive: {url}")
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(data)
        check(sum(p.stat().st_size for p in dest.rglob("*") if p.is_file()) <= TOTAL_LIMIT, "package source budget exceeded")
    return path.relative_to(dest).as_posix()


def angular_maps(tf):
    # Only fesm2022 is browser runtime JS. Schematic CLI bundles and optional
    # generated locale globals also ship verbatim inside the locked tarball.
    members = tf.getnames()
    bundles = [p for p in members if p.startswith("package/fesm2022/") and p.endswith(".mjs")]
    check(bundles, "missing Angular browser bundles")
    count = 0
    for p in bundles:
        check(p + ".map" in members, f"missing Angular source map: {p}")
        m = json.load(tf.extractfile(p + ".map"))
        sources, contents = m.get("sources"), m.get("sourcesContent")
        check(isinstance(sources, list) and isinstance(contents, list) and len(contents) == len(sources)
              and all(isinstance(text, str) and text.strip() for text in contents), f"incomplete Angular sources: {p}")
        count += len(sources)
    check(count, "no embedded Angular source")
    return {"kind": "fesm2022 embedded sourcesContent", "bundles": len(bundles), "sourceEntries": count}


def browser_sources(braid, dest):
    rows = []
    for key, name, version, sri in browser_closure(bun_lock(braid), json_file(braid / "apps/web/package.json")):
        leaf = name.split("/")[-1]
        url = f"https://registry.npmjs.org/{name}/-/{leaf}-{version}.tgz"
        data = download(url, PACKAGE_LIMIT)
        check("sha512-" + sha512(data) == sri, f"Bun integrity mismatch: {key}")
        path = dest / "npm" / f"{name.replace('/', '--')}-{version}.tgz"
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(data)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tf:
            package = json.load(tf.extractfile("package/package.json"))
            check((package["name"], package["version"]) == (name, version), f"incorrect tarball: {name}")
            if name.startswith("@angular/"):
                evidence = angular_maps(tf)
            else:
                meta = json.loads(download(f"https://registry.npmjs.org/{name}/{version}", MIB))
                check(meta["dist"]["integrity"] == sri, f"registry disagrees with Bun: {key}")
                repo = meta["repository"]["url"].removeprefix("git+").removesuffix(".git").removeprefix("https://github.com/")
                ref = meta.get("gitHead")
                if (name, version) == ("@standard-schema/spec", "1.1.0"):
                    # Upstream release v1.1.0; locked module is type declarations
                    # and an empty ESM entry point.
                    ref = "2c4e37f6d92e7ddc1d0d0b6bab4173f1d8d41df1"
                check(ref, f"missing immutable upstream source: {name}@{version}")
                evidence = {"kind": "upstream source", "url": github(repo, ref), "archive": archive(dest, repo, ref)}
        rows.append({"lockKey": key, "name": name, "version": version, "integrity": sri, "registry": url,
                     "package": path.relative_to(dest).as_posix(), "source": evidence})
    return rows


def nuget_closure(braid):
    # Superset: all apps and operator tool locks, including every
    # TFM/RID, analyzers and compile-only packages. Test/probe locks are excluded.
    paths = sorted({*braid.glob("dotnet/apps/*/*.csproj"),
                    *braid.glob("dotnet/tools/*Tool/*.csproj")})
    check(paths, "no .NET production projects")
    rows = {}
    for proj in paths:
        lock = json_file(proj.with_name("packages.lock.json"))
        check(lock["version"] == 1, f"unknown NuGet lock format: {proj}")
        for pkgs in lock["dependencies"].values():
            for name, row in pkgs.items():
                if row["type"].lower() == "project":
                    continue
                check(row["type"].lower() in {"direct", "transitive", "centraltransitive"}, f"unknown NuGet record: {name}")
                ver, digest = row["resolved"], row["contentHash"]
                check(re.fullmatch(NAME, name) and re.fullmatch(VERSION, ver), f"invalid NuGet identity: {name}@{ver}")
                key = name.lower(), ver.lower()
                check(key not in rows or rows[key][2] == digest, f"conflicting NuGet hashes: {key}")
                rows[key] = name, ver, digest
    check(rows, "empty .NET package closure")
    return sorted(rows.values())


def nuget_hash(data, z):
    """SHA512 of the original unsigned ZIP (NuGet lock contentHash).

    nuget.org adds a final .signature.p7s entry after publication. Remove its
    local and central directory records and restore the original EOCD fields.
    Reject unsupported ZIP layouts instead of trusting unbound metadata.
    """
    if ".signature.p7s" not in z.namelist():
        return sha512(data)
    sig = z.getinfo(".signature.p7s")
    end = len(data) - 22
    (_, disk, start, n, count, length, offset, comment) = struct.unpack_from("<4s4H2LH", data, end)
    check(data[end:end+4] == b"PK\x05\x06" and (disk, start, comment) == (0, 0, 0)
          and n == count == len(z.infolist()) and offset + length == end, "unsupported signed NuGet ZIP")
    check(sig.header_offset == max(m.header_offset for m in z.infolist()), "unexpected NuGet signature placement")
    p = offset
    for _ in range(count):
        check(data[p:p+4] == b"PK\x01\x02", "bad NuGet central directory")
        size, extra, note = struct.unpack_from("<3H", data, p + 28)
        nxt = p + 46 + size + extra + note
        if data[p+46:p+46+size] == b".signature.p7s":
            check(nxt == end, "unexpected signature directory placement")
            break
        p = nxt
    check(p < end, "missing NuGet signature directory")
    digest = hashlib.sha512(memoryview(data)[:sig.header_offset])
    digest.update(memoryview(data)[offset:p])
    digest.update(struct.pack("<4s4H2LH", b"PK\x05\x06", 0, 0, count-1, count-1, p-offset, sig.header_offset, 0))
    return base64.b64encode(digest.digest()).decode()


# Publisher version tags, resolved to immutable commits for older packages with
# no repository commit field. Never fall back to the head of a branch.
LEGACY = {
    ("microsoft.azure.amqp", "2.7.0"): ("Azure/azure-amqp", "175d93e37b93b035e258c6b0cc3d877d43a0f110", "publisher tag v2.7.0"),
    ("nettopologysuite.io.postgis", "2.1.0"): ("NetTopologySuite/NetTopologySuite.IO.PostGIS", "c604084e2e39213aaceffdcc729d26ce7160019b", "publisher tag v2.1.0"),
}


def nuget_package(row, dest):
    name, ver, digest = row
    k, v = name.lower(), ver.lower()
    url = f"https://api.nuget.org/v3-flatcontainer/{k}/{v}/{k}.{v}.nupkg"
    data = download(url, PACKAGE_LIMIT)
    rec = {"name": name, "version": ver, "lockContentHash": digest, "registry": url}
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        check(nuget_hash(data, z) == digest or sha512(data) == digest, f"NuGet integrity mismatch: {name}@{ver}")
        spec, = [n for n in z.namelist() if n.lower().endswith(".nuspec") and "/" not in n]
        meta = ET.fromstring(z.read(spec)).find("{*}metadata")
        check(meta.findtext("{*}id").lower() == k and meta.findtext("{*}version").lower() == v, f"unexpected nuspec: {k}")
        if (k, v) == ("imtools", "4.0.0"):
            check("contentFiles/cs/netstandard2.0/ImTools/ImTools.cs" in z.namelist(), "missing ImTools source")
            path = dest / "nuget" / f"{k}-{v}.nupkg"
            path.write_bytes(data)
            return rec | {"source": {"kind": "C# source package", "archive": path.relative_to(dest).as_posix()}}
        if (k, v) in LEGACY:
            repo, ref, evidence = LEGACY[k, v]
        elif (k, v) == ("system.security.cryptography.protecteddata", "4.5.0"):
            repo, ref, evidence = "dotnet/corefx", z.read("version.txt").decode().strip(), "locked version.txt"
        else:
            origin = meta.find("{*}repository")
            check(origin is not None, f"manual source review: {name}@{ver} has no repository")
            repo = re.sub(r"^https?://github\.com/", "", origin.get("url", "")).removesuffix(".git")
            ref, evidence = origin.get("commit", ""), "locked nuspec repository commit"
        if (k, ref) == ("apache.arrow", "fcc16d879fe3618b2a6806851922c51beb1a3fd2"):
            # The C# binding moved; the exact nuspec commit is in arrow-dotnet.
            repo = "apache/arrow-dotnet"
            evidence += ", located in split C# repository"
        github(repo, ref)
        rec.update(repo=repo, commit=ref, provenance=evidence)
        return rec


PATCH_FORK = "Hawxy/DistributedLock"
PATCH_BASE = "f06ab0865999586ac28066bcb4253e0b7ab04c28"
PATCH_OVERLAY = "fd0c35057ad8b06460d84b093866496de82f1569"
# The reported SourceLink commit is unavailable. For the two reviewed locked
# net8.0 DLLs, all 42 Core and 14 Postgres non-generated PDB documents match
# this base with the listed replacements (SHA256, allowing checkout CRLF).
# Keep bound to the reviewed package bytes, not just the package names.
PATCHED = {
    ("patched.distributedlock.core", "1.0.11", "JUDAhM5ca24AxE+3OGFYGQoPszKkEOLX7jykJgglJxam668Cf5aHmB/MOJR7/fWZPWFC0egwQ9S9yQyzrFaECA=="):
        {"src/DistributedLock.Core/Internal/Data/ConnectionMonitor.cs": "efc9ea4b7c30fd6d831da13fc6c794195233cea95a04dba68b414c1b0983c64f"},
    ("patched.distributedlock.postgres", "1.3.3", "dCk328m177iENwX1TpUhoYapIAncPwYbeahz8YPb3IXMalteLl6p2w+cSmhDMptlu9zesjUGBYLmHi4Mu3X5fw=="):
        {"src/DistributedLock.Postgres/PostgresAdvisoryLock.cs": "83666d24f823daff807f4bd32cb1663964b0e94b53dbc9defdaf0293669b2255",
         "src/DistributedLock.Postgres/PostgresDatabaseConnection.cs": "a061dafa28587b30831218e3c9e63082190613473923557b82b4136c5c2ff57d"},
}


IDENTITY_REPO = "AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet"
IDENTITY_RELEASE = "ee19952f21d5c170a778bdca901b67076a1cd8f8"
IDENTITY_OVERLAY = "e3e47ee6914fe0a8e60b80933e4891951dbb361c"
# Compared every non-generated net10.0 source document checksum in the seven
# 8.19.2 symbol packages with the tag. One file needed an older revision.
IDENTITY_HASHES = {
    "HJbo/lnSfNHUfphPRT910poQc4T2/9+8svFLvzuaYHGAOJ2Tu+oEDqpX0BVP3BJ4OuUM1kylEKyaiX2fCAK3Cw==",
    "ui3fuBT4fs8kdKfBthI4NzLYIBIneEVS8UrL1JVBzAn80UiKmngBBi0BEByE7n/9c+EElcfFlCMFFTqpkBSLNA==",
    "r5YLDIxGOnkVJHrqXv/iD1FM1CgGrQOdriXuvuWvTPmKbnGANhEysq9XKmN6IHjf2a+9bAGEpnoRBsAQlvJU5w==",
    "sGxSsSrZXNmca6D+jHH2rVRyo2nNRd/g4H9CFbPmLLq0xgoH1U0orLWE5minfijw7+zq49tBs7txenbfAErRoQ==",
    "1XOcyY36cVymzE3qKdzKaUEZ4Pzt7ZpSa14JZoPPK1NLFUkQDs85TCqpV6XDo0YjFXj6nVK00AfOHppjghjhtw==",
    "GtPC1S02uH1gOO4fQ+zRysIicKmEXaYFP8PIkdJYXqMyruYhopre4ozVHp0XiDSA0+GJvOZH9prxCPvgBMg4Ww==",
    "gqhDC/icByKEutygpr+OFgAmjwTVowyzFjWB8K0q1ww8uFj5a1BQNL+QvijUl9uhq4p8OdDwcAIrjVC+eC9FVA==",
}


def reconstructed_source(dest, repo, base_ref, overlay_ref, directory, files, tfm):
    base = archive(dest, repo, base_ref)
    for name, digest in files.items():
        data = download(f"https://raw.githubusercontent.com/{repo}/{overlay_ref}/{name}", MIB)
        lf = data.replace(b"\r\n", b"\n")
        match = next((s for s in (lf, lf.replace(b"\n", b"\r\n")) if hashlib.sha256(s).hexdigest() == digest), None)
        check(match is not None, f"PDB document checksum mismatch: {name}")
        path = dest / directory / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(match)
    return {"kind": f"{tfm} non-generated C# documents verified against publisher PDB checksums",
            "baseArchive": base, "baseUrl": github(repo, base_ref), "applyOverlayAtRoot": directory,
            "overlayRepository": github(repo, overlay_ref),
            "overlayPdbSha256": files, "limit": "Original build commit unavailable; generated obj files and exact project configuration not reconstructed."}


MONOREPOS = {"dotnet/dotnet", "dotnet/runtime", "azure/azure-sdk-for-net", "dotnet/corefx"}


def monorepo_sources(repo, ref, rows, dest, cache):
    # A filtered fetch needs only Git trees (~8 MiB even for dotnet/dotnet).
    # Match actual project files at the verified commit. GitHub codeload accepts
    # Git tree IDs directly: transfer each selected project subtree once.
    db = cache / repo.replace("/", "--")
    env = os.environ | {"GIT_TERMINAL_PROMPT": "0", "GIT_CONFIG_NOSYSTEM": "1",
                        "GIT_CONFIG_GLOBAL": os.devnull, "GIT_ALLOW_PROTOCOL": "https"}
    def git(*args):
        return subprocess.check_output(["git", "-c", "credential.helper=", *args], cwd=db, env=env, timeout=240)
    if not db.exists():
        db.mkdir()
        git("init", "--quiet", "--bare")
    git("-c", "protocol.version=2", "fetch", "--quiet", "--no-tags", "--depth=1", "--filter=blob:none",
        "https://github.com/" + repo + ".git", ref)
    check(git("rev-parse", "FETCH_HEAD").decode().strip() == ref, f"unexpected Git revision: {repo}")
    files = {p.decode() for p in git("ls-tree", "-rz", "--name-only", ref).split(b"\0") if p}
    projects = [PurePosixPath(p) for p in files if p.lower().endswith(".csproj")]
    # Root notices live outside project subtrees. Bind each raw file to its
    # Git blob ID; keep them alongside, without unpacking the source archives.
    notices = []
    for name in sorted(n for n in files if "/" not in n and re.match(r"(?i)^(license|notice|third-party-notices)(\.|$)", n)):
        data = download(f"https://raw.githubusercontent.com/{repo}/{ref}/{name}", 10 * MIB)
        oid = git("rev-parse", f"{ref}:{name}").decode().strip()
        check(hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest() == oid, "repository notice mismatch")
        path = dest / "git/notices" / repo.replace("/", "--") / ref / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        notices.append(path.relative_to(dest).as_posix())
    for row in rows:
        name = row["name"]
        matches = [p for p in projects if p.stem.lower() == name.lower()
                   and not {"ref", "tests", "test", "samples", "reference", "shims"} & set(part.lower() for part in p.parts)]
        check(len(matches) == 1, f"manual source review: {repo}@{ref} {name} project matches: {matches}")
        project = matches[0]
        folder = project.parent.parent if project.parent.name == "src" else project.parent
        tree = git("rev-parse", f"{ref}:{folder}").decode().strip()
        shared = {}
        for location in ("src/runtime/src/libraries/Common/src", "src/aspnetcore/src/Shared",
                         "src/libraries/Common/src", "src/Common/src", "sdk/core/Azure.Core/src/Shared"):
            if any(p.startswith(location + "/") for p in files):
                shared_tree = git("rev-parse", f"{ref}:{location}").decode().strip()
                shared[location] = archive(dest, repo, shared_tree)
        row["source"] = {"kind": "project subtree", "project": str(project), "root": str(folder),
                         "tree": tree, "archive": archive(dest, repo, tree),
                         "notices": notices, "sharedSource": shared, "completeRepository": github(repo, ref)}


def collect(braid: Path, dest: Path):
    """Write locked linked sources and a provenance index under a new/empty dest."""
    braid, dest = braid.resolve(), dest.resolve()
    dest.mkdir(parents=True, exist_ok=True)
    check(not any(dest.iterdir()), "package source destination must be empty")
    browser = browser_sources(braid, dest)
    (dest / "nuget").mkdir()
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        nuget = list(pool.map(lambda row: nuget_package(row, dest), nuget_closure(braid)))
    groups = {}
    for row in nuget:
        key = row["name"].lower(), row["version"], row["lockContentHash"]
        if key in PATCHED:
            row["source"] = reconstructed_source(dest, PATCH_FORK, PATCH_BASE, PATCH_OVERLAY,
                                                  "nuget/distributedlock-overlay", PATCHED[key], "net8.0")
        elif row["lockContentHash"] in IDENTITY_HASHES:
            files = {"src/Microsoft.IdentityModel.Tokens/AppContextSwitches.cs":
                     "920074a86f0f8976c0bf642e0f377fcad4c23f8105d2c77ac69d0b40471583f9"}
            row["source"] = reconstructed_source(dest, IDENTITY_REPO, IDENTITY_RELEASE, IDENTITY_OVERLAY,
                                                  "nuget/identitymodel-overlay", files, "net10.0")
        elif "source" not in row:
            groups.setdefault((row["repo"], row["commit"]), []).append(row)
    with tempfile.TemporaryDirectory() as tmp:
        for (repo, ref), rows in sorted(groups.items()):
            if repo.lower() in MONOREPOS:
                monorepo_sources(repo, ref, rows, dest, Path(tmp))
            else:
                source = {"kind": "upstream repository", "url": github(repo, ref), "archive": archive(dest, repo, ref)}
                for row in rows:
                    row["source"] = source
    index = {
        "format": 1, "browser": browser, "nuget": nuget,
        "scope": [
            "Browser workspace production closure, including locked peers. Angular archive fesm2022/*.mjs.map sourcesContent contains original source text; inspect the sources and sourcesContent arrays together. The archive also contains published JS and license notices.",
            "NuGet app and operator Tool lockfiles across all target frameworks; includes project-transitive, analyzer and compile-time dependencies; SDK and shared framework are separate system prerequisites.",
            "For large monorepos local archives contain the named project and common source directories, with root notices alongside. Use completeRepository for additional shared files and parent build infrastructure at the verified commit.",
            "Git archives contain the published Git tree, not recursive submodule checkouts. Review upstream generated, native, and externally fetched inputs before claiming an offline rebuild.",
        ],
        "archives": {p.relative_to(dest).as_posix(): {"bytes": p.stat().st_size, "sha256": file_hash(p)}
                     for p in sorted(dest.rglob("*")) if p.is_file()},
    }
    (dest / "index.json").write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    check(sum(p.stat().st_size for p in dest.rglob("*") if p.is_file()) <= TOTAL_LIMIT, "package source budget exceeded")


def file_hash(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()
