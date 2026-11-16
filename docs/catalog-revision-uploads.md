# Native revision upload and recovery

The P3d3 native revision API implements
[#30](https://github.com/DeandreT/weft/issues/30), following qualified and merged
[PR #38](https://github.com/DeandreT/weft/pull/38). This document describes its
upload and recovery HTTP boundary. [Native revision ingestion](native-revision-ingestion.md)
owns byte admission, frozen profiles and fenced publication;
[catalog reads](catalog-reads.md) owns metadata and the Development request boundary.
[Project creation](project-creation-http.md), [catalog browser](catalog-browser.md)
and [Session host](session-host.md) own their separate workflows and host activation.
The [contract-owner map](catalog-persistence-plan.md#current-contract-owners)
links the original requirements. No local API contract establishes cloud acceptance.

Let `P=/api/v1/organizations/{organizationId}/projects/{projectId}` and
`M=P/mappings/{mappingId}`. Every identifier is a canonical nonzero lowercase
UUID. Every operation uses current Publisher or Administrator authority on the
exact project, including reads of an actor's own operation receipts.

| Route | Operation |
| --- | --- |
| POST `P/mappings` | Create a mapping and its first admitted immutable revision |
| POST `M/revisions` | Append one immutable revision with any-head or equal-head policy |
| GET `P/revision-ingestions/{operationId}` | Read one original actor's safe receipt without claiming or executing |
| POST `P/revision-ingestions/{operationId}/resume` | Attempt one explicit source-free recovery claim |

## Exact native submission

[Mapping draft saves](mapping-editing.md) use this same ingestion workflow with
server-generated checkpoint bytes, the original actor and an exact-base
equal-head condition. The browser sends only typed operations. Normal receipt,
conflict and recovery semantics apply; draft provenance is reconciled from the
committed receipt.

Create and append accept the original bytes as `application/octet-stream`, with
no media parameters and no request compression. Supply exactly one
`Idempotency-Key` header containing a canonical UUID. HTTP header whitespace
around the value is permitted; lists, duplicates, empty values and internal
whitespace are rejected. Metadata ETags do not select revisions: these routes
reject `If-Match` and `If-None-Match`.

Create requires unique query fields `name` and `backendId`. Optional fields are
`description` and `originalFilename`. Name is trimmed and bounded to 200 Unicode
scalars; description is preserved and bounded to 4000. Backend IDs retain their
exact spelling and 128 UTF-8 byte bound. The display filename is a safe basename
of at most 255 UTF-8 bytes; omission selects `entry.native.json`. Metadata is
strict Unicode without NUL. These fields never select a private storage path or
worker profile.

Append permits `originalFilename` and `head`, which defaults to exact `any`.
`head=equal` requires one canonical `expectedHeadRevisionId`; any-head forbids
it. The mapping's persisted backend is authoritative. Append cannot change its
name, description or backend. Unknown or duplicate decoded query names are
rejected. Percent escapes and UTF-8 are decoded strictly once; `+` means space
and `%2B` means literal plus. Invalid encoding is never replacement-decoded into
different metadata.

Feature enablement, actual transport/token boundary, full scope, strict query and
key policy, and fresh resource authority precede media and body checks. Known
Content-Length above 4,194,304 bytes is rejected. The handler passes the request
stream to the qualified application service without buffering, hashing or
decoding it. Chunked input remains bounded by that service without draining.

The fixed hard ceiling also applies when comparing an existing original key.
Current reduced new-request policy cannot reject a changed complete body before
that durable comparison. Reusing a key with a different bounded body produces
an idempotency conflict; it does not append or overwrite an object.

## Outcomes and original-actor recovery

| Actual result | HTTP status |
| --- | --- |
| Committed valid or decoded-invalid, including replay | 200 |
| In progress with an actual own receipt | 202 |
| Needs source, idempotency/head conflict, exhausted attempts or lost claim | 409 |
| Empty native input | 400 |
| Native byte limit | 413 |
| Unregistered backend, decode, unsupported artifact or resource rejection | 422 |
| Retryable storage/worker failure | 503 |
| Missing, denied or another original actor | 404 |
| Persistence/schema or internal failure | 500 closed category |

JSON replies use explicit closed public contracts and `Cache-Control: no-store`.
Receipts expose complete future scopes, phase, claim count, UTC times, original
filename, committed sequence/admission when present, and a closed latest failure
category. They omit keys, native bytes, private references, profiles, leases,
evidence and provider exception text. A future scope does not prove the resource
exists. Only an actually committed mutation result receives a committed revision
URL. Committed results consistently use 200 because the application service does
not invent a new-versus-replay distinction.

Receipt GET returns 200 for any actually readable phase. It does not infer a
mutation result from private rejection facts. Another Publisher or Administrator
cannot read or resume the original actor's receipt; demotion also removes that
actor's receipt access.

Resume permits no query fields, idempotency key or native body. A nonempty body
is refused after own-receipt admission; unknown-length bodies are probed for one
byte without draining. The application rechecks authority and attempts one claim.
Terminal replay does not open physical storage or invoke a worker. Missing
reserved source becomes NeedsSource, not automatic repair or a retry loop.

One process-local gate admits at most two concurrent create/append/resume service
calls without queuing. Saturation returns 503 `ingestionAdmissionBusy`, with no
invented operation or receipt. Receipt polling uses no work permit. Caller
disconnect aborts the response and retains the application's bounded cancellation
bookkeeping; it does not fabricate a validator result.

## Explicit local host setup

Catalog and ingestion remain independently disabled by default. For the local
Development profile, use the identity/request setup in
[catalog reads](catalog-reads.md#explicit-local-setup), plus the settings below.
Protected Session hosts use [their own composition](session-host.md), not the
Development identity adapter or plain credential delivery.

- `WEFT_REVISION_INGESTION_ENABLED=true`.
- A dedicated `WEFT_POSTGRES_INGESTION_CONNECTION_STRING`; reader, editor and owner
  credentials are never substituted.
- Private storage, explicitly initialized by its owner: `WEFT_PRIVATE_STORAGE_ROOT`,
  a canonical private Linux root outside application and detected Git trees, or
  the Azure Blob settings selected by `WEFT_PRIVATE_STORAGE_KIND=azure-blob`
  ([host selection](private-storage-blob.md#host-selection)).
- Optional `WEFT_NATIVE_BYTE_LIMIT`, a canonical positive decimal no greater than
  4,194,304. It reduces new-request application admission. The actual storage
  adapter retains its hard 4 MiB cap so earlier reserved source objects remain
  verifiable after a current admission reduction.
- An actual configured `WEFT_FERRULE_WORKER_IMAGE` for new Ferrule validation.
  An empty current backend registry still permits receipt reads and terminal
  replay; it does not admit new uploads for an unregistered backend.

The web host never creates or adopts a storage root. The owner setup command is:

```sh
dotnet run --project dotnet/tools/Weft.StorageTool -- initialize ROOT APPLICATION_ROOT [REPOSITORY_ROOT]
```

Use concrete canonical absolute paths and the same user that will run the host.
The command creates only a new marked root beneath an existing trusted parent;
an existing root is refused. Opening is deferred until actual object publication
or verification. Receipt and matching terminal replay therefore do not require a
live filesystem or worker.

The local listener uses HTTP/1 with a 65,536-byte request-line ceiling, including
method and protocol framing, to admit maximum percent-encoded metadata. Host
request-start diagnostics are suppressed so query and native data do not enter
logs. These local settings do not qualify Azure proxy or HTTP/2 URL limits.

No metadata transaction spans request, private storage or worker waits. Immutable
revision admission and diagnostics remain distinct: a decoded-invalid revision
is committed history, without preview or deployment permission. Reports and
[catalog integration](catalog-browser.md) have their own focused contracts.
