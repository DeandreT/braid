# Durable per-case regression runs

`PostgresRegressionRunStore` retains one explicitly requested run of one
immutable saved case. `RegressionRunExecutor` applies the existing exact-case
execution workflow to a leased run. `RegressionRunReadService` reopens the
retained output and recomputes comparison against the original saved baseline.

The prepared `0015_regression_runs.sql` is separate from the active migration
manifest. It requires the saved-case schema and explicit CONNECT-only
`weft_run_reader` / `weft_run_writer` logins. It does not provision its own roles,
activate an HTTP endpoint, or start a worker.

## Request and result identity

A request fixes the organization, mapping, submitter, client-selected run UUID
and the full saved-case reference. The reference includes the exact case
version, creator, native revision/digest and complete envelope digest/length.
The database verifies those facts against the immutable saved case.

The same submitter retrying the same run UUID and case returns the original
run. A different request using that UUID conflicts, including concurrent
submissions by different actors. A terminal run is never reset or overwritten.
Deliberately rerunning a failed case creates a new run UUID and keeps the earlier
history. Batch callers must keep stable per-case request IDs, retain completed
results, and retry only the work that did not finish successfully.

Outputs are stored in a bounded canonical private completion document. Its
digest covers the complete bytes; reopening also validates the run UUID and
case digest, full output names and byte limits. No stored `passed` flag is
trusted: comparison is recomputed using the case's immutable expected output.
Ordinary failures retain categories, not raw database exceptions or payload
excerpts.

## Lifecycle and recovery

Runs start queued. One worker can claim a run for 90 seconds using an unguessable
token and monotonically increasing attempt. Observation, release and completion
use the exact worker/token/attempt. An expired or superseded attempt cannot
publish a result. Database time, not a worker's local clock, decides eligibility
and expiry.

Transient infrastructure failures release the run with a one-second retry
delay. There are at most three attempts, including abandoned attempts reclaimed
after expiry. Exhaustion is terminal rather than an unlimited retry loop.
Each submitter has at most 32 queued/running runs per organization; an exact
retry does not consume another admission slot.

Cancellation invalidates the current claim and is terminal. The executor
observes claim validity while the preview runs and cancels its execution token
when ownership is lost. Host shutdown cancels execution and attempts a bounded
release. Backend process termination and isolation remain the preview adapter's
responsibility.

Completion commits once. A lost acknowledgment can leave the result committed;
redelivery reads the terminal run instead of executing the case again. A
connection interrupted by database restart is an infrastructure failure, not an
instruction to assume success. Fresh pools can reopen retained requests and
outputs after restart.

## Access and integrity

Submission and private reads require current Runner-or-higher project access.
Reads and cancellation are limited to the original submitter. No project role
here grants access to production payloads. Execution separately rechecks the
saved native source and runtime through the exact-case workflow.

Every security-definer function checks the bound organization before inspecting
or changing run rows. Completion also rechecks the submitter's current
authority. Revoked authority invalidates publication, and fresh read checks drop
private content after access is lost.

The dedicated reader has only the read function; the writer has the explicit
run operations. Neither has direct table or column access. Pool admission checks
role attributes, function bodies/signatures and the schema fingerprint before
binding the current organization/actor. Unexpected grants, triggers, columns,
constraints or functions are refusals, not an implicit schema upgrade.

## Remaining integration boundary

The store is the durable run ledger, not a message transport. Submission does
not yet atomically enqueue a Wolverine message, and no background discovery
loop is started here. A host must wire reliable scheduling/redelivery to
`RegressionRunExecutor`, preserve the per-case boundary, and expose separately
qualified request/status/result UI and HTTP operations. Preview cleanup does not
expire these saved-case results; retention changes require an explicit policy.
