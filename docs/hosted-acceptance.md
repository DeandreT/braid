# Hosted-shape acceptance

`deploy/compose/acceptance-test.sh` is the local acceptance of the packaged Session
stack: real sign-in, the main workflows through the Angular application, and recovery
across a server restart and Ferrule service scaling. It qualifies the hosted shape on
one machine; it does not provision Azure or use a live Entra tenant. Tracking work is
[#243](https://github.com/DeandreT/weft/issues/243).

## What runs

The script builds the `weft`, `edge` and `weft-worker` targets of
`deploy/container/Dockerfile`, the native worker from `engines/ferrule-host` and the
[Ferrule service](ferrule-service.md) image, each with a unique tag, then starts these
compose files from new volumes:

| File | Role |
| --- | --- |
| `compose.yml` | PostgreSQL, the migration, storage and key-ring steps, and the Session server behind its edge. |
| `compose.ferrule-service.yml` | Native upload and report derivation through two or more service replicas. |
| `compose.features.yml` | Chat, previews, regression cases and runs, and builds on the server, plus the `runs` Weft.Worker (see [runtime packaging](runtime-packaging.md)). |
| `compose.acceptance.yml` | Test-only plumbing, described below. Never part of a deployment. |

No container receives the Docker socket; the script checks every container's mounts.

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
  single-use codes, then returns an RS256 ID token. The server resolves
  `login.microsoftonline.com` to the issuer through a network alias and trusts only the
  run's test certificate authority, delivered as `SSL_CERT_FILE`. The Session host's
  endpoint, metadata, key, token and binding checks run unchanged.
- **Ingress.** A TLS relay terminates HTTPS for the public host `weft.example.test`
  in front of the edge, as the platform ingress does.
- **Chat model.** An Ollama container on an internal network serves the chat model from an
  existing model cache volume, reached through the server's loopback relay by an
  `ollamaChatNdjson` profile marked `loopbackFixture`.

The harness then binds one external identity through the owner commands of
`Weft.DatabaseTool`: it creates the organization and an admitted principal
(`provision-development-identity`), binds the issuer's subject
(`bind-external-identity`) and grants project creation
(`set-project-creation-capability`). No project role is granted; creating the project
makes the user its administrator.

## Browser flows

`apps/web/tests/hosted-acceptance.spec.ts` drives one signed-in Chromium session. The
browser resolves only the public host and `login.microsoftonline.com`, each to the run's
listener, and admits only the run's two TLS keys.

1. Sign in through the session panel and the issuer's page; the status reports an
   authenticated Session.
2. Create a project.
3. Upload a native mapping (the preview-orders fixture with JSON documents declared on
   every input and output); its registration carries the Blob profile identity.
4. Generate and read the explanation and validation reports.
5. Ask chat a question that calls `search_mapping`, and require a completed answer with
   verified citations.
6. Run a preview with sample JSON through the `runs` worker and the service, and compare
   the output with independently authored bytes.
7. Append a second revision that renames one output field, generate its explanation and
   compare the two revisions.
8. Save a regression case with an independently authored baseline and run it to a
   passing comparison.
9. Register a build environment, verify the regression evidence, approve and request a
    build. The ordinary worker compiles through the pinned Ferrule service, and the
    run must finish with a nonempty artifact on its first attempt.
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

On a slow host the application can report a read as timed out or unavailable, or discard
an editor draft when a background session check fails. The spec then uses only the
application's own recovery controls (Retry, read again, resend the original request, try
derivation again after the server's retry delay), a bounded number of times, and records
each use; the summary line prints them with the number of chat attempts. A small local
model does not always call a tool; each chat attempt is a new conversation.

## Not composed in the Session profile

- Qualification and release activation do not follow automatically from a completed
  build; they remain separate explicit operations.
- Native examples and projection fixtures, which are Development features.
- Release status, qualification and activation, which the browser API does not provide in
  any profile; the In use and Change history views state that limitation.

## Running it

For explicit provider emulation, set `WEFT_ACCEPTANCE_MODEL_FIXTURE=true`. The
test-only Node service implements the Ollama wire protocol and requests the real
`search_mapping` tool; the application still retrieves and verifies its own
evidence and citations. No model cache or Ollama image is used in this mode.
The receipt records `modelFixture: true`. This qualifies application orchestration
and recovery against an emulated provider, not live-model behavior. The default
remains the real cached Ollama model described below.

Prerequisites:

- Docker with BuildKit (`docker buildx`), which `engines/ferrule-host` requires.
- `npm ci` in the repository and `WEFT_CHROMIUM_EXECUTABLE` naming a Chromium build.
- A Docker volume holding an Ollama model cache with a chat model that supports tool calls,
  by default `weft-ollama-models` with `qwen2.5:1.5b`:

  ```sh
  docker run -d --name weft-ollama -v weft-ollama-models:/root/.ollama docker.io/ollama/ollama
  docker exec weft-ollama ollama pull qwen2.5:1.5b
  docker rm -f weft-ollama
  ```

```sh
deploy/compose/acceptance-test.sh
```

| Setting | Effect |
| --- | --- |
| `WEFT_ACCEPTANCE_BROWSER_WRAPPER` | Command prefix for the browser run, for example `xvfb-run -a`. |
| `WEFT_ACCEPTANCE_MODEL_FIXTURE` | Explicit `true` selects the deterministic provider emulator; the default is `false`. |
| `WEFT_ACCEPTANCE_OLLAMA_VOLUME`, `WEFT_ACCEPTANCE_CHAT_MODEL` | The model cache volume and model name. |
| `WEFT_ACCEPTANCE_NODE_IMAGE` | The Node image for the issuer and relays (pinned by digest by default). |
| `WEFT_FERRULE_NATIVE_IMAGE` | A tag or registry digest of an `engines/ferrule-host` build to use instead of building it. |
| `WEFT_IMAGE`, `WEFT_EDGE_IMAGE`, `WEFT_WORKER_IMAGE`, `WEFT_FERRULE_SERVICE_IMAGE` | Prebuilt images to use instead of building them; they are not removed. |
| `WEFT_ACCEPTANCE_LOG_DIRECTORY` | On failure, keep the service logs and the browser failure context there. |
| `WEFT_ACCEPTANCE_HOLD` | Debugging aid: stop after provisioning, write the run's environment to a private file and wait for a release file before cleaning up. |

Generated passwords, keys and certificates stay in a private temporary directory and are
never printed. The script removes every container, network, volume and image tag it
created, falling back to the compose project's labels when compose cannot render the
stack; a failed cleanup or a remaining owned object fails the run. The model cache volume
and pulled base images are left in place.
