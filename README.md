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

The local web/API foundation and read-only mapping viewer are implemented. The
workspace displays native development examples through the Ferrule adapter.
Catalog persistence, scoped reads, metadata edits, native uploads, project
creation, and saved revision reports through the API are qualified and merged.
Maintenance, saved-revision viewer integration, authentication, AI chat, and deployment follow
the focused delivery sequence below.

Versioned mapping contracts and development-only synthetic projections provide
the shared foundation for the viewer. Angular types are generated from
the C# API schema.

The Ferrule adapter can inspect, validate, and project native artifacts through a
bounded, isolated worker. Its revision-specific projection contains fields,
rules, controls, lineage, and located coverage gaps. Development example routes
provide separate projection and validation reports. The catalog API accepts
immutable native uploads and serves their saved projection and validation reports.
Connecting those catalog revisions to the viewer follows maintenance.

See [Local development](docs/development.md) to run and verify the application.

- [Architecture](docs/architecture.md): boundaries, storage, identity, and hosting.
- [Mapping backend contract](docs/mapping-backends.md): projections and Ferrule integration.
- [Projection v1](docs/projection-contract.md): public read model and fixture behavior.
- [Mapping viewer](docs/mapping-viewer.md): navigation, exact values, coverage, and validation.
- [Ferrule worker](docs/ferrule-worker.md): native authority, transport, isolation, and qualification.
- [Ferrule projection](docs/ferrule-projection.md): native explanation coverage and viewer integration.
- [Catalog persistence plan](docs/catalog-persistence-plan.md): ordered ownership, storage, revision, API, and browser increments.
- [Project ownership](docs/project-ownership.md): qualified scoped PostgreSQL reads, migrations, and local setup.
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
