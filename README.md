# Weft

**Understand, test, and deploy your data mappings.**

Weft is a web application in development for exploring, reviewing, testing, and
operating data mappings. Ferrule will be its first mapping backend; the
application will support other engines through explicit adapters.

An integrated AI chat window will help users understand mappings through a
model-independent backend and permission-controlled mapping tools.

## Planned first release

- A mapping catalog with immutable revisions and project ownership.
- A searchable source-to-rule-to-target view with expandable details and lineage.
- An AI chat window that explains the selected mapping and links answers to rules.
- Bounded sample previews through the selected mapping backend.
- Microsoft Entra sign-in and project-scoped application permissions.

Other mapping engines, identity providers, and AI model providers will integrate
through separate adapters. Web hosting and publishing a mapping as a runnable
endpoint are distinct workflows.

## Status and planning

The Angular frontend and C# API provide a mapping catalog, immutable revision
history, saved projection and validation views, project creation, metadata editing,
and native-file upload with explicit recovery. A separate upload page appends a
revision against a reviewed head. Ferrule is the first mapping backend.

Catalog persistence, scoped APIs, private storage, maintenance, and all six catalog
browser workflows are qualified and merged. Conditional revision append completed
[PR #96](https://github.com/DeandreT/weft/pull/96) after 140 fresh browser cases
passed with no retries or skips, completing
[#19](https://github.com/DeandreT/weft/issues/19). Authentication, AI chat, sample
previews, and deployment follow those catalog workflows.

Provider-neutral external identity bindings and explicit owner commands are
qualified and merged in [PR #98](https://github.com/DeandreT/weft/pull/98).
They do not enable Entra sign-in, browser sessions, or production-payload access;
see [External identity bindings](docs/external-identity-bindings.md) for the
operator contract and qualification evidence.

Explicit project payload-read permissions are qualified and merged in
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
keys, database grants, or an Entra application, and does not itself provide an
Angular sign-in interface or establish live-provider/cloud support.

The Angular catalog uses [session-safe browser state](docs/browser-session-state.md)
to observe the explicit host mode, fence stale identity callbacks, and preserve
private uncertain requests across same-owner reauthentication. Status observation
does not replace fresh catalog authority or add sign-in controls.

Versioned mapping contracts and development-only synthetic projections provide
the shared foundation for the viewer. Angular types are generated from
the C# API schema.

The Ferrule adapter can inspect, validate, and project native artifacts through a
bounded, isolated worker. Its revision-specific projection contains fields,
rules, controls, lineage, and located coverage gaps. Development example routes
provide separate projection and validation reports. The catalog retains
immutable native uploads and their saved projection and validation reports; its
scoped viewer opens those reports for the exact selected revision.

See [Local development](docs/development.md) to run and verify the application.

- [Architecture](docs/architecture.md): boundaries, storage, identity, and hosting.
- [Mapping backend contract](docs/mapping-backends.md): projections and Ferrule integration.
- [Projection v1](docs/projection-contract.md): public read model and fixture behavior.
- [Mapping viewer](docs/mapping-viewer.md): navigation, exact values, coverage, and validation.
- [Revision upload](docs/native-revision-append-ui.md): reviewed heads, immutable history, and explicit recovery.
- [Ferrule worker](docs/ferrule-worker.md): native authority, transport, isolation, and qualification.
- [Ferrule projection](docs/ferrule-projection.md): native explanation coverage and viewer integration.
- [Catalog persistence plan](docs/catalog-persistence-plan.md): ordered ownership, storage, revision, API, and browser increments.
- [Project ownership](docs/project-ownership.md): qualified scoped PostgreSQL reads, migrations, and local setup.
- [External identity bindings](docs/external-identity-bindings.md): explicit owner preparation, exact account bindings, and the restricted reader.
- [Server sessions](docs/server-sessions.md): pure lifetime and conditional-operation contracts, privacy, and separate store/authentication qualification.
- [Entra browser client](docs/entra-browser-client.md): dormant fixed-tenant OIDC adapter, certificate and key-profile ownership, and separate HTTP/host activation.
- [Authentication HTTP](docs/browser-authentication-http.md): session inspection, login/callback, acknowledged local logout and explicit local forgetting.
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
- [Maintenance metadata](docs/private-object-maintenance-metadata.md): permanent retirement, fenced cleanup claims, and positive publication registration; implementation in progress.
- [AI chat architecture](docs/ai-chat.md): provider independence, context, and tools.
- [Delivery plan](ROADMAP.md): focused PRs, dependencies, and acceptance checks.

Stack: Angular frontend, C#/.NET 10 modular backend, PostgreSQL metadata,
and private object storage. Wolverine will handle durable internal jobs when
preview execution is introduced. Model providers remain behind an independent
chat adapter; Ferrule remains behind a typed mapping-engine boundary.
