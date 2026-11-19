# Building from a braid source release

`BUILD.json` identifies the release, both Git commits and the Rust toolchain.
`braid/` and `ferrule/` contain the tracked files from those commits, without
Git history or local configuration. Start with their README and LICENSE files.
Keep third-party notices with the respective components.

## Rust components, offline

Use Linux x86-64, Python 3.12+, a C/C++ build environment and the Rust toolchain
named in `BUILD.json` (including the `wasm32-unknown-unknown` target for the
browser editor). From the directory containing this README:

```sh
toolchain=$(python3 -c 'import json; print(json.load(open("BUILD.json"))["rustToolchain"])')
python3 braid/tools/release/source_archive.py prepare-rust
cargo +"$toolchain" build --frozen --release --manifest-path offline-engines/ferrule-host/Cargo.toml
cargo +"$toolchain" build --frozen --release --manifest-path offline-engines/compile-sandbox/Cargo.toml
cargo +"$toolchain" build --frozen --release --target wasm32-unknown-unknown \
  --manifest-path offline-engines/ferrule-edit-wasm/Cargo.toml
```

`prepare-rust` creates a new `offline-engines/` without changing the original
manifests or locks. It points their Git dependencies at the complete local
Ferrule tree: some crates include C# sources outside their crate directory.
The remaining registry dependencies come from `cargo-vendor/`; Cargo's source
replacement config is `.cargo/config.toml`. `rust-library/` contains the pinned
standard library source. For the final JS/Wasm glue, see `braid/tools/edit-core/build.py`
and `package-assets.py` (the specified wasm-bindgen CLI is a build prerequisite).

## Product images and deployment

The release recipes live in `braid/.github/workflows/release-images.yml`,
`braid/deploy/container/Dockerfile`, `braid/engines/{ferrule-host,ferrule-service}/Dockerfile`
and `braid/tools/release/`. Run the Dockerfile builds from their documented contexts.
For example, from `braid/`, `docker build -f deploy/container/Dockerfile --target braid .`
builds the server with its web UI. A distributor can set the `BRAID_VERSION` and
`BRAID_SOURCE_ARCHIVE` build arguments to its exact version and public HTTPS source
download. See `braid/docs/runtime-packaging.md` for installation and
`braid/docs/consuming-a-release.md` for verification. `build_image.py` is a
**publisher** that pushes an image, not a local rebuild command.

`package-sources/index.json` maps browser and .NET dependency versions to
the included source files/archives, integrity checks and upstream revisions.
Read each entry's scope: an Angular source map can contain original TypeScript;
a NuGet package's DLL or a lockfile by itself is not its source. Large upstream
projects may be represented by their project subtree, without shared build
infrastructure; some packages have reconstructed rather than complete original
build inputs. The index identifies those cases and available upstream locations.
Crate and upstream archives retain their own license texts and notices where
present; see also the project's `engines/ferrule-host/third-party/`.

The standard container recipes still acquire pinned toolchains, NuGet and Bun
packages, base images and OS packages. This is not a registry mirror, an
offline rebuild of all five images, or a promise of byte-identical binaries.
Independent programs and runtime layers (including Caddy, Docker CLI, .NET/
Roslyn and Linux libraries) have their own redistribution terms. A distributor
must review the actual image contents, upstream notices and any missing
corresponding sources/build inputs before shipping; supplement the release
where needed. Do not infer license compliance from successful collection.
