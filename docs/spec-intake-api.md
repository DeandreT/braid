# Specification intake API

The server maps authenticated intake through `CatalogRequestBoundary`,
`SpecBundleService`, actor-bound PostgreSQL sessions and
`BlobSpecObjectStore.OpenAsync`. The feature defaults off. Each request checks the
exact schema, restricted login, active location binding and private Blob marker.

## Routes and bounded inputs

Base: `/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}`. IDs are nonzero UUIDs. Session requests use the catalog's current session/epoch and `Origin`/`X-XSRF-TOKEN` rules. Optional delegated bearer access uses the same business-route admission and durable mutation audit. Development catalog identity is explicitly refused. Current project content grants and publisher/administrator write authority are checked by the registry, not inferred from transport identity or metadata visibility.

- `PUT` base: strict JSON `{ "creationKey": "<uuid>" }`, at most 1024 bytes. Exact key/bundle replay; returns status and `X-Braid-Spec-Version` mutation CAS version.
- `GET` base: content-authorized inventory, at most 128 object entries with source identities, format, digest/length, publication state and extraction outcome. Includes pending/retiring entries to diagnose interrupted work; contains no physical location, attempt/retirement claims or actor identity. Snapshot version and entries are read within the same bundle lock/transaction. A tombstoned bundle is hidden.
- `POST /uploads`: raw `application/octet-stream`, streamed into bounded capture (10 MiB maximum, including requests without Content-Length). `X-Braid-Spec-Upload` is strict JSON, at most 2048 characters, with all fields below. No multipart filename/path or URL fetching.
- `POST /text`: strict `application/json`, at most 1 MiB encoded body, `{ "upload": { ... }, "text": "..." }`; format must be `text`. Text is encoded as UTF-8 without normalization. Authority is checked before consuming the text body and again at publication. URLs in text are inert evidence.
- `GET /extractions/{objectId}`: exact canonical JSON evidence bytes; originals are refused by this route. Object lookup and physical read independently recheck current authority. No provider or model invocation.
- `DELETE` base, or `/sources/{sourceId}/versions/{sourceVersion}`: `If-Match` and `Idempotency-Key` headers. CAS logical tombstone only; physical retirement remains separately recoverable through the existing maintenance lifecycle.

Upload metadata fields: `sourceId`, positive `sourceVersion`, `originalObjectId`, `uploadKey`, `extractionObjectId`, `extractionKey`, positive `extractionExpectedVersion`, and `format` (`text`, `json`, `openapi-json`). Original/extraction object IDs must differ, as must their attempt keys. Both POST routes require one strong `If-Match`, such as `"1"`, for the original reservation. Typically `extractionExpectedVersion` is 2 for that first upload.

Unknown/duplicate JSON properties, missing/null fields, unsupported format labels, compressed bodies, query parameters, malformed UUIDs/versions and excessive inputs are refused. Source bytes that cannot be parsed are deliberately retained with a `Malformed` extraction outcome; this is different from malformed transport JSON. Extraction uses the existing bounded adapters and preserves unsupported/limit outcomes. All responses are `no-store`/`nosniff`. Errors use fixed codes (400 invalid request, 404 hidden/not admitted, 409 CAS/replay conflict, 413 size limit, 503 unavailable/uncertain), never exception details or private content.

## Replay and concurrent updates

Inventory and status responses do not emit ETags. `X-Braid-Spec-Version` and the body's version are mutation CAS tokens only; publication/retirement acknowledgements may change inventory at the same CAS version. Continue sending the quoted positive CAS version in mutation `If-Match`; it is not a representation validator.

The extractor bounds canonical JSON evidence bytes, including escaping and
framing, at the 10 MiB object ceiling. Exceeding it produces atomic `LimitExceeded`
evidence with code `serialized-evidence-bytes` and no chunks. The original remains
intact, bounded failure evidence is published and exact retries replay it.

The original and extraction are two durable publications, not an atomic pair. Retain **all** IDs, keys, original bytes, format and versions across an uncertain response. A retry replays the original reservation/publication before attempting the extraction. `reservationVersion` in the successful response is a replay-stable acknowledgement, not a claim that no concurrent update has occurred.

If another writer advances the bundle between original and extraction reservation, the extraction CAS can conflict. Read inventory: when the chosen extraction object has no reservation, retry with the same original intent/If-Match and a refreshed `extractionExpectedVersion`. Once the extraction reservation exists, retain its original expected version for exact replay. Never invent replacement IDs merely because an acknowledgement was lost. Tombstoning/revocation stops retries and future reads.

## Server-owned configuration

Session host only, with explicit `BRAID_SPEC_INTAKE_ENABLED=true`:

- `BRAID_POSTGRES_SPEC_READER_CONNECTION_STRING_FILE` and `BRAID_POSTGRES_SPEC_WRITER_CONNECTION_STRING_FILE`: existing protected file delivery. Logins must be `braid_spec_reader` and `braid_spec_writer`; plain credential variables are refused. The server creates resetting pools, not roles, schema or grants.
- `BRAID_SPEC_STORE_ID`: previously provisioned nonzero UUID.
- `BRAID_SPEC_BLOB_SERVICE_URI`, `BRAID_SPEC_BLOB_CONTAINER`: admitted HTTPS account/container.
- `BRAID_SPEC_BLOB_CREDENTIAL`: `managed-identity` or `workload-identity`. Optional `BRAID_SPEC_BLOB_MANAGED_IDENTITY_CLIENT_ID` only for managed identity. No connection string, account key, emulator or filesystem activation in this capability.

The owner-provisioned location digest must equal SHA-256 of UTF-8 `braid.spec-blob-location.v1\n` followed by the admitted service absolute URI without its trailing slash, `/`, and the container name. The host derives this from the actual options; clients cannot supply the fingerprint. The container and its matching spec marker must already exist and be private. The host calls `OpenAsync`, never `InitializeAsync`.

## Tests

Portable tests cover capture limits, revocation before consumption, interleaved
extraction CAS/replay, schema refusal and Development-identity refusal.
`SpecHttpTests` covers bearer/session requests, CSRF, strict inputs, scope/revocation,
uncertain publication and extraction reads over recording business ports. These
HTTP cases require the Linux durable browser key profile.
`PostgresSpecRegistryTests` covers restricted-reader inventory in a disposable
database. The generated HTTP contract is `contracts/braid-v1.json`.
