# Weft

**Understand, test, and deploy your data mappings.**

Weft is a web application for exploring, reviewing, testing, and operating data
mappings. Ferrule is its first mapping backend; other engines require explicit
adapters and their own capability checks.

## Implemented workflows

- A mapping catalog with immutable revisions, native-file uploads and project ownership.
- A compact source-to-rule-to-target workbench with a contextual inspector and investigation dock.
- Revision-pinned AI chat with permission-controlled mapping tools and verified citations.
- Bounded sample previews, revision comparison and saved regression cases.
- Immutable build preparation, qualification, and local release activation, audit and rollback.
- A machine-facing mapping endpoint that serves the active qualified release to callers
  authorized by app-only tokens and per-target invocation grants, with idempotent retries.
- Hosted execution through the platform-agnostic Ferrule service: inspection, previews,
  regression runs, builds and compiled mappings run in isolated sandboxes without Docker,
  and private objects can be kept in Azure Blob storage.
- Microsoft Entra sign-in, server sessions and project-scoped application permissions.
- A `weft` command line that works with the API as the signed-in user.

Other mapping engines, identity providers, and AI model providers will integrate
through separate adapters. Web hosting and publishing a mapping as a runnable
endpoint are distinct workflows.

## Configuration and qualification boundaries

The Angular frontend and C# API provide a mapping catalog, immutable revision
history, saved projection and validation views, project creation, metadata editing,
and native-file upload with explicit recovery. A separate upload page appends a
revision against a reviewed head. Ferrule is the first mapping backend.

Optional business features remain off until their own settings and dependencies are configured.
The repository includes a local hosted-shape stack and Azure deployment templates;
source code, local qualification and actual hosted/provider qualification are
separate gates. Templates do not establish a working Azure deployment or mapping
endpoint. See [Delivery plan](ROADMAP.md) for the acceptance requirements.

Preparing a build does not publish it. The [local release adapter](docs/local-releases.md)
changes a retained release pointer to an already qualified artifact, and the separate
[mapping endpoint](docs/mapping-endpoint.md) host invokes the current release through the
restricted runtime for callers admitted by [machine authorization](docs/machine-authorization.md).

