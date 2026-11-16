# braid architecture

This document describes the application's source boundaries and design contracts.

The [delivery plan](../ROADMAP.md) owns current priorities.

## Product purpose

braid helps people understand, review, test, and operate data mappings. Its
primary interface is a readable field mapping view with an integrated AI chat
window. Engineers can inspect precise rules; reviewers can follow the business
meaning without navigating a complete authoring canvas.

Ferrule is the first mapping backend. braid has its own repository, release
process, product model, and deployment. Ferrule remains responsible for its
native format and transformation semantics.

The product includes catalog browsing, immutable revisions, a field mapping
workbench, grounded chat, bounded samples, regression cases, build/release paths
and Entra sign-in. Opt-in editing uses private drafts and a separate **Save**.
Creation from specifications uses retained proposals, sample review and explicit
**Approve and save this mapping**. Chat edits likewise require approval of the
exact reviewed proposal; a model cannot approve or save its own proposal.

## Architectural decisions

| Decision | Reason and consequence |
| --- | --- |
| Independent repository | braid can integrate multiple engines and release independently of Ferrule. |
| C#/.NET 10 modular application with explicit assemblies | Keep initial deployment and transactions simple; extract services only when operational needs justify it. |
| Angular/TypeScript browser frontend | Use searchable tables, accessible forms, and ordinary document navigation; graph rendering is an expandable detail view. |
| ASP.NET Core HTTP service | Follow the Wareboxes-style modular API/worker structure; mapping-engine details remain inside adapters. |
| PostgreSQL plus private object storage | Store relational ownership and history separately from native mappings and execution payloads. |
| Server-managed OIDC login sessions | Keep identity-provider tokens and client credentials on the server; later providers can implement the same identity boundary. |
| AI conversation orchestration owned by braid | Model providers translate requests and streams; permissions, context, tools, and history remain application concerns. |
| Wolverine with PostgreSQL durability | Durable internal job delivery and transactional inbox/outbox; braid retains business run history, permissions, and idempotency. |
| Mapping execution outside the HTTP process | A bounded worker process permits actual cancellation and resource isolation for submitted mappings. |
| Optional capabilities | Viewing, previewing, editing, compiling, and deploying need separate admission checks. |

The modules below are source boundaries, not a requirement to run a distributed
microservice system. An API executable and an isolated execution worker are
sufficient for the initial runtime. AI orchestration runs in the API with
bounded, cancellable outbound requests.

## System layout

```mermaid
flowchart TB
    subgraph callers [Callers]
        direction LR
        browser["Browser: Angular workbench"]
        machine["Machine caller: app-only Entra token"]
        operator["Operator tools: database, release, storage, maintenance"]
    end
    subgraph hosts [Hosts]
        direction LR
        edge["Edge: TLS and HSTS"]
        server["braid server (Session host): /api/v1 and /api/auth"]
        endpoint["Mapping endpoint host: /v1/invocations"]
        worker["Braid.Worker runs: previews, regression runs, builds"]
    end
    subgraph external [External services]
        direction LR
        entra["Microsoft Entra ID"]
        models["Model provider: Chat Completions or Ollama"]
    end
    ferrule["Ferrule service: one sandboxed ferrule-host child per operation"]
    subgraph state [State]
        direction LR
        postgres["PostgreSQL: one restricted login per role; build artifacts"]
        objects["Private objects: native files and reports, filesystem or Azure Blob"]
    end

    browser -->|"session cookie and antiforgery"| edge --> server
    machine -->|"bearer token and request ID"| endpoint
    operator -->|"restricted logins"| postgres
    server -->|"sign-in"| entra
    endpoint -->|"signing keys"| entra
    server -->|"chat rounds"| models
    server -->|"inspect, validate, project"| ferrule
    worker -->|"preview, compile"| ferrule
    endpoint -->|"run the compiled release"| ferrule
    hosts -->|"restricted logins, create-only objects"| state
```

Hosts never load Ferrule or mapping code in process. Every native operation crosses
the pinned, mutually authenticated transport to a fresh sandboxed child (see
[Ferrule service](ferrule-service.md)); local development can use the per-operation
Docker runner instead, and Azure can use Container Apps dynamic sessions.

### Upload and reports

