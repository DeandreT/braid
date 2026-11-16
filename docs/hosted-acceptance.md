# Hosted-shape acceptance

`deploy/compose/acceptance-test.sh` is the local acceptance of the packaged Session
stack: real sign-in, the main workflows through the Angular application, and recovery
across a server restart and Ferrule service scaling. It runs on one machine with a
test-owned identity provider. For Azure, use the
[live qualification](azure-deployment.md#live-qualification) harness.

## What runs

The script builds the `braid`, `edge` and `braid-worker` targets of
`deploy/container/Dockerfile`, the native worker from `engines/ferrule-host` and the
[Ferrule service](ferrule-service.md) image, each with a unique tag, then starts these
compose files from new volumes:

| File | Role |
| --- | --- |
| `compose.yml` | PostgreSQL, the migration, storage and key-ring steps, and the Session server behind its edge. |
| `compose.ferrule-service.yml` | Native upload and report derivation through two or more service replicas. |
| `compose.features.yml` | Chat, previews, regression cases and runs, and build approval and submission on the server, plus the `runs` Braid.Worker that executes previews, regression runs and builds (see [runtime packaging](runtime-packaging.md)). |
| `compose.acceptance.yml` | Test-only plumbing, described below. Never part of a deployment. |
| `compose.model-fixture.yml` | Only with `BRAID_ACCEPTANCE_MODEL_FIXTURE=true`: the test-only provider emulator in place of the Ollama model (see [Running it](#running-it)). |

Before starting the stack, the script runs the service image's `identity` command with no
network, mounts or capabilities. It requires the measured native identity to equal the
image's recorded build identity, pins the measured toolchain identity as
`BRAID_FERRULE_COMPILE_TOOLCHAIN` on the server and the `runs` worker, and keeps the
combined identity for the build checks below. No container receives the Docker socket; the
script checks every container's mounts.

`compose.acceptance.yml` supplies what a hosted environment provides by other means:

- **Blob private storage.** The server, the `runs` worker and the storage tools select
  [Azure Blob storage](private-storage-blob.md) against an Azurite container on an
  internal network. A one-shot step creates the private container, as a deployment template
  would for a real account, and `storage-init` runs `storage initialize-blob`. The product
  admits the emulator's development account only on loopback HTTP, so each consumer
  reaches Azurite through a byte relay (`tools/hosted-acceptance/relay.mjs`) listening on
  loopback inside its own network namespace. A second `initialize-blob` refuses the
  initialized store, so restarts start only the server replica (`--no-deps`).
- **Sign-in.** `tools/hosted-acceptance/issuer.mjs` is a test-owned OpenID Connect issuer
  for the fixed Microsoft organizational endpoints of the run's tenant and client:
  discovery, signing keys, an authorization page with one sign-in button, and code
  redemption that requires the server's PS256 certificate assertion, PKCE (S256) and
  single-use codes, then returns an RS256 ID token carrying the run's pairwise subject and
  the user's object ID for the requested `openid profile` scope. The server resolves
  `login.microsoftonline.com` to the issuer through a network alias and trusts only the
  run's test certificate authority, delivered as `SSL_CERT_FILE`. The Session host's
  endpoint, metadata, key, token and binding checks run unchanged.
- **Ingress.** A TLS relay terminates HTTPS for the public host `braid.example.test`
  in front of the edge, as the platform ingress does.
- **Chat model.** An Ollama container on an internal network serves the chat model from an
  existing model cache volume, reached through the server's loopback relay by an
  `ollamaChatNdjson` profile marked `loopbackFixture`.

The harness then binds one external identity through the owner commands of
`Braid.DatabaseTool`: it creates the organization and an admitted principal
(`provision-development-identity`), binds the signed-in user's tenant and object ID
(`bind-entra-user`) and grants project creation
(`set-project-creation-capability`). No project role is granted; creating the project
makes the user its administrator.

## Browser flows

`apps/web/tests/hosted-acceptance.spec.ts` drives one signed-in Chromium session. The
browser resolves only the public host and `login.microsoftonline.com`, each to the run's
listener, and admits only the run's two TLS keys.

1. Sign in through the sign-in page and the issuer's page; the account menu reports
   an authenticated Session.
2. Create a project.
3. Upload a native mapping (the preview-orders fixture with JSON documents declared on
   every input and output); its registration carries the Blob profile identity.
4. Generate and read the explanation and validation reports.
5. Ask the workbench's workspace chat a question that calls `search_catalog`, and
   require a completed answer with a verified mapping citation.
6. Run a preview with sample JSON through the `runs` worker and the service, and compare
   the output with independently authored bytes.
7. Append a second revision that renames one output field, generate its explanation and
   compare the two revisions.
8. Save a regression case with an independently authored baseline and run it to a
   passing comparison.
9. Register a build environment, verify the regression evidence, approve and request a
    build. The approval must name the combined identity measured by the `identity` step.
    The ordinary worker compiles through the pinned Ferrule service. The spec waits for a
    terminal state and requires success within the run's three-attempt budget; a retry is
    designed behaviour and is recorded. The artifact must be nonempty and in the approval
    profile's format, the published private body must have the run's digest and length,
    and the panel must show the completed attempt and digest after a refresh.
10. With the `runs` worker stopped, queue three previews from three tabs. Scale the service
    to three replicas, stop the server replica while a chat turn is streaming, and start
    it again. The same browser session reopens the reports, conversations and queued
    work without a new sign-in; the interrupted turn settles once with no further model
    call. Restart the worker while the service scales to one replica: every queued
    preview completes exactly once, the regression case passes again, and the completed
    build retains the same artifact digest and byte count. The service returns to two replicas.

The script then requires that every sign-in redeemed its code without an issuer refusal,
that every service replica is healthy, and that the maintenance tool observes the
project's objects in the Blob store.

With the fixture provider, the standalone `/chat` page also checks every
combination of the model's payload policy and an explicit project payload-read
grant against the saved preview. The fixture requests a payload tool only when
it is actually offered, confirms the received UTF-8 byte count, and never logs
payload content. Only the allowed combination may produce a data-sharing marker
and audit row. The test revokes its own grant and checks that a subsequent turn
cannot share the input. The two acceptance model profiles differ only in that
payload policy; neither profile can grant project permission.

On a slow host the application can report a read as timed out or unavailable, or discard
an editor draft when a background session check fails. The spec then uses only the
application's own recovery controls (Retry, read again, resend the original request, try
derivation again after the server's retry delay), a bounded number of times, and records
each use; the summary line prints them with the number of chat attempts and the build's
completing attempt. A small local model does not always call a tool; each chat attempt is
a new conversation.

## Not composed in the Session profile

- Native examples and projection fixtures, which are Development features.
- Release status, qualification and activation, which the browser API does not provide in
  any profile and which never follow automatically from a completed build; the In use and
  Change history views state that limitation.

## Running it

For explicit provider emulation, set `BRAID_ACCEPTANCE_MODEL_FIXTURE=true`. The
test-only Node service implements the Ollama wire protocol and requests the real
workspace catalog or payload tool (and `search_mapping` for revision
chat); the application still retrieves and verifies its own
evidence and citations. No model cache or Ollama image is used in this mode.
The receipt records `modelFixture: true`. The default is the cached Ollama model
described below.

Prerequisites:

- Docker with BuildKit (`docker buildx`), which `engines/ferrule-host` requires.
- `npm ci` in the repository and `BRAID_CHROMIUM_EXECUTABLE` naming a Chromium build.
- A Docker volume holding an Ollama model cache with a chat model that supports tool calls,
  by default `braid-ollama-models` with `qwen2.5:1.5b`:

  ```sh
  docker run -d --name braid-ollama -v braid-ollama-models:/root/.ollama docker.io/ollama/ollama
  docker exec braid-ollama ollama pull qwen2.5:1.5b
  docker rm -f braid-ollama
  ```

```sh
deploy/compose/acceptance-test.sh
```

| Setting | Effect |
| --- | --- |
| `BRAID_ACCEPTANCE_BROWSER_WRAPPER` | Command prefix for the browser run, for example `xvfb-run -a`. |
| `BRAID_ACCEPTANCE_MODEL_FIXTURE` | Explicit `true` selects the deterministic provider emulator; the default is `false`. |
| `BRAID_ACCEPTANCE_OLLAMA_VOLUME`, `BRAID_ACCEPTANCE_CHAT_MODEL` | The model cache volume and model name. |
| `BRAID_ACCEPTANCE_NODE_IMAGE` | The Node image for the issuer, the relays and the model fixture (pinned by digest by default). |
| `BRAID_FERRULE_NATIVE_IMAGE` | A tag or registry digest of an `engines/ferrule-host` build to use instead of building it. |
| `BRAID_IMAGE`, `BRAID_EDGE_IMAGE`, `BRAID_WORKER_IMAGE`, `BRAID_FERRULE_SERVICE_IMAGE` | Prebuilt images to use instead of building them; they are not removed. |
| `BRAID_ACCEPTANCE_LOG_DIRECTORY` | On failure, keep the service logs and the browser failure context there. |
| `BRAID_ACCEPTANCE_HOLD` | Debugging aid: stop after provisioning, write the run's environment to a private file and wait for a release file before cleaning up. |
| `BRAID_ACCEPTANCE_STAGE` | An executable to run after the hosted checks pass and before cleanup, with the run's environment; its failure fails the run. The [mapping endpoint stage](#mapping-endpoint-stage) uses it. |
| `BRAID_TEST_SUBNET_RANGE` | The IPv4 range the run's networks take their explicit subnets from; see [Docker test networks](development.md#docker-test-networks). |

Generated passwords, keys and certificates stay in a private temporary directory and are
never printed. The script removes every container, network, volume and image tag it
created, falling back to the compose project's labels when compose cannot render the
stack; a failed cleanup or a remaining owned object fails the run. The model cache volume
and pulled base images are left in place.

## Mapping endpoint stage

`deploy/compose/endpoint-acceptance-test.sh` runs this acceptance with a follow-on stage for
the packaged [mapping endpoint](mapping-endpoint.md). It takes the same settings, plus
`BRAID_ENDPOINT_ACCEPTANCE_RUNTIME`: `service` (the default) executes through the run's Ferrule
service in the hosted shape, and `docker` uses the local Docker compiled runtime.
`BRAID_ENDPOINT_IMAGE` and, for `docker`, `BRAID_COMPILED_RUNTIME_IMAGE` (an image ID) select
prebuilt images instead of building them. Before the stack starts, it registers two machine
clients with the run's issuer, which then issues app-only v2 access tokens through a
client-credentials grant; one client's tokens expire after two seconds. Once the hosted checks
pass, the stage:

1. Prepares the compiled runtime. With `service`, it builds `braid-mapping-endpoint`, reads the
   execution identity from the service image's `runtime-identity` command, creates the
   endpoint's client certificate, and restarts the service replicas with the execute profile and
   that certificate admitted. With `docker`, it builds `braid-mapping-endpoint-local-docker`, and
   the compiled runtime from `engines/build-worker` with the run's native image. A reproducible
   build can share its image ID with other users of the same sources, so the stage registers a
   run-unique child image that differs only by a label. Its runtime containers inherit that
   label, and only containers carrying it are ever checked or removed.
2. Names the build the browser run completed. The build must name the Ferrule service's
   combined compiler identity, which must equal the identity measured from the service image.
3. Qualifies the build with `endpoint-release qualify-build`, which replays its saved regression
   case through the compiled runtime. It then registers a target, activates a release and grants
   one caller with `endpoint-grants`, each as the signed-in principal.
4. Starts `endpoint` from `compose.endpoint.yml`, the selected runtime override and the
   test-only `compose.endpoint-acceptance.yml`, which gives it the issuer's network alias and the run's
   certificate authority, as `compose.acceptance.yml` does for the server. The container must be
   healthy, run as UID 1654 with a read-only root filesystem, no capabilities and
   `no-new-privileges`, and print `BraidMappingEndpointReady`. With `service`, no container of the
   stack may mount the Docker socket.
5. Calls it with the regression case's inputs and parameters. Each completed call must name its
   release, build and artifact, and both outputs must equal independently authored bytes at the
   call's own clock. A retry under the same request ID must return identical bytes, marked as
   replayed, with no new ledger audit. A second activation and then a rollback to the first
   release must each serve the next call. Tokens for another audience and expired tokens are
   refused with `401`. After the grant is revoked, a new call and a replay are refused with `403`.
6. Stops the endpoint. It must exit 0. With `docker`, no compiled-runtime container may remain;
   with `service`, every service replica must still be healthy.

A `503` answer records nothing final, so the stage retries that request with the same ID a
bounded number of times and reports the count. Qualification is tried again with the same ID
only when it answered `unavailable` and left no runtime container. Any runtime container that
the stage has to remove itself, whether from a failed qualification, after the stop or during
cleanup, is printed with its state and times, and the run fails: a clean result needs a new run
(`deploy/compose/endpoint-settlement.sh`). Only with `docker` do the endpoint and
`endpoint-release` receive the Docker socket. The stage removes its containers and images, and
the hosted cleanup then requires that no owned object remains.