Conditional revision append is implemented in
[PR #96](https://github.com/DeandreT/weft/pull/96), completing
[#19](https://github.com/DeandreT/weft/issues/19). Upload recovery preserves the
original request and does not guess whether an unconfirmed operation succeeded.

Provider-neutral external identity bindings and explicit owner commands are
implemented in [PR #98](https://github.com/DeandreT/weft/pull/98).
Preparing a binding does not itself enable sign-in, create a browser session or
grant payload access; see [External identity bindings](docs/external-identity-bindings.md).

Explicit project payload-read permissions are implemented in
[PR #105](https://github.com/DeandreT/weft/pull/105), with a separate grant from
project membership. The owner contract is described in
[Payload permissions](docs/project-payload-permissions.md).

Provider-neutral [session contracts](docs/server-sessions.md) separate lifetime
policy and restricted PostgreSQL ports from the
[protected ticket/key profile](docs/protected-server-sessions.md) and
[browser cookie/epoch-CSRF boundary](docs/browser-server-sessions.md).
The fixed organizational-tenant [Entra adapter](docs/entra-browser-client.md)
supplies verified identity facts to the opt-in
[authentication HTTP protocol](docs/browser-authentication-http.md).

The Server explicitly selects disabled, local Development, or Session mode.
[Session host operations](docs/session-host.md) describes private credential
delivery, the admitted durable ring, direct HTTPS or one-hop proxy ingress,
privacy and shutdown ownership. Host activation does not provision identities,
keys, database grants or an Entra application, or establish live-provider/cloud
qualification.

The Angular catalog uses [session-safe browser state](docs/browser-session-state.md)
to observe the explicit host mode, fence stale identity callbacks, and preserve
private uncertain requests across same-owner reauthentication. Its sign-in
controls use the separate [authentication HTTP protocol](docs/browser-authentication-http.md);
status observation and authentication do not replace fresh catalog authority.

Versioned mapping contracts and development-only synthetic projections provide
the shared foundation for the viewer. Angular types are generated from
the C# API schema.

The Ferrule adapter can inspect, validate, and project native artifacts through a
bounded, isolated worker. Its revision-specific projection contains fields,
rules, controls, lineage, and located coverage gaps. The catalog retains
immutable native uploads and their saved projection and validation reports; its
scoped viewer opens those reports for the exact selected revision.

See [Local development](docs/development.md) to run and verify the application.

- [User guide](docs/user-guide/overview.md): how to use Weft, also shown in the app at `/help`.
- [In-app help](docs/help.md): writing guide sections, the embedded guide and the help API.
- [Architecture](docs/architecture.md): boundaries, storage, identity, and hosting.
- [Mapping backend contract](docs/mapping-backends.md): projections and Ferrule integration.
- [Projection v1](docs/projection-contract.md): public read model and fixture behavior.
- [Mapping viewer](docs/mapping-viewer.md): navigation, exact values, coverage, and validation.
- [Revision upload](docs/native-revision-append-ui.md): reviewed heads, immutable history, and explicit recovery.
- [MFD interchange](docs/mfd-interchange.md): bounded packages, warning review, immutable provenance, and disabled-by-default import/export integration.
- [Ferrule worker](docs/ferrule-worker.md): native authority, transport, isolation, and qualification.
- [Ferrule projection](docs/ferrule-projection.md): native explanation coverage and viewer integration.
- [Catalog persistence plan](docs/catalog-persistence-plan.md): ordered ownership, storage, revision, API, and browser increments.
- [Project ownership](docs/project-ownership.md): qualified scoped PostgreSQL reads, migrations, and local setup.
- [Catalog search](docs/catalog-search.md): authorized organization-wide search, retained-report backfill, exact workbench links and PostgreSQL indexes.
- [External identity bindings](docs/external-identity-bindings.md): explicit owner preparation, exact account bindings, and the restricted reader.
- [Server sessions](docs/server-sessions.md): pure lifetime and conditional-operation contracts, privacy, and separate store/authentication qualification.
- [Entra browser client](docs/entra-browser-client.md): dormant fixed-tenant OIDC adapter, certificate and key-profile ownership, and separate HTTP/host activation.
- [Authentication HTTP](docs/browser-authentication-http.md): session inspection, login/callback, acknowledged local logout and explicit local forgetting.
- [API authentication](docs/api-authentication.md): the browser session and opt-in Entra bearer tokens for signed-in users' other clients, with rate limits and durable audit.
- [Session host operations](docs/session-host.md): explicit host modes, private configuration, offline TLS, ingress, privacy and shutdown.
- [Runtime packaging](docs/runtime-packaging.md): container images, private delivery, the same-replica edge and the hosted-shape local stack.
- [Azure deployment](docs/azure-deployment.md): Bicep templates, operator inputs, the deployment procedure and open hosting questions.
- [Session-safe browser state](docs/browser-session-state.md): fresh identity observation, protected-view invalidation and private original-operation recovery.
- [Project creation](docs/project-creation.md): explicit capability, atomic creation, and immutable replay history.
- [Project creation HTTP](docs/project-creation-http.md): request admission, organization actions, and separate host composition.
- [Private storage](docs/private-storage.md): exact-byte publication, verified reads, and the local filesystem profile.
- [Claim-aware storage](docs/private-storage-v2.md) and [offline activation](docs/private-storage-activation.md): tracked publication, permanent gates, and explicit v1 root conversion.
- [Revision report metadata](docs/revision-report-metadata.md): complete report identity, restricted roles, claims, retained attempts, and repair.
- [Persisted report API](docs/persisted-report-api.md): current and retained report reads, explicit derivation, and complete-response limits.
- [Maintenance metadata](docs/private-object-maintenance-metadata.md): permanent retirement, fenced cleanup claims, and positive publication registration.
- [Workbench presentation](docs/workbench-presentation.md): semantic colors, exact-value details, safe layout preferences and action confirmations.
- [Chat UI](docs/chat-ui.md): revision-pinned conversations and evidence-backed navigation.
- [Preview runs](docs/preview-runs.md): bounded execution of explicit sample inputs.
- [Regression cases](docs/regression-cases.md): saved inputs, expected output and immutable case versions.
- [Automation and notifications](docs/automation-notifications.md): exact-revision checks, project build gates and metadata-only activity delivery.
- [Build workflow](docs/build-workflow.md) and [compiled runtime](docs/compiled-runtime.md): capability admission, immutable artifacts and restricted execution.
- [Local releases](docs/local-releases.md): atomic activation, retained audit, explicit rollback and current-release invocation.
- [Release operations](docs/release-operations.md): conditional release actions, metadata-only invocation monitoring, caller access and CLI commands.
- [Release qualification](docs/release-qualification.md): exact-baseline qualification evidence for builds.
- [Mapping endpoint](docs/mapping-endpoint.md) and [machine authorization](docs/machine-authorization.md): the machine-facing HTTP host, app-only tokens, invocation grants and idempotent calls.
- [Ferrule service](docs/ferrule-service.md), [build compiler](docs/build-compiler.md) and [compiled service runtime](docs/compiled-service-runtime.md): sandboxed hosted inspection, compilation and compiled execution without Docker.
- [API contracts](docs/development.md#updating-api-contracts): the OpenAPI 3.1 documents of the [session API](contracts/weft-v1.json), the [mapping endpoint](contracts/weft-endpoint-v1.json) and the [Ferrule service](contracts/ferrule-service-v1.json).
- [Azure Blob private storage](docs/private-storage-blob.md): the Blob object store and its publication format.
- [Hosted acceptance](docs/hosted-acceptance.md): the packaged signed-in stack exercised end to end.
- [Weft CLI](docs/cli.md): the `weft` command line as the signed-in user, its profiles, sign-in, protected token cache and commands.
- [Revision comparison](docs/revision-comparison.md) and [compact workbench](docs/compact-workbench.md): semantic differences between revisions and the workbench layout.
- [AI chat architecture](docs/ai-chat.md): provider independence, context, and tools.
- [Delivery plan](ROADMAP.md): focused PRs, dependencies, and acceptance checks.

Stack: Angular frontend, C#/.NET 10 modular backend, PostgreSQL metadata,
private object storage and Wolverine-backed durable execution. Model providers remain behind an independent
chat adapter; Ferrule remains behind a typed mapping-engine boundary.
