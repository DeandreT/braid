#!/usr/bin/env python3
"""Build and package the pinned edit module (no native mapping data is processed)."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
TOOLCHAIN = "1.98.1"


def run(*command: str) -> None:
    subprocess.run(command, cwd=ROOT, check=True)


def install() -> None:
    if shutil.which("rustup") is None:
        with tempfile.TemporaryDirectory(prefix="weft-edit-toolchain-") as temporary:
            installer = Path(temporary) / "rustup-init"
            context = ssl.create_default_context()
            if os.path.isfile("/run/secrets/proxy_ca"):
                context.load_verify_locations("/run/secrets/proxy_ca")
            url = "https://static.rust-lang.org/rustup/archive/1.29.1/x86_64-unknown-linux-gnu/rustup-init"
            with urllib.request.urlopen(url, context=context, timeout=120) as response:
                installer.write_bytes(response.read())
            if hashlib.sha256(installer.read_bytes()).hexdigest() != "dda7234360b7f578ca8b0ddcb80145646fa61a67c1720a5abc7051b35c9fcb71":
                raise RuntimeError("The pinned rustup installer checksum does not match.")
            installer.chmod(0o700)
            run(str(installer), "--yes", "--no-modify-path", "--profile", "minimal", "--default-toolchain", TOOLCHAIN)
    run("rustup", "toolchain", "install", TOOLCHAIN, "--profile", "minimal")
    run("rustup", "target", "add", "wasm32-unknown-unknown", "--toolchain", TOOLCHAIN)
    version = subprocess.run(["wasm-bindgen", "--version"], capture_output=True, text=True) if shutil.which("wasm-bindgen") else None
    if version is None or version.stdout.strip() != "wasm-bindgen 0.2.129":
        run("cargo", "+" + TOOLCHAIN, "install", "wasm-bindgen-cli", "--version", "0.2.129", "--locked", "--force")


def remap_build_paths() -> None:
    """Replace the builder's directories in panic locations, so the shipped module and its integrity are host-independent."""
    home = Path.home()
    prefixes = {
        Path(os.environ.get("CARGO_HOME", home / ".cargo")).resolve(): "/cargo",
        Path(os.environ.get("RUSTUP_HOME", home / ".rustup")).resolve(): "/rustup",
        ROOT: "/weft",
    }
    flags = [f"--remap-path-prefix={source}={replacement}" for source, replacement in prefixes.items()]
    existing = os.environ.pop("CARGO_ENCODED_RUSTFLAGS", "")
    legacy = os.environ.pop("RUSTFLAGS", "")
    inherited = existing.split("\x1f") if existing else legacy.split()
    os.environ["CARGO_ENCODED_RUSTFLAGS"] = "\x1f".join([*inherited, *flags])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", action="store_true", help="Install the pinned toolchain into the caller's explicit CARGO_HOME/RUSTUP_HOME")
    parser.add_argument("--destination", type=Path, default=ROOT / "apps/web/public/edit")
    parser.add_argument("--manifest", type=Path, default=ROOT / "apps/web/src/edit/edit-assets.generated.ts")
    args = parser.parse_args()
    if args.install:
        install()
    version = subprocess.run(["wasm-bindgen", "--version"], capture_output=True, text=True, check=True)
    if version.stdout.strip() != "wasm-bindgen 0.2.129":
        raise RuntimeError("The edit module requires wasm-bindgen 0.2.129.")
    target = Path(os.environ.get("CARGO_TARGET_DIR", ROOT / "target/edit-wasm")).resolve()
    os.environ["CARGO_TARGET_DIR"] = str(target)
    os.environ["CARGO_BUILD_JOBS"] = "2"
    remap_build_paths()
    run("cargo", "+" + TOOLCHAIN, "build", "--manifest-path", "engines/ferrule-edit-wasm/Cargo.toml", "--release", "--target", "wasm32-unknown-unknown", "--locked")
    with tempfile.TemporaryDirectory(prefix="weft-edit-bindgen-") as temporary:
        run("wasm-bindgen", "--target", "web", "--out-dir", temporary, str(target / "wasm32-unknown-unknown/release/weft_ferrule_edit_wasm.wasm"))
        run(sys.executable, "tools/edit-core/package-assets.py", temporary, str(args.destination), str(args.manifest))