```mermaid
sequenceDiagram
    participant B as Browser
    participant S as braid server
    participant P as PostgreSQL
    participant O as Private objects
    participant F as Ferrule service
    Note over S: Every step rechecks current organization and project authority
    B->>S: Upload a native file (new mapping or appended revision)
    S->>P: Reserve identities and claim the attempt
    S->>O: Publish the native bytes (create-only)
    S->>O: Read back and verify scope, size and digest
    S->>F: Validation frame
    F-->>S: Typed result echoing revision and digest
    S->>P: Finish the attempt and publish the revision
    B->>S: Ensure the projection and validation reports
    S->>P: Claim the derivation, or return the retained report
    S->>F: Projection and validation frames
    F-->>S: Typed reports
    S->>O: Publish report objects (create-only)
    S->>P: Record the reports
    B->>S: Read the reports of the exact revision
```

### Build, release and invocation

```mermaid
sequenceDiagram
    participant B as Browser
    participant S as braid server
    participant W as Braid.Worker runs
    participant R as Braid.ReleaseTool
    participant P as PostgreSQL
    participant F as Ferrule service
    participant E as Mapping endpoint
    participant M as Machine caller
    B->>S: Approve and request a build
    S->>P: Durable build run
    W->>P: Claim the run
    W->>F: Compile (generation and compiler children)
    F-->>W: Artifact bytes and compiler identity
    W->>P: Store the artifact and its publication
    R->>F: Qualify: replay the saved regression case on the compiled runtime
    R->>P: Record qualification, register the target, activate the release
    M->>E: Invocation with app-only token and request ID
    E->>E: Validate the token and the grant, claim the request ID
    E->>P: Read the current release and its artifact
    E->>F: Run the compiled mapping in a sandboxed child
    F-->>E: Outputs
    E->>P: Retain the outcome and audit
    E-->>M: Outputs with release, build and artifact identity
    M->>E: Retry with the same request ID
    E-->>M: The retained outcome, replayed without running again
```

There are four independent extension boundaries: mapping backend, identity
provider, model provider, and deployment target. Adding one must not require
changing the contracts of the others.

## Source ownership

| Path | Owns | Must not own |
| --- | --- | --- |
| `apps/web/` | Catalog, viewer, chat window, preview forms, generated API client | Ferrule semantics, provider credentials, authorization decisions |
| `dotnet/src/Braid.Domain/` | IDs, revisions, memberships, runs, conversations, capabilities | Framework types and third-party engine/provider types |
| `dotnet/src/Braid.Api/` | HTTP admission, authenticated request boundaries, application composition and chat event streams | Mapping execution and model-specific wire formats |
| `dotnet/src/Braid.MappingBackends/` | Mapping adapter contract and Ferrule implementation | User sessions, deployment credentials, universal mapping execution |
| `dotnet/src/Braid.Ai/` | Provider adapters, tool contracts, model profiles and bounded conversation processing | Bypassing API permissions or executing arbitrary model-supplied code |
| `dotnet/apps/Braid.Worker/` | Durable preview, regression and build job hosting | Public authentication and unrestricted host I/O |
| `dotnet/src/Braid.Deployment/` | Build, qualification, release and runtime contracts/adapters | Reimplementing a mapping engine |
| `deploy/azure/` | Azure infrastructure templates and operator procedures | Cloud types in domain contracts |

Keep Ferrule dependency declarations confined to its adapter and worker wiring.
braid's public API and database schema must not expose Ferrule's Rust types.
`dotnet/src/Braid.Contracts/` owns public wire models;
`dotnet/src/Braid.Application/` owns commands and policies, and
`dotnet/src/Braid.Persistence.Postgres/` owns scoped transactions and migrations.
`dotnet/apps/Braid.Server/` is the executable host. Identity adapters live in
`dotnet/src/Braid.Identity/`; durable job handlers live in `dotnet/src/Braid.Jobs/`.
`engines/ferrule-host/` uses Ferrule's Rust libraries behind a versioned
request/result protocol. It is an engine adapter, not the application backend.

## Domain and persistence

All resource ownership is explicit: organization, project, mapping, then
revision. Queries and storage references include organization IDs. Each host
uses one configured organization and Entra tenant.

| Entity | Important contents |
| --- | --- |
| Principal / membership | Provider identity, internal principal ID, organization/project role grants |
| Mapping | Stable ID, name, owner, tags, description, selected backend |
| Revision | Immutable native artifact digest, entry document, dependency manifest, backend build identity, creator |
| Projection | Derived viewer document keyed by revision, adapter version, and projection schema version |
| Test case | Revision or mapping association, input references, typed parameters, expected results, comparison policy |
| Run | Exact revision, runtime identity, parameter snapshot, input/output references, status and timestamps |
| Conversation / turn | Owner and project, pinned revision, selected model profile, messages and validated citations |
| Model profile | Provider/model identifier, endpoint reference, capability policy, credential reference, data policy |
| Deployment | Approved revision, immutable build/artifact, qualification, registered target and retained release history; new triggers need separate contracts |

