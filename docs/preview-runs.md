# Preview runs

A preview run is an owned, replayable business operation: one bounded sample
preview of an exact immutable revision, submitted by a project member. It records
the submitted inputs, the admitted request, every attempt and the terminal result.
Execution uses the [Ferrule preview worker](ferrule-worker.md#json-sample-preview)
and is delivered durably through Wolverine; HTTP routes and the Angular form are
separate increments.

## Submission and idempotency

`PreviewRunService.SubmitAsync` validates the request against `PreviewPolicy` and
the server's `PreviewRunOptions.Limits`, then stores it with a server-assigned
execution clock. The clock is captured once at submission so every attempt of a
run evaluates with the same value.

The admitted request is a canonical UTF-8 JSON document (`weft.preview-request.v1`)
with the revision scope, target selection, typed parameters, input names, byte
lengths and SHA-256 digests, and the effective limits. Its SHA-256 is the request
fingerprint. A submitter's request key is unique: resubmitting the same key with
an identical fingerprint and revision returns the original run (`Replayed`); any
difference returns `Conflict` and stores nothing.

Only an active principal with active organization and project memberships whose
project role is runner, publisher or administrator can submit. The revision must
belong to that project. Unauthorized submissions and missing revisions return the
same `NotAuthorizedOrNotFound` result.

## Execution and leases

`PreviewRunExecutor.ExecuteAsync` claims one attempt with a database lease
(90 seconds by default, longer than the 30-second worker deadline). A claim
re-checks the submitter's current authority; a run whose submitter lost it
finishes as `authorityRevoked`, a cancellation request finishes it as `canceled`,
and a run with no attempts left finishes as `attemptsExhausted`. A live lease
returns `Busy`; there is no lease renewal.

The executor verifies the stored request and inputs against their declared
lengths and digests, reads the native object from private storage with its exact
digest, and runs the preview backend. While the worker runs, it polls the claim;
a requested cancellation cancels the worker and completes the run as canceled,
and a superseded claim stops without publishing.

Every transition is fenced by the claim's lease token. Completion records the
terminal outcome and, only for success, all output documents in one transaction,
so a duplicate or superseded attempt can never publish a second result set.
`BackendUnavailable` and `IsolationFailure` results, an unreadable native object
and host shutdown release the claim back to the queue, with the configured retry
delay for failures. Every other result, including timeouts, is terminal. Attempts
are bounded by `MaxAttempts` (three by default, at most eight).

States are `Queued`, `Running`, `Completed` and `Canceled`. Completed outcomes are
the preview statuses plus `AuthorityRevoked` and `AttemptsExhausted`. Failure
code, message and subject are bounded worker or adapter text.

## Durable delivery

Wolverine delivers a typed `RunPreview` command (organization and run IDs only)
through a PostgreSQL queue. `OutboxPreviewRunStore` enlists Wolverine's outbox in
the submission transaction, so a newly created run and its command commit
together or not at all; replays and refusals publish nothing. A submitting host
configures `PreviewJobRole.Submitter` (`ServerlessWithOutbox`: outbox only, no
listeners or node agents).

Wolverine generates its handler and outbox code and compiles it when a host
starts (`WolverineFx.RuntimeCompilation`).

`dotnet/apps/Weft.Worker` is the preview worker host (`PreviewJobRole.Worker`,
Wolverine `Solo` durability). It listens with a durable inbox and one message at
a time, because the restricted runner admits one container. A delivery calls
`PreviewRunExecutor.ExecuteAsync`; a released attempt or a busy run is rescheduled
after the retry delay. Redelivery and duplicate delivery are safe because the run
lease fences every attempt and a finished run is never executed again. On restart
the worker reclaims every persisted envelope; an attempt interrupted by a process
kill is claimed again after its lease expires. P7 runs a single worker host;
several hosts need Wolverine's balanced durability mode.

The worker reads its configuration from the environment:
`WEFT_PREVIEW_WRITER_CONNECTION_STRING`,
`WEFT_PRIVATE_STORAGE_ROOT` (an existing v2 root, opened read-only),
`WEFT_FERRULE_WORKER_IMAGE`, optional `WEFT_DOCKER_EXECUTABLE` and optional
`WEFT_PREVIEW_LEASE_SECONDS` (35–600). It runs as the same operating-system user
that owns the private storage root.

Migration `0013_preview_messages.sql` creates the `weft_messages` schema and grants
the preview writer `USAGE` and `CREATE` on it only. Wolverine creates and owns its
envelope, queue and node tables there on startup; Weft tables and grants are not
affected. The writer connection must be direct or session pooled, because
Wolverine's durability agent uses session advisory locks.

## Reads and cancellation

Reads are limited to the submitter and re-check the same current authority as
submission; unauthorized and missing runs are indistinguishable. A snapshot
contains the state, attempts, outcome, failure, provenance (engine revision,
worker image, adapter and profile) and output descriptors. Output bytes are read
individually by ordinal with their digest verified. The submitter can cancel a
queued run immediately or request cancellation of a running one.

## Storage and retention

Interactive preview payloads are bounded (8 MiB of inputs and 8 MiB of outputs)
and short-lived, so inputs and outputs are stored as `bytea` rows in
`weft.preview_documents` beside their run. This keeps the terminal transition and
its outputs atomic. Each row is checked against its SHA-256 and byte count by the
database. Runs carry an expiry (24 hours by default, at most seven days).
Persistent regression fixtures use separate storage with their own retention.

## Database profiles

Migration `0012_preview_runs.sql` adds `weft.preview_runs`,
`weft.preview_documents` and owner-defined `SECURITY DEFINER` functions. Neither
runtime profile has table privileges:

- `weft_preview_writer` executes submit, claim, inputs, observe, complete, release
  and cancel.
- `weft_preview_reader` executes run, output-list and output reads.

Both logins must exist with CONNECT-only provisioning before the migration runs.
New local Compose installations create them from independent
`WEFT_LOCAL_PREVIEW_READER_PASSWORD` and `WEFT_LOCAL_PREVIEW_WRITER_PASSWORD`
values. An existing installation runs
`deploy/local/postgres-add-preview-roles.sh` inside its PostgreSQL container with
both passwords before the coordinated stop/migrate/start upgrade.
`PostgresPreviewDatabase` verifies the exact restricted session identity on every
transaction.
