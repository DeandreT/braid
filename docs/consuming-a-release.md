# Consume a Braid product release

Use the signed `release.json` asset from the selected product release to pin
the containers in your deployment. All five image references describe the same
`linux/amd64` release. The manifest identifies what to pull; PostgreSQL, persistent
storage, TLS ingress, identity registration, credentials, network policy and
feature choices belong to the consuming environment.

## Select and pin the images

1. Select the exact `vX.Y.Z` tag from the trusted product repository and record
   the full commit that tag resolves to. Download both `release.json` and
   `release.json.bundle` from that release. Set `REPO` to the trusted `owner/repo`
   (matching its case), `TAG` to the selected tag, and `COMMIT` to the full commit.
   Verify the exact downloaded bytes against the **tag-specific publisher**:

   ```sh
   cosign verify-blob --bundle release.json.bundle \
     --certificate-identity "https://github.com/${REPO}/.github/workflows/release-images.yml@refs/tags/${TAG}" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     release.json
   ```

   Stop if verification fails. Keep both assets together, without reformatting
   the JSON. Use the validator and schema from the trusted revision. The
   [example manifest](../deploy/release/release-manifest.example.json) contains
   illustrative digests only.
2. Validate with Python 3.9 or later before changing the deployment:

   ```sh
   python3 deploy/release/manifest.py validate release.json \
     --expect-version "$TAG" --expect-source-commit "$COMMIT"
   ```

   Validation is offline. It checks the [v1 schema](../deploy/release/release-manifest.schema.json)
   and Ferrule build identity consistency; it does not authenticate the publisher
   or contact the registry. Check that the registry and repositories belong to
   the expected publisher before pulling. Keep the original manifest with the
   deployment record.
3. Set each workload's image to its **complete** `images` value, including
   `@sha256:…`. For example, to pull the pinned server using `jq`:

   ```sh
   docker pull --platform linux/amd64 "$(jq -er '.images.server' release.json)"
   ```

   A version tag, `latest`, a local image ID, or a rebuild from the recorded
   commit is not a substitute for the published registry digest. When mirroring
   images, verify the manifest digest at the destination before promoting it.

| `images` key | Compose image variable | Runtime use |
| --- | --- | --- |
| `server` | `BRAID_IMAGE` | Session API and web UI (`server`); also the one-shot tools below. |
| `edge` | `BRAID_EDGE_IMAGE` | Caddy next to the server in the **same network namespace**. Route TLS-terminated traffic to port 8080; the server listens on loopback port 8081. |
| `worker` | `BRAID_WORKER_IMAGE` | Separate `worker` process for enabled preview, regression and build work (`runs` in the features overlay). |
| `mappingEndpoint` | `BRAID_ENDPOINT_IMAGE` | Optional `endpoint` host for one registered target and its `release` operator job. Use the service-backed runtime for this image. |
| `ferruleService` | `BRAID_FERRULE_SERVICE_IMAGE` | Private Ferrule service. Clients select `BRAID_FERRULE_RUNNER=service` and authenticate by pinned mutual TLS. |

For the Compose settings, certificates, overlays and volume ownership, follow
[Runtime packaging](runtime-packaging.md#hosted-shape-local-stack) at the selected
commit. Set the image variables above before starting that stack so it pulls the
release instead of falling back to local build tags.

## Configure Ferrule protocol identities

The digest in `images.ferruleService` selects a registry object. The `ferrule`
fields, when present, identify components **inside** that image:

| Field | What it identifies | Use |
| --- | --- | --- |
| `native` | SHA-256 of `/ferrule-host` | `BRAID_FERRULE_SERVICE_IDENTITY` in Compose; passed to service clients as `BRAID_FERRULE_WORKER_IMAGE`. Despite its name, this client setting is not a container pull reference in service mode. |
| `toolchain` | Measured compilation toolchain | `BRAID_FERRULE_COMPILE_TOOLCHAIN` for build-enabled hosts and the service-backed endpoint/release tool. |
| `build` | Combined native and toolchain identity | Compilation provenance. Clients derive it from the two pins; it is not the toolchain pin. |
| `runtime` | Measured compiled-execution installation | `BRAID_COMPILED_RUNTIME_IMAGE` for a service-backed endpoint and its qualification job. |

If `ferrule` is absent, measure the pins from the **pinned service image** before
enabling features that require them. The same commands let you check published
measurements on a deployment host. With `FERRULE_REF` set to the full
`images.ferruleService` value:

```sh
set -e
for command in identity runtime-identity; do
  docker run --rm --platform linux/amd64 --network none --read-only \
    --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp:size=256m,mode=1777 --memory 3g --pids-limit 256 \
    "$FERRULE_REF" "$command" > "$command.json"
done
```

`identity` emits `native`, `toolchain` and `build`; `runtime-identity` emits
`native` and `runtime`. Check that both native measurements agree and that the
values match any published pins. Stop if measurement fails or a pin differs.
Use a host that supports the [Ferrule sandbox](ferrule-service.md#request-isolation);
do not weaken it to make a measurement succeed. Certificate pins are separate
deployment inputs. See [compiled execution](compiled-service-runtime.md#explicit-admission)
for the independent execution profile.

## Jobs and rollout order

1. **Provision first.** Prepare the database and its restricted logins, persistent
   storage and durable Session key volume. Use the selected release's documented
   role provisioning for a new database; keep owner/migrator credentials out of
   long-running workloads.
2. **Run one-shot jobs.** From `images.server`, run `migrate` with the migrator
   login. For filesystem storage run `initialize-storage /var/lib/braid/storage/root`
   against the persistent storage volume; run `bootstrap-session-keys` against
   the durable key ring with its certificate. Require successful job exits before
   server startup. Use `database` for workspace, admission and identity provisioning
   after migration and before first sign-in. `storage`, `session-keys` and
   `maintenance` are separate operator commands in that same image.
3. **Start and verify.** Start the selected Ferrule profiles and the paired server
   and edge. Check `/api/v1/health/ready` through the edge; then check a signed-in
   operation for the intended workspace. Add the `worker` with its own credentials
   and feature settings if queued work is enabled. For a machine endpoint,
   use `database` from `images.server` for caller grants, and `release` from
   `images.mappingEndpoint` for qualification, target registration and activation.
   Use the same compiled runtime for qualification and invocations, then check
   endpoint readiness (`/v1/health/ready`) and an authorized invocation. See
   [mapping endpoints](mapping-endpoint.md#packaging).
4. **Promote or recover.** Promote the same reviewed manifest across environments.
   Before upgrading, retain the prior manifest, database backup, private objects,
   session key ring and decrypt certificates. Check migration compatibility before
   restoring earlier image pins: reverting containers does not reverse migrations.
   Product deployment and [mapping release activation/rollback](release-operations.md)
   are separate operations.
