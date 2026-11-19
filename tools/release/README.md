# Product image publisher

`.github/workflows/release-images.yml` accepts newly created, protected `vX.Y.Z` tags
on commits reachable from the default branch. Configure a tag ruleset that
restricts creation to release maintainers and prevents updates and deletions;
protect the default branch and configure deployment restrictions and reviewers
on the `release` environment. A tag is not itself evidence that CI passed: the
workflow runs the same CI qualification at the tagged commit before publishing.

By default, images are published under `ghcr.io/<lowercase-owner>/<repo>` with
suffixes `-edge`, `-worker`, `-mapping-endpoint` and `-ferrule-service` (no suffix
for the server). Grant this repository Actions write access to its packages.
Alternatively set the `release` environment variable `RELEASE_IMAGE_PREFIX`
to a qualified, lowercase repository prefix such as `registry.example/team/braid`,
with matching `RELEASE_REGISTRY_USERNAME` and `RELEASE_REGISTRY_PASSWORD`
environment secrets. The registry must retain OCI indexes, attached BuildKit
attestations and Cosign signatures; make the packages readable to consumers.
Keyless signing uses GitHub's OIDC issuer and public Sigstore services.

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
