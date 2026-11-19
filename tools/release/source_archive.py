"""Build/verify a Braid source-release asset. Publisher: Linux, Python 3.12+, Git, rustup/Cargo, Bun."""

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import tomllib


FERRULE = "https://github.com/DeandreT/ferrule"
SHA = r"[0-9a-f]{40}"
TAG = r"v(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}"
MAX_BYTES = 1900 * 1024**2  # Below GitHub Releases' 2 GiB per-file limit.


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def identities(version, commit):
    if not re.fullmatch(TAG, version) or not re.fullmatch(SHA, commit):
        raise ValueError("Expected an exact vX.Y.Z and a full lowercase commit id")
    return f"braid-{version}-source.tar.gz"


def git_tree(repository, commit, dest):
    """Export tracked blobs only; reject submodules or attribute-filtered archives."""
    tree = run("git", "-C", repository, "rev-parse", "--verify", commit + "^{tree}",
               stdout=subprocess.PIPE, text=True).stdout.strip()
    entries = run("git", "-C", repository, "ls-tree", "-rz", tree, stdout=subprocess.PIPE).stdout
    expected = {}
    for entry in entries.rstrip(b"\0").split(b"\0"):
        info, name = entry.split(b"\t", 1)
        if info.split()[0] not in (b"100644", b"100755", b"120000"):
            raise ValueError(f"Unsupported git entry: {name!r}")
        expected[name.decode("utf-8")] = info.split()[2].decode("ascii")
    dest.mkdir()
    # Archive the tree, not a commit: export-subst cannot rewrite tracked bytes.
    with tempfile.TemporaryFile() as stream:
        run("git", "-c", "core.autocrlf=false", "-C", repository, "archive", "--format=tar", tree, stdout=stream)
        stream.seek(0)
        with tarfile.open(fileobj=stream) as archive:
            if {m.name for m in archive if not m.isdir()} != expected.keys():
                raise ValueError("Export differs from tracked tree; check export-ignore attributes")
            archive.extractall(dest, filter="data")
    for name, oid in expected.items():
        path = dest / name
        if path.is_symlink():
            data = os.fsencode(os.readlink(path))
        else:
            data = path.read_bytes()
            if data.startswith(b"version https://git-lfs.github.com/spec/v1\n"):
                raise ValueError(f"Cannot ship a Git LFS pointer as source: {name}")
        if hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest() != oid:
            raise ValueError(f"Git archive changed tracked bytes: {name}")


def rust_inputs(braid):
    locks = sorted((braid / "engines").glob("*/Cargo.lock"))
    packages, pins = set(), set()
    for lock in locks:
        for package in tomllib.loads(lock.read_text(encoding="utf-8"))["package"]:
            source = package.get("source")
            if source is None or source == "registry+https://github.com/rust-lang/crates.io-index":
                continue
            match = re.fullmatch(r"git\+" + re.escape(FERRULE) + r"\?rev=(" + SHA + r")#\1", source)
            if not match:
                raise ValueError(f"Unadmitted source in {lock.name}: {source}")
            packages.add(package["name"])
            pins.add(match[1])
    if len(pins) != 1:
        raise ValueError("Expected one identical pinned Ferrule revision across Rust lockfiles")
    return locks, pins.pop(), packages


def prepare_rust(root):
    """Create a disposable build tree that uses the full Ferrule checkout (including ../runtime)."""
    braid = root / "braid"
    locks, pin, packages = rust_inputs(braid)
    ferrule = root / "ferrule"
    paths = {}
    for directory in ("crates", "vendor"):
        for manifest in (ferrule / directory).glob("*/Cargo.toml"):
            name = tomllib.loads(manifest.read_text(encoding="utf-8"))["package"]["name"]
            if name in packages:
                if name in paths:
                    raise ValueError(f"Ambiguous Ferrule crate: {name}")
                paths[name] = manifest.parent
    if paths.keys() != packages:
        raise ValueError(f"Missing Ferrule crate paths: {packages - paths.keys()}")
    target = root / "offline-engines"
    shutil.copytree(braid / "engines", target, symlinks=True)
    git_ref = f'git = "{FERRULE}", rev = "{pin}"'
    for manifest in target.rglob("Cargo.toml"):
        lines = manifest.read_text(encoding="utf-8").splitlines(keepends=True)
        for i, line in enumerate(lines):
            if git_ref in line:
                alias, dep = next(iter(tomllib.loads(line).items()))
                path = os.path.relpath(paths[dep.get("package", alias)], manifest.parent)
                lines[i] = line.replace(git_ref, 'path = ' + json.dumps(Path(path).as_posix()))
        manifest.write_text("".join(lines), encoding="utf-8")
    source = f'source = "git+{FERRULE}?rev={pin}#{pin}"\n'
    manifests = []
    for original in locks:
        lock = target / original.relative_to(braid / "engines")
        lock.write_text(lock.read_text(encoding="utf-8").replace(source, ""), encoding="utf-8")
        manifests.append(lock.with_name("Cargo.toml").relative_to(root).as_posix())
    return manifests


