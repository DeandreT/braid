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

The local web/API foundation is implemented. It provides a workspace shell and
a live service connection check. Mapping, authentication, AI chat, and deployment
features follow the focused delivery sequence below.

Versioned mapping contracts and development-only synthetic projections provide
the shared foundation for the upcoming viewer. Angular types are generated from
the C# API schema.

See [Local development](docs/development.md) to run and verify the application.

- [Architecture](docs/architecture.md): boundaries, storage, identity, and hosting.
- [Mapping backend contract](docs/mapping-backends.md): projections and Ferrule integration.
- [Projection v1](docs/projection-contract.md): public read model and fixture behavior.
- [AI chat architecture](docs/ai-chat.md): provider independence, context, and tools.
- [Delivery plan](ROADMAP.md): focused PRs, dependencies, and acceptance checks.

Stack: Angular frontend, C#/.NET 10 modular backend, PostgreSQL metadata,
and private object storage. Wolverine will handle durable internal jobs when
preview execution is introduced. Model providers remain behind an independent
chat adapter; Ferrule remains behind a typed mapping-engine boundary.