Native mapping bytes are authoritative. Uploading a revision never rewrites
them through JavaScript JSON parsing. This also preserves engine-specific
numeric encodings and unknown metadata. A projection is a derived explanation,
not a replacement executable mapping format.

Object keys are generated by braid within the owning organization's namespace.
Mapping file paths and output names are logical identities, not authorization
to read or write arbitrary server paths. Blob access requires the same ownership
checks as metadata access; digests alone never grant access.

Artifact uploads are staged, inspected under limits, and linked to an immutable
revision only when the metadata transaction commits. Unreferenced staged blobs
can expire. Projection/validation failures retain explicit status and diagnostics
instead of silently marking a mapping executable. Derived projections can be
rebuilt without changing a revision's artifact bytes.

## HTTP and browser boundary

Use versioned `/api/v1` routes and the published [OpenAPI contract](../contracts/braid-v1.json).
Generate browser types from that contract rather than copying C#
models by hand. Resource IDs and backend node locators use strings in the public
API to avoid JavaScript integer precision loss.

Route families include projects, mappings/revisions, projections/lineage,
validation, previews/runs, conversations/chat turns, specification intake,
creation proposals and model-profile discovery. Build and release operations have
their own contracts. Responses distinguish invalid
mapping, unsupported capability, authorization failure, resource limit, backend
failure, and cancellation without leaking raw provider responses or credentials.

Use immutable revision URLs for reproducible viewer and chat links. Mutation
requests use idempotency keys where retries could create duplicate revisions or
jobs. Preview creation returns a run ID; polling or streaming observes the same
run even when HTTP instances scale or restart. Chat uses the analogous turn ID
and application-owned streaming events described in [AI chat](ai-chat.md).

## Identity and authorization

The initial identity adapter uses Entra OIDC authorization-code flow with PKCE,
state, and nonce. The server validates issuer, audience, signature, expiry, and
nonce with a maintained library. Configure an explicit tenant allowlist; do not
infer trusted membership from an email suffix or client-supplied organization ID.

The browser receives a secure, HttpOnly, SameSite session cookie. Sessions are
stored server-side, rotate on login, have bounded lifetimes, and can be revoked.
State-changing requests require CSRF/origin protection. The SPA and API use one
origin in production; development origins are explicitly configured. Signed-in
API bearer access and app-only mapping endpoint tokens are validated separately
from browser sessions; see [API authentication](api-authentication.md) and
[machine authorization](machine-authorization.md).

An identity adapter maps verified issuer/subject information to an internal
principal. Entra app roles can constrain application admission and role grants;
braid's project membership records determine resource scope. Role values do not
implicitly grant access to every project. Permission changes are applied by
fresh server-side checks, including each AI tool call.

| Permission | Viewer | Runner | Publisher | Administrator |
| --- | --- | --- | --- | --- |
| View mapping metadata and approved revisions | Yes | Yes | Yes | Yes |
| Ask read-only mapping questions, when the configured model profile is allowed | Yes | Yes | Yes | Yes |
| Submit sample preview | No | Yes | Yes | Yes |
| Upload revisions and maintain tests | No | No | Yes | Yes |
| Request permitted build/release actions | No | No | Yes | Yes |
| Manage membership, connections, and model profiles | No | No | No | Yes |

Production payload access is a separate grant (`run.payload.read`), not inherited
from mapping visibility or publishing. Uploaded sample data is accessible only
under its own ownership policy. Chat does not gain payload access merely because
the user can inspect mapping rules.

Azure RBAC separately controls the service/workload identity's Azure resource
access. Entra directory administration roles, Azure resource roles, and braid app
permissions are distinct. Model credentials and deployment credentials remain
server-side references; they are not stored inside mapping artifacts.

## AI chat window

The chat window stays beside the selected mapping and revision. Users can ask
where a value comes from, why rows are filtered, what a rule means, or which
outputs depend on a field. Responses link to actual fields and rules.

braid owns conversation state, context retrieval, authorization, tool execution,
citations, cancellation, and budgets. A model-provider adapter owns provider
authentication, request translation, capability reporting, and stream decoding.
The frontend never speaks directly to a model vendor.

Read-only tools and action tools have separate admission. Opt-in edit and creation
tools prepare retained proposals; only an authenticated, proposal-specific user
action can approve them. Approval rechecks current authority and the exact
proposal/base. Reconnects and natural-language claims of consent cannot approve.
Uncertain results retain the original operation for explicit recovery rather than
starting a replacement save. See [AI chat architecture](ai-chat.md),
[exact edit review](chat-proposal-review-ui.md) and
[creation runtime](mapping-creation-runtime.md).

