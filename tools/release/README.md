# Product image publisher

`.github/workflows/release-images.yml` accepts newly created, protected `vX.Y.Z` tags
on commits reachable from the default branch. Configure a tag ruleset that
restricts creation to release maintainers and prevents updates and deletions;
protect the default branch and configure deployment restrictions and reviewers
on the `release` environment. A tag is not itself evidence that CI passed: the
workflow runs the same CI qualification at the tagged commit before publishing.
Also protect `source-v*` tags from movement/deletion while allowing the
publisher to create them. Enable immutable GitHub Releases before publishing.
Without that setting a versioned URL alone cannot prevent an administrator
from replacing assets. Keep an independently verified copy and maintain the
public downloads while distributing the binaries.

By default, images are published under `ghcr.io/<lowercase-owner>/<repo>` with
suffixes `-edge`, `-worker`, `-mapping-endpoint` and `-ferrule-service` (no suffix
for the server). Grant this repository Actions write access to its packages.
Alternatively set the `release` environment variable `RELEASE_IMAGE_PREFIX`
to a qualified, lowercase repository prefix such as `registry.example/team/braid`,
with matching `RELEASE_REGISTRY_USERNAME` and `RELEASE_REGISTRY_PASSWORD`
environment secrets. The registry must retain OCI indexes, attached BuildKit
attestations and Cosign signatures; make the packages readable to consumers.
Keyless signing uses GitHub's OIDC issuer and public Sigstore services.

Before any image push, a separate source job creates the public
`source-vX.Y.Z` release at the product commit. It publishes a `.tar.gz`,
`source.json` (commits, length and SHA-256) and its Cosign bundle.
Only after an anonymous download and signature, version, pin, size and digest
verification may the matrix start. Each role checks the public bytes again
before `--push`; its OCI image config binds their URL and SHA-256. On retry
the source job verifies the existing signed assets instead of overwriting
them. A missing asset, inaccessible repository, wrong tag, invalid signature
or digest mismatch stops publication. Investigate incomplete releases; never
replace a previously distributed version with different bytes.

The supported source publisher is the Linux x86-64 workflow runner (Ubuntu
24.04, Python 3.12+, Git, pinned Bun, rustup with the locked Rust toolchain
and `rust-src`, plus HTTPS access to upstream package and Git sources).
To build source assets locally from a committed revision, without publishing:

```sh
python3 tools/release/source_archive.py build --version "$TAG" --commit "$COMMIT" --output ./source-assets
python3 tools/release/source_archive.py verify --version "$TAG" --commit "$COMMIT" --output ./source-assets
```

Use an empty output directory and allow several GiB of free scratch space
(`TMPDIR` selects it). One gzip tar is limited to 1,900 MiB, below the per-file
release limit. Do not silently drop inputs to fit: investigate any failure or
change the distribution format if the release outgrows the limit. Review the
[archive instructions and coverage limits](SOURCE-README.md) against the
publishable components, including upstream layers; supplement missing required
sources/notices before approval. Source availability is a technical gate, not
a determination of legal completeness.

Each role independently signs and verifies the pushed index digest covering
the `linux/amd64` image and its SPDX and SLSA attestations. The release job
assembles their digest references, includes the Ferrule identity reports, and
signs the validated manifest bytes. The public release assets are
`release.json` and `release.json.bundle`. A failed matrix
can leave individual OCI images in the registry; a version tag on an image
is not a completed product release. Consume the
[signed release manifest](../../docs/consuming-a-release.md).

Run `python3 -m unittest discover -s tools/release -p 'test_*.py'` for the
offline checks; `build_image.py` itself performs real registry writes.
