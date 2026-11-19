# Product release manifest v1

The `.github/workflows/release-images.yml` publisher attaches `release.json`
and its keyless Cosign `sign-blob` bundle `release.json.bundle` to the release.
From the repository root, use
Python 3.9 or later (standard library only). Pass the **registry references returned by
the publish steps**, including the repository name and pushed manifest digest:

```sh
python3 deploy/release/manifest.py generate \
  --version "$RELEASE_TAG" --source-commit "$SOURCE_COMMIT" \
  --server "$SERVER_REF" --edge "$EDGE_REF" --worker "$WORKER_REF" \
  --mapping-endpoint "$ENDPOINT_REF" --ferrule-service "$FERRULE_REF" \
  --output release.json
python3 deploy/release/manifest.py validate release.json \
  --expect-version "$RELEASE_TAG" --expect-source-commit "$SOURCE_COMMIT"
cosign sign-blob --yes --bundle release.json.bundle release.json
```

The platform is fixed at `linux/amd64`. If the publisher measured the Ferrule
pins, pass **both** `--compiler-identity compiler-identity.json` and
`--runtime-identity runtime-identity.json` to `generate`. These files are the
JSON stdout of the exact published Ferrule service image's `identity` and
`runtime-identity` commands. Without measurements, omit the `ferrule` object;
downstream operators must measure pins for the capabilities they enable.

[Schema](release-manifest.schema.json) · [Example](release-manifest.example.json)
· [Consumer guide](../../docs/consuming-a-release.md)

Run the offline contract tests:

```sh
python3 -m unittest discover -s deploy/release -p 'test_*.py'
```