def collect_rust(root, toolchain):
    manifests = prepare_rust(root)
    cargo = ["cargo", "+" + toolchain]
    sync = [arg for path in manifests[1:] for arg in ("--sync", path)]
    config = run(*cargo, "vendor", "--locked", "--versioned-dirs", "--manifest-path", manifests[0],
                 *sync, "cargo-vendor", cwd=root, stdout=subprocess.PIPE, text=True).stdout
    (root / ".cargo").mkdir()
    (root / ".cargo/config.toml").write_text(config, encoding="utf-8")
    with tempfile.TemporaryDirectory() as empty_cache:
        for manifest in manifests:
            run(*cargo, "metadata", "--format-version=1", "--frozen", "--manifest-path", manifest,
                cwd=root, env={**os.environ, "CARGO_HOME": empty_cache}, stdout=subprocess.DEVNULL)
    shutil.rmtree(root / "offline-engines")  # Regenerated from the exact source trees by the recipient.
    sysroot = Path(run("rustc", "+" + toolchain, "--print", "sysroot", stdout=subprocess.PIPE,
                       text=True).stdout.strip())
    shutil.copytree(sysroot / "lib/rustlib/src/rust/library", root / "rust-library", symlinks=True)
    shutil.copyfile(sysroot / "share/doc/rust/COPYRIGHT-library.html", root / "rust-library/COPYRIGHT.html")


def write_archive(root, archive):
    def normalize(member):
        member.uid = member.gid = member.mtime = 0
        member.uname = member.gname = ""
        member.mode = 0o755 if member.isdir() or member.mode & 0o111 else 0o644
        return member

    with archive.open("xb") as dest, gzip.GzipFile(filename="", fileobj=dest, mode="wb",
                                                  compresslevel=5, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w|") as tar:
            tar.add(root, arcname=archive.name.removesuffix(".tar.gz"), filter=normalize)
    if not 0 < archive.stat().st_size <= MAX_BYTES:
        raise ValueError("Compressed source exceeds single-asset budget; review coverage and storage")


def build(version, commit, output):
    from package_sources import collect  # Publisher-only dependencies are not needed for verification.
    name = identities(version, commit)
    repository = Path.cwd()
    with tempfile.TemporaryDirectory() as scratch:
        temp = Path(scratch)
        root = temp / "source"
        root.mkdir()
        braid, ferrule = root / "braid", root / "ferrule"
        git_tree(repository, commit, braid)
        _, pin, _ = rust_inputs(braid)
        remote = temp / "upstream"
        run("git", "init", "--quiet", "--bare", remote)
        run("git", "-C", remote, "fetch", "--no-tags", "--depth=1", FERRULE, pin)
        git_tree(remote, pin, ferrule)
        if not (braid / "LICENSE").is_file() or not (ferrule / "LICENSE").is_file():
            raise ValueError("Missing project license")
        toolchain = tomllib.loads((braid / "engines/ferrule-host/rust-toolchain.toml").read_text())["toolchain"]["channel"]
        collect_rust(root, toolchain)
        collect(braid, root / "package-sources")
        shutil.copyfile(braid / "tools/release/SOURCE-README.md", root / "README.md")
        identity = {"version": version, "braidCommit": commit, "ferruleCommit": pin, "rustToolchain": toolchain}
        (root / "BUILD.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="ascii")
        output.mkdir(parents=True, exist_ok=True)
        asset = output / name
        write_archive(root, asset)
        metadata = {**identity, "archive": name, "sha256": sha256(asset), "bytes": asset.stat().st_size}
        (output / "source.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="ascii")


def verify(version, commit, output):
    name = identities(version, commit)
    record = json.loads((output / "source.json").read_bytes())
    archive = output / name
    if (record["version"] != version or record["braidCommit"] != commit or record["archive"] != name
            or not re.fullmatch(SHA, record["ferruleCommit"]) or not 0 < record["bytes"] <= MAX_BYTES
            or archive.stat().st_size != record["bytes"] or sha256(archive) != record["sha256"]):
        raise ValueError("Source release identity, length or digest mismatch")
    return record


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for action in ("build", "verify"):
        cmd = sub.add_parser(action)
        cmd.add_argument("--version", required=True)
        cmd.add_argument("--commit", required=True)
        cmd.add_argument("--output", required=True, type=Path)
    sub.add_parser("prepare-rust", help="In an extracted release: prepare the offline Rust build tree")
    args = parser.parse_args()
    if args.command == "prepare-rust":
        prepare_rust(Path.cwd())
    else:
        (build if args.command == "build" else verify)(args.version, args.commit, args.output)