## Execution and resource policy

Run mapping inspection and preview in isolated worker processes with time,
memory, input/output, trace, artifact-count, and concurrency limits. A timeout
must terminate the worker, not just stop awaiting an in-process task. Worker
credentials cannot administer memberships, model profiles, or deployments.

Preview supports explicitly supplied in-memory documents and typed
parameters. It rejects unresolved resources, stateful database operations,
update-existing files, and unapproved outbound connections before execution.
Capabilities are checked for the exact revision, target, format, and requested
operation. A backend's maximum sizes are ceilings; braid can impose smaller limits.

Use the limits in
[preview runs](preview-runs.md), [Ferrule worker](ferrule-worker.md) and the exact
operation's contract. The implementation must also limit projection
nodes, JSON depth, diagnostics, chat tool results, and trace bytes. Exceeding a
viewer limit yields an explicit incomplete-view status.

Record operation IDs, revision/backend identities, durations, counts, bounded
error categories, and audit actions. Avoid logging native mappings, prompts,
payloads, credentials, or complete model responses by default. Interactive
preview results expire after a configurable short period; persistent test
fixtures are explicit objects with independent retention and access rules.

## Durable internal jobs

Wolverine-backed preview, regression and build jobs carry scoped identifiers and
artifact references, not raw
payloads or model credentials. A worker reloads the owning run/revision and
validates its authority before executing a command.

Commit accepted job state and its outgoing command in one PostgreSQL transaction.
Configure durable endpoints explicitly. braid's run/deployment records remain the
source of user-visible status and immutable attempt evidence; Wolverine envelopes
are delivery infrastructure. Handler retries and duplicate delivery must use the
same business operation identity and must not duplicate published outputs or
external deployment effects.

Keep chat's live response stream and ordinary reads in ASP.NET Core. Do not retry
paid model turns through a generic background-message policy. Longer AI actions
can use typed jobs later, with their own cancellation and idempotency rules.
Messaging-store tenant scope and operational connections need explicit checks;
Wolverine persistence does not itself establish project authorization. Its
PostgreSQL durability agents require a direct or session-pooled connection for
session advisory locks, independently of application connection pooling.

## Hosting and mapping deployment

Local development uses a static frontend, one API process, an isolated worker,
PostgreSQL, and local private object storage. A local-only development identity
mode must be explicit, bind to loopback, and fail startup in hosted configuration.

The Azure templates, in [Azure deployment](azure-deployment.md), describe the
API container on Container Apps serving the built frontend from the same origin,
with private PostgreSQL Flexible Server, Key Vault and a managed identity. The API uses one
instance with persistent session keys and private storage.
With native work enabled, a separate worker app executes previews, regression runs and
builds through the Ferrule service below, with its own identity and database logins.

Hosted platforms without Docker run native work through the platform-agnostic
[Ferrule service](ferrule-service.md): every inspection, preview, compilation and
compiled-mapping execution is a fresh, independently limited sandbox child, and the
service scales with load. Platform sandboxes such as Container Apps dynamic sessions
plug in as adapters, and the local Docker runner remains for development.

Publishing an individual mapping validates the exact revision, preflights
runtime/format capability, builds an immutable artifact, qualifies it against saved
regression baselines, and activates it as the current release of a registered target
([build workflow](build-workflow.md), [release qualification](release-qualification.md),
[local releases](local-releases.md)). Rollback activates a previously qualified
immutable artifact. The separate [mapping endpoint](mapping-endpoint.md) host serves the
current release to machine callers admitted by [machine authorization](machine-authorization.md);
same-ID retries replay the retained outcome instead of executing again. Transformations
with external side effects still require their own idempotency contract rather than
blind replay.

Cloud selection is a deployment decision, not part of the mapping
model. Generated mappings are supported only when their backend accepts the
exact graph and physical format; interpreter support does not imply generator
support.

## References

- [Mapping backend contract](mapping-backends.md) and [AI chat contract](ai-chat.md).
- [Focused delivery sequence](../ROADMAP.md).
- [Angular](https://angular.dev/overview) and [ASP.NET Core APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis?view=aspnetcore-10.0).
- [Wolverine PostgreSQL durability](https://wolverinefx.net/guide/durability/postgresql).
- [Entra authorization-code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).
- [Application RBAC](https://learn.microsoft.com/en-us/entra/identity-platform/howto-implement-rbac-for-apps).
- [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app).
- [Container Apps managed identities](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity).
- [Azure Functions isolated .NET worker](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide).
