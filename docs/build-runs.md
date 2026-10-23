# Durable build execution

The immutable build ID is also the durable run ID. Submission binds the original
actor and exact canonical intent to its existing approval. An identical retry
returns the original run; another intent or actor cannot replace it or reset its
attempt budget. Browser submission and environment/approval HTTP operations are
separate from worker composition.

## Queue and authority

Migration 0019 adds a retained `build_runs` ledger and three independent profiles:
`weft_build_run_reader` reads the actor's run metadata, `weft_build_run_writer`
submits or cancels that actor's runs, and `weft_build_dispatcher` discovers and
executes committed work. None has direct table or column access. Discovery is
unscoped and machine-only, returning at most 32 organization/build ID pairs;
claims load the actor and approved intent from the database, not a worker's
asserted identity. The worker's serial pump takes at most four IDs per discovery
page and polls every two seconds.

Fresh publisher/administrator authority is required. Admission allows 32 active
runs per actor and organization; permanently revoked approvals and superseded
environment versions do not consume that active admission count. Existing stages
and completed artifacts retain their independent ownership.

Claims have a database-clock 90-second lease, renewed every 15 seconds, with at
most three attempts. Each attempt also has a six-minute database and process
execution deadline; renewal never extends that overall deadline or resurrects an
expired claim. Every transition checks the token, worker, attempt, original actor
and full intent after taking the authority locks. One failed build cannot stop
later independent work on the discovery page.

## Publication and cancellation

The claim-bound `PostgresBuildStore` carries private transaction-local worker,
token, attempt and intent settings. A trigger checks the current run under the
same database transaction as every real stage, object-publication or final
publication mutation. Cancellation or lease loss therefore prevents a stale
worker from advancing publication, even when it ignores a cancellation token.
Already retained bytes are not deleted. Standalone library invocations without a
queued run preserve the existing build workflow; they cannot mutate a queued run's
publication without its current claim.

Completion independently requires fresh authority and the exact committed
publication, including its digest and length. A stage or object-published record
is not a successful build. Terminal results are immutable. Requested cancellation
before claiming records attempt zero and no artifact.

Transient failures release work within the finite attempt budget. If the final
attempt loses its acknowledgment after publication, the retained result is
`Uncertain`, not a fabricated success. Its exact artifact remains retained and
the queue does not automatically compile or submit a replacement. Without a final
publication, the exhausted run remains a nonpassing terminal result.

## Installation and worker configuration

Fresh provisioning includes the three run profiles. Existing local installations
apply `deploy/local/postgres-add-build-run-roles.sh` explicitly with an
administrator before migrating. Applications never create their own credentials
or schema. Schema18 controls retain their exact function bodies; when schema19 is
present, both old controls and new run profiles validate the extended schema and
publication fence before operating.

`Weft.Worker` remains the ordinary preview worker. Its build component is off by
default and reads no build credentials or creates build services until
`WEFT_BUILD_WORKER_ENABLED=true`. Enabled composition additionally requires:

| Environment input | Restricted database profile |
| --- | --- |
| `WEFT_BUILD_DISPATCHER_CONNECTION_STRING` | `weft_build_dispatcher` |
| `WEFT_BUILD_WRITER_CONNECTION_STRING` | `weft_build_writer` |
| `WEFT_BUILD_ACCESS_CONNECTION_STRING` | `weft_runtime` |
| `WEFT_BUILD_REPORT_READER_CONNECTION_STRING` | `weft_report_reader` |
| `WEFT_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING` | `weft_run_reader` |
| `WEFT_BUILD_CASE_READER_CONNECTION_STRING` | `weft_case_reader` |

With the default Docker runner, `WEFT_FERRULE_WORKER_IMAGE` is the exact qualified
combined image used by the approval's regression evidence. With
`WEFT_FERRULE_RUNNER=service` or `azureContainerAppsSessions`, the build component
uses that runner's compile profile instead and requires
`WEFT_FERRULE_COMPILE_TOOLCHAIN=sha256:<digest>`. The service must explicitly enable
compilation, and the configured pin must match its complete toolchain identity.
No Docker socket is needed for these service-backed builds.

The native worker setting stays the service's native identity. Every participating
Server and Worker uses the same compile-toolchain pin: report provenance,
regression evidence, build approvals and artifacts all use the combined native
and toolchain identity. A toolchain pin with a Docker or inspection-worker
selection is refused. A different pin is not adopted from the service's response.

The build component shares one
compiler-profile runner for process-wide concurrency and cleanup ownership, while
constructing controls and private-object wrappers for each claimed actor.
Successful process startup alone is not build readiness: qualify a real queued
build through compilation, private publication and durable completion.

See [Build persistence](build-persistence.md), [Build workflow](build-workflow.md)
and [Restricted compiler](build-compiler.md). Compiled-runtime output equivalence,
deployment and Azure endpoint authorization remain separate target boundaries.
