# Explicit hosted sessions

## Set up a Session host

For packaged evaluation, start with the [Compose Session stack and Ferrule service](runtime-packaging.md#start-with-the-compose-session-stack).
This page explains the host's settings and safety boundaries; it does not provision
a deployment. [Azure](azure-deployment.md) is optional and still needs platform
qualification.

1. Prepare the database and restricted roles using the
   [database upgrade index](runtime-packaging.md#database-upgrade-index). Use the
   separate owner tools for the organization, principal, admission, project
   memberships and [external identity binding](external-identity-bindings.md).
2. Prepare a persistent private session key ring and its protecting certificate.
   Use the packaged [key bootstrap command](runtime-packaging.md#entrypoint-commands)
   only for first initialization. Retain the same realm, ring and decrypt
   certificates on restart; the server opens an existing ring, never bootstraps it.
3. Configure HTTPS ingress or direct HTTPS, the Entra web registration and the
   protected files in the settings table below. Keep owner/migrator credentials
   out of the web process.
4. Choose features explicitly under [Feature composition](#feature-composition).
   Deploy any required Ferrule service and separate `Weft.Worker` yourself; a
   feature flag does not create services, roles or grants.

Use [Restart, rotation and shutdown](#restart-rotation-and-shutdown) before an
upgrade or recovery. One API instance is supported; scaling Ferrule replicas does
not permit API scale-out.

## Choose the authentication mode

The Server selects `disabled`, `development`, or `session` with `WEFT_CATALOG_AUTH_MODE`, composing the browser-session, Entra, authentication HTTP and catalog modules in one host. It does not create database objects, external bindings, principals, organization admission, project memberships, keys, certificates, or an Entra app.

Absent mode preserves the old choice: catalog off means disabled; explicit catalog enablement uses the existing Development profile. An explicit disabled mode requires `WEFT_CATALOG_ENABLED=false`; development and session require `true`. Values and booleans are case-sensitive. Session never falls back to another mode.

Disabled and Development use a loopback HTTP listener. Disabled status is noncredential JSON with only version and mode; it creates no session, Entra or identity database resources. Enabled Development retains its existing identity/origin/environment checks and token contract. Its concrete origin must match the configured listener. `DevelopmentListenAddress.Parse` still accepts port zero. Only Disabled status may defer its origin: one actual nonzero bound loopback address is admitted after Kestrel starts, before application startup completes. Before that publication the status endpoint cannot return success. Neither Host nor forwarding headers select this origin.

Both pipelines install the same [browser security headers](browser-security.md)
before admission, static files and routing, including in packaged images. The app
shell and ordinary static assets use the strict same-origin CSP; authentication
error documents retain their stricter policy. Existing shipped `/edit/` files
use the separate, path-confined dedicated-worker policy: only that worker may
compile Wasm, with network access and nested workers denied. Missing editor
paths return empty 404 responses under the shell policy. The shell never enables
inline scripts, `unsafe-eval` or `wasm-unsafe-eval`.

## Configure Session mode

Session requires the following explicit configuration. Deliver secrets through protected files, not command-line arguments or logged configuration dumps.

| Setting                                                                                                                                                      | Required value                                                                                                                                                                                                                                                                                 |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WEFT_CATALOG_AUTH_MODE` / `WEFT_CATALOG_ENABLED`                                                                                                            | `session` / `true`                                                                                                                                                                                                                                                                             |
| `WEFT_PUBLIC_ORIGIN`                                                                                                                                         | Canonical HTTPS root origin accepted by the browser-session module; no path, userinfo, query or fragment.                                                                                                                                                                                      |
| `WEFT_HOST_TRANSPORT`                                                                                                                                        | `directHttps` or `trustedProxyOneHop`.                                                                                                                                                                                                                                                         |
| `WEFT_LISTEN_ADDR`                                                                                                                                           | One canonical literal IP endpoint with a nonzero port; no wildcard or extra listener. Direct HTTPS port equals the public origin port; proxy backend is loopback HTTP.                                                                                                                         |
| `WEFT_KNOWN_PROXY_IP`                                                                                                                                        | Proxy mode only: exactly one canonical literal loopback peer IP.                                                                                                                                                                                                                               |
| `WEFT_SESSION_ORGANIZATION_ID`                                                                                                                               | One canonical nonempty lowercase-D UUID.                                                                                                                                                                                                                                                       |
| `WEFT_SESSION_APPLICATION_NAME` / `WEFT_SESSION_KEY_DIRECTORY`                                                                                               | Stable application discriminator and existing canonical absolute private key-ring directory.                                                                                                                                                                                                   |
| `WEFT_SESSION_KEY_CERTIFICATE_FILE` / `WEFT_SESSION_KEY_PRIVATE_KEY_FILE`                                                                                    | Current usable RSA PEM certificate and matching key for durable key protection.                                                                                                                                                                                                                |
| `WEFT_SESSION_DECRYPT_CERTIFICATES_FILE`                                                                                                                     | Optional private JSON array of at most eight exact `{ "certificateFile": "...", "privateKeyFile": "..." }` pairs. Each pair is RSA PEM with a usable private key; historical certificate expiry is allowed for decryption. Absence is an empty overlap set.                                    |
| `WEFT_SESSION_READER_CONNECTION_STRING_FILE` / `WEFT_SESSION_WRITER_CONNECTION_STRING_FILE` / `WEFT_IDENTITY_READER_CONNECTION_STRING_FILE`                  | Three separately delivered private UTF-8 files with explicit nonempty usernames and passwords for the existing restricted reader, writer and external-identity reader roles. No owner/migration/runtime provisioner or ambient credential fallback.                                            |
| `WEFT_ACCOUNT_DISPLAY_CONNECTION_STRING_FILE`                                                                                                                | Optional separate protected connection for `weft_account_display`; absent means null account labels. Plain credentials or a configured invalid delivery are refused. See [Account display names](account-display-name.md).                                                                     |
| `WEFT_ENTRA_TENANT_ID` / `WEFT_ENTRA_CLIENT_ID`                                                                                                              | The fixed tenant/client UUIDs accepted by the Entra module.                                                                                                                                                                                                                                    |
| `WEFT_ENTRA_CLIENT_CERTIFICATE_FILE` / `WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE`                                                                                  | Separate usable RSA PEM client certificate/key. No client-secret or ambient-credential path.                                                                                                                                                                                                   |
| `WEFT_API_BEARER_ENABLED`, `WEFT_API_BEARER_AUDIENCE`, `WEFT_API_BEARER_SCOPE`, `WEFT_API_BEARER_CLIENT_IDS`, `WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE` | Optional, all or none: [bearer access](api-authentication.md#configuration) for signed-in users' public clients, with its private audit-writer credential. Any partial set refuses startup.                                                                                                    |
| `WEFT_TLS_CERTIFICATE_FILE` / `WEFT_TLS_PRIVATE_KEY_FILE`                                                                                                    | Direct HTTPS only: current matching server PEM certificate/key, with SAN matching the public host and server-auth EKU when EKU is present. Put the leaf first; append its public intermediate/issuer certificates to the same PEM when required by clients. Proxy mode rejects these settings. |

## Feature composition

Each business feature stays off until its own flag is `true`. The table lists what each profile composes; "Session `_FILE`" means Session admits those credentials only through the `_FILE` form of each connection-string setting, as protected private files (below).

| Feature                                       | Settings                                                                                                  | Disabled         | Development                                                                                                       | Session                                                                       |
| --------------------------------------------- | --------------------------------------------------------------------------------------------------------- | ---------------- | ----------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Health, static application, unknown-API 404   | always                                                                                                    | yes              | yes                                                                                                               | yes                                                                           |
| Authentication status                         | `WEFT_CATALOG_AUTH_MODE`                                                                                  | mode-only status | Development identity                                                                                              | Entra sign-in and browser sessions                                            |
| Synthetic projection fixtures                 | `DOTNET_ENVIRONMENT=Development`                                                                          | yes              | yes                                                                                                               | no                                                                            |
| OpenAPI document                              | `DOTNET_ENVIRONMENT=Development`                                                                          | yes              | yes                                                                                                               | no                                                                            |
| Catalog reads                                 | `WEFT_CATALOG_ENABLED`, `WEFT_CATALOG_CONNECTION_STRING`                                                  | no               | yes                                                                                                               | yes                                                                           |
| Metadata edits, project creation              | `WEFT_METADATA_EDITS_ENABLED`, `WEFT_PROJECT_CREATION_ENABLED` and their connection strings               | no               | yes                                                                                                               | yes                                                                           |
| Retained report reads and revision comparison | `WEFT_REVISION_REPORTS_ENABLED`, `WEFT_POSTGRES_REPORT_CONNECTION_STRING`, private storage                | no               | yes                                                                                                               | yes                                                                           |
| Native upload and report derivation           | `WEFT_REVISION_INGESTION_ENABLED`, `WEFT_REVISION_REPORT_DERIVATION_ENABLED` and their connection strings | no               | yes; native work needs an admitted Development runner and `WEFT_FERRULE_WORKER_IMAGE` | yes, through the Ferrule service, dynamic sessions or the separate inspection worker; startup refuses missing runner settings |
| MFD import and export                         | `WEFT_MFD_INTERCHANGE_ENABLED`, native ingestion and retained report-read storage                         | no               | separate Docker or Ferrule-service `interchange` profile                                                          | Ferrule service or dynamic sessions; not the inspection-only worker           |
| Chat                                          | `WEFT_CHAT_ENABLED`, `WEFT_CHAT_PROFILES_FILE`, chat reader/writer                                        | no               | connection strings                                                                                                | Session `_FILE`                                                               |
| Previews (submission, reads, cancel)          | `WEFT_PREVIEWS_ENABLED`, preview reader/writer                                                            | no               | connection strings                                                                                                | Session `_FILE`                                                               |
| Regression cases and runs                     | `WEFT_REGRESSIONS_ENABLED`, `WEFT_FERRULE_WORKER_IMAGE`, four regression logins                           | no               | connection strings                                                                                                | Session `_FILE`                                                               |
| Builds                                        | `WEFT_BUILDS_ENABLED`, `WEFT_FERRULE_WORKER_IMAGE`, private storage, eight build logins                   | no               | connection strings                                                                                                | Session `_FILE`                                                               |

"Private storage" is `WEFT_PRIVATE_STORAGE_ROOT`, or the Azure Blob settings selected by `WEFT_PRIVATE_STORAGE_KIND=azure-blob` (see [host selection](private-storage-blob.md#host-selection)); the Blob settings carry no secret, so they are plain environment values in both profiles.

Preview, regression and build execution always runs in the separate `Weft.Worker` host; the web host only records runs and their durable commands. Revision comparison has no setting of its own: it reads the two retained reports through the same admitted report reader.

### Choose the native runner

The Session host has no Docker access. For the recommended service route, set
`WEFT_FERRULE_RUNNER=service`. Session sends inspection, validation and projection
frames directly to the [Ferrule service](ferrule-service.md#braid-service-runner).
It requires `WEFT_FERRULE_SERVICE_URL`,
`WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256`, `WEFT_FERRULE_WORKER_IMAGE` (the service's
build identity) and the protected `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE` /
`WEFT_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE`.

[Azure Container Apps dynamic sessions](ferrule-service.md#azure-container-apps-dynamic-sessions)
is an advanced alternative selected by
`WEFT_FERRULE_RUNNER=azureContainerAppsSessions`, with its own pool endpoint and
managed-identity settings. It is not enabled by choosing Session authentication.

The older inspection-worker route remains available: `WEFT_FERRULE_RUNNER=worker`
(also the Session default when no runner is selected) forwards frames to a separate
`Weft.Worker` over the [hosted inspection transport](ferrule-worker.md#hosted-inspection-transport).
That worker needs a Docker-capable host. When native upload or derivation is
enabled with this route, Session requires:

| Setting                                                                               | Required value                                                                                                                                    |
| ------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WEFT_INSPECTION_WORKER_URL`                                                          | The worker's canonical HTTPS origin on the private network, for example `https://weft-worker:8443`; no path, user information, query or fragment. |
| `WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256`                                           | Lowercase hexadecimal SHA-256 of the worker's certificate (DER); the only certificate the host admits for it.                                     |
| `WEFT_FERRULE_WORKER_IMAGE`                                                           | The immutable lowercase `sha256:` image ID the worker runs; recorded in admission and report provenance.                                          |
| `WEFT_INSPECTION_CLIENT_CERTIFICATE_FILE` / `WEFT_INSPECTION_CLIENT_PRIVATE_KEY_FILE` | The host's current RSA client PEM certificate and key, delivered as protected private files; the worker admits only this certificate's pin.       |

The settings of a runner that is not selected must be absent. Do not combine the
service and inspection-worker configurations.

`WEFT_FERRULE_COMPILE_TOOLCHAIN` optionally binds the service or dynamic-sessions
toolchain to the native worker identity for reports and build evidence; hosted
builds require it. A supplied pin is validated even when ingestion and report
derivation are disabled. It is not valid with the hosted inspection worker, or
on a `Weft.Worker` with preview execution disabled. Validating a dormant pin does
not open a certificate or start a native runner.

The scalars are checked with the other feature scalars, before any private delivery is opened; the client PEM is admitted with the other PEMs. A missing or invalid value refuses startup. With both flags disabled these settings stay dormant. Startup does not contact the worker: an unreachable worker makes admission and derivation fail as a retryable unavailable backend, recovered by explicit resume or ensure.

Session snapshots every feature flag before it opens any PEM, ring or credential. Chat additionally requires report reads, and that inconsistency is refused at the same scalar stage. Chat's private profile document is described in [Chat turn HTTP API](chat-http.md); previews in [Preview runs](preview-runs.md).

### Deliver feature credentials separately

| Session credential file                                                                                                             | Login                                                                                                                                                                                                    |
| ----------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WEFT_CHAT_READER_CONNECTION_STRING_FILE` / `WEFT_CHAT_WRITER_CONNECTION_STRING_FILE` / `WEFT_CHAT_APPROVER_CONNECTION_STRING_FILE` | Independently credentialed `weft_chat_reader` / `weft_chat_writer` / `weft_chat_approver`; all three required when chat is enabled. See [exact approval composition](chat-edit-approval-composition.md). |
| `WEFT_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING_FILE` (optional)                                                                    | `weft_payload_permission_reader`                                                                                                                                                                         |
| `WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING_FILE` / `WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING_FILE`                       | `weft_preview_reader` / `weft_preview_writer`                                                                                                                                                            |

A plain chat or preview connection-string setting refuses Session startup rather than being ignored, and each pair must name two different deliveries. The files use the same admission as the session/identity credentials: private-file checks, explicit username and password, no passfile, client certificate/key or ambient `PG*` credential, and password-only authentication.

## Private files and conflicting configuration

Session rejects a true `WEFT_DEV_IDENTITY_ENABLED` or any supplied Development principal, organization, browser-origin or listener setting. It also rejects conflicting URL/port/Kestrel/automatic-forwarding configuration. Remove ambient `ASPNETCORE_URLS`, `DOTNET_URLS`, HTTP/HTTPS port variables, endpoint/certificate configuration, and automatic forwarding before choosing this explicit profile. Supported host settings prevent hosting-startup loading. Configured startup hooks, profilers and automatic OpenTelemetry instrumentation are outside this closed process profile and are refused before private resources are opened; a process cannot undo privileged code already injected before its entrypoint.

Secret text/overlap JSON is bounded to 16 KiB, each PEM to 64 KiB. Paths must already be canonical absolute Linux paths with nonsymlink protected ancestors. Each file must be owned by the effective user, regular, single-link and exactly 0600. Root-owned sticky temporary ancestors retain the existing filesystem-profile exception. UTF-8 is strict; BOM and NUL are rejected. Connection files remove at most one terminal LF, preserving other text. There is no directory/file creation, permission repair, symlink adoption or descriptor-relative race guarantee. These checks do not isolate the host from another process with the same effective UID. The key profile independently preserves its existing 0700 ring/0600 child, owner, ancestor and every-nonrevoked-key admission checks. The host opens an existing ring; it never calls bootstrap. Keep old decrypt certificates during explicit restart-based rotation. There is no hot certificate/profile reload.

The three identity/session deliveries reject passfiles, PostgreSQL client-certificate/key settings and ambient `PGUSER`, `PGPASSWORD`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGREQUIREAUTH` and `PGGSSENCMODE`. TLS connections also refuse an implicit `$HOME/.postgresql/postgresql.crt` identity. GSS encryption is disabled; authentication is restricted to password, MD5 or SCRAM-SHA-256, retaining any explicitly selected narrower subset. Username/password text is not trimmed or normalized. The three files are admitted before any identity/session pool owner is constructed.

The three identity/session pools are separate, lazy and borrowed by the modules. Startup does not open PostgreSQL or call the provider, except that enabled previews start the Wolverine submitter, which verifies its message storage in `weft_messages` with the delivered preview writer login; an unreachable database then fails startup. Session parses and retains its feature flags, report/derivation consistency and enabled native/derived byte limits before opening PEMs, the ring or credentials. Disabled feature limits remain dormant. Existing catalog read/write/ingestion/report flags and their dedicated credentials remain separate and lazy, with the same business ACL checks. Session hosting does not register a Docker runner or map examples, projection fixtures, or Development OpenAPI, even if the environment is Development. Inspection goes through the selected service, dynamic session or hosted worker. Enabling an existing business flag does not create a worker deployment or a grant; unsupported dispatch still fails through the existing service. Run the required `Weft.Worker` services as a separate deployment rather than assuming this host starts them.

## Transport and privacy boundaries

Direct TLS prebuilds one `SslStreamCertificateContext` with `offline: true` from the admitted leaf/key and supplied PEM chain, then passes that static context through Kestrel's supported TLS handshake callback. Startup and handshakes do not fetch AIA issuers or start background OCSP stapling downloads; supply a usable chain rather than relying on live issuer completion. Clients still validate the server's trust, name and lifetime. HTTP/1, system TLS protocol defaults, Kestrel's handshake timeout and the absence of requested client certificates are preserved. Certificates remain borrowed until the existing shutdown settlement boundary; there is no new TLS trust store, client-certificate bypass or hot reload.

Authentication registration also adds an unused framework Data Protection warmup to the main host container. Session removes only that exact singleton hosted-service descriptor, resolving its trusted type from the public `IKeyManager` assembly and failing startup if the expected registration count/shape changes. Other hosted services remain intact. The unused main-container `IDataProtectionProvider` refuses protector creation rather than falling back to a default HOME key ring. Named cookie/OIDC formats and private antiforgery services continue to use the existing admitted, certificate-protected profile; its key storage, purpose isolation and owner are unchanged. This prevents new default-ring access by this composition, not a claim about ownership or origin of any files already present on an operating host.

Direct HTTPS admits only the exact configured scheme/authority on its explicit HTTP/1 TLS socket and refuses forwarding/original headers. Proxy mode first verifies the actual peer and local socket, raw canonical Host, and exactly one literal-IP `X-Forwarded-For`, `https` proto, and exact public host. It rejects unknown forwarding, prefix/original headers and comma chains before using the standard one-hop ForwardedHeaders middleware. A final gate requires the canonical HTTPS authority and no PathBase. The edge must terminate trusted TLS, strip client-supplied forwarding fields, emit exactly the agreed fields, block backend bypass, and omit callback query/cookies/body/redirect/provider diagnostics from its logs. Another local process can impersonate a loopback proxy; the operating host is part of this trust boundary. Actual HTTP parse failures that precede application middleware remain Kestrel failures, not a claimed application-header response.

The session pipeline is a supported Generic Host with an explicit application pipeline. It contains no default authentication scheme, blanket authentication/authorization middleware, developer exception page, provider logout, bearer fallback, account provisioning or claims-to-role converter. Opt-in [bearer access](api-authentication.md) is a separate admission path of the catalog boundary, never a fallback for a missing or refused session. The fixed auth mapper and catalog mapper own their individual admission. Successful authentication still needs fresh organization/project authority for each business operation; it grants no native-payload permission. Callback completion/failure remains the fixed #118 protocol.

Privacy is applied before listening or provider work: IdentityModel's logger is replaced with a no-output implementation, its artifact/PII flags stay false and its static EventSource is disposed. The final incoming ASP.NET DiagnosticListener/DiagnosticSource alias and ActivitySource are checked and disposed before start. Session ILogger output is disabled even under provider-specific overrides; only fixed entrypoint status lines are emitted. The Entra backchannel uses a null ActivityHeadersPropagator by default. No HTTP logging, request Activity, raw diagnostic exporter, dump or verbose internal network sink is admitted. Scalar counters/status/timing can be observed through separately admitted collectors; raw path/address/config/exception fields are not approved exports. This constrains configured channels, not arbitrary privileged process-memory observation. Edge privacy is separate.

## Restart, rotation and shutdown

Restart with the same organization/application realm, database, key ring and
retained decrypt material. For certificate rotation, deliver the old decrypt
certificates alongside the new protecting certificate before restarting. There
is no hot reload. See [protected server sessions](protected-server-sessions.md)
and [Azure operations](azure-deployment.md#operations) for the key-ring and
certificate procedures; do not bootstrap a replacement ring to recover a host.

Shutdown first closes host request admission. A request is settled only after both its middleware continuation and the host's first-registered, last-run `Response.OnCompleted` callback; downstream browser-session cleanup joins the actual stored tasks before this witness. The host retains the original StopAsync, auth-orchestrator disposal, Entra disposal, provider disposal and resource-disposal tasks. A bounded canceled/timed-out wait is never treated as completion or replaced by a second idempotent call. A settled StopAsync fault retains its failure but does not skip the first provider disposal when request/module drains prove safety; borrowed resources close after successful browser/provider disposal. Browser cleanup failure still permits that safe provider-disposal attempt, but does not admit borrowed-resource release. If host stop is unfinished or request/module settlement is unproven, the provider, browser private services, profile, pools and certificates remain retained and the entrypoint returns a fixed failure. The root's process owner must treat a nonzero shutdown result as unconfirmed cleanup and retain the relevant external resources/evidence rather than silently restarting.

The supported profile is one Linux API instance with a persistent local POSIX ring and external PostgreSQL. A fresh process must reopen that exact realm and retained decrypt material. Shared-filesystem concurrency, replicas, Blob rings, autoscaling, scale-to-zero and Functions/ephemeral disks require separate design and qualification. Host session activation does not itself provide a frontend sign-in interface.
