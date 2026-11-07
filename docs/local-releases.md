# Local release activation

The local release adapter changes a PostgreSQL active-release pointer. It does
not create an Azure resource, install an HTTP endpoint, provision a machine
credential or run an external deployment command.

## Target and request identity

An administrator registers one immutable `LocalReleaseTarget`: a mapping,
environment ID/version, runtime image and artifact profile. Registration checks
the active environment. Replaying its original registration returns the original
disabled target rather than enabling it. The separately authorized
[operations API](release-operations.md) supports conditional enabling and disabling.

A publisher submits an immutable `ReleaseIntent` against a stored qualification.
The qualification must retain that publisher, mapping, exact environment version,
runtime image and complete artifact profile. The request contains the expected
active release ID; it contains no executable, filesystem path or deployment URL.
Submitting a request and applying it are separate operations.

Recovery reads the original release before resolving a new qualification or
current registration. A different body under the same release ID conflicts.
Retained reads require current publisher authority and expose only that actor's
own release and private proof. Target-pointer metadata does not grant access to
another actor's private qualification or samples.

## Atomic local effects and audit

Migration 0022 owns the target pointer, immutable release intent and append-only
audit. `weft_release_reader` and `weft_release_writer` are independent logins with
no direct table access. Existing local databases provision them with the explicit
administrator step in `deploy/local/postgres-add-release-roles.sh` before the
ordered migration. Application startup does not provision credentials.

Each application transaction locks the original authority and build, checks the
current target/approval, and compares the expected active pointer. Only one
competing activation can win. A stale-pointer attempt is retained as failed with
a rejected audit event; it cannot later overwrite the winner. Cancellation and
disabled/revoked targets never update the pointer. Independent requests use
separate transactions, not a shared all-or-nothing batch.

A release has one requested event and, once terminal, one activated, canceled or
rejected event. Deferred database validation requires complete matching audit
at commit. Updating or deleting historical intent/audit is prohibited.

`Active` on a release means its activation completed. Only the separate target
pointer says which release is current. Replaying an older successful activation
returns its history without making it current again. A lost commit response is
recovered by reading or replaying the original release ID, not by replacing it.
The local pointer change and its audit are one database commit, so there is no
separate external effect to reconcile.

## Rollback and invocation

Rollback creates a new release ID. It names an already activated own release on
the same target and retains that release's qualification. It also names the
expected current release, which must differ from the rollback source. It never
recompiles, rewrites a build intent, renews an approval or crosses environment
versions implicitly. Current target and original build authorization still apply.

`LocalReleaseInvocation` resolves the current own active release and executes its
verified compiled artifact through the existing isolated runtime. It checks the
active pointer around private reads, immediately before dispatch and before
delivering output. A concurrent activation, rollback or disable prevents stale
output delivery. Mapping assemblies are never loaded into the server process.

The [mapping endpoint](mapping-endpoint.md) serves machine callers through this
invocation path, under the authority of the current release's publisher, after
[machine authorization](machine-authorization.md) admits the caller's token and grant.
The invocation ledger reads only which publisher's release is current, to select that
authority; the pointer, release and build are read and checked by this path. Activation,
rollback and their audit are unchanged.

## Operator commands

`Weft.ReleaseTool` qualifies builds and changes the target pointer for a
[mapping endpoint](mapping-endpoint.md). It ships in the endpoint image and runs as the
entrypoint's `release` command. Each command acts as one named principal (`ACTOR`), and
the database checks that principal's current authority as for any other caller. The
tool uses restricted logins only, reads every setting from its environment and prints
one JSON line, for example `{"outcome":"activated","releaseId":"…","qualificationId":"…"}`.

```sh
Weft.ReleaseTool qualify-build ACTOR ORG PROJECT MAPPING BUILD QUALIFICATION
Weft.ReleaseTool register-release-target ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET
Weft.ReleaseTool activate-release ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE QUALIFICATION EXPECTED|none
Weft.ReleaseTool rollback-release ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE SOURCE EXPECTED
Weft.ReleaseTool disable-release-target ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET
```

- `qualify-build` runs `BuildQualificationService` for one of the actor's completed
  builds. It replays the build's saved regression evidence through the compiled runtime
  and stores the qualification. It needs `WEFT_COMPILED_RUNTIME_IMAGE`, the compiler
  identity of the build in `WEFT_FERRULE_WORKER_IMAGE`, and one login per setting:
  `WEFT_QUALIFICATION_WRITER_CONNECTION_STRING` (`weft_qualification_writer`), and the
  readers `WEFT_QUALIFICATION_BUILD_READER_…` (`weft_build_reader`),
  `…_BUILD_RUN_READER_…` (`weft_build_run_reader`), `…_ACCESS_…` (`weft_runtime`),
  `…_REPORT_READER_…` (`weft_report_reader`), `…_RUN_READER_…` (`weft_run_reader`) and
  `…_CASE_READER_…` (`weft_case_reader`), each ending in `_CONNECTION_STRING`. The local
  runtime is the Docker compiled runtime, so the command needs Docker access, and
  `WEFT_DOCKER_EXECUTABLE` optionally names the CLI.
- `register-release-target` registers the target with the Ferrule compiler's artifact
  profile and `WEFT_COMPILED_RUNTIME_IMAGE` as its runtime image. It uses
  `WEFT_RELEASE_WRITER_CONNECTION_STRING`; so does `disable-release-target`.
- `activate-release` requests and applies an activation of one of the actor's
  qualifications. `EXPECTED` is the current release, or `none` for the first activation.
  `rollback-release` reactivates an earlier release `SOURCE` of the same target with
  that release's qualification, under a new release ID. Both also use
  `WEFT_QUALIFICATION_READER_CONNECTION_STRING`.

Each login must name exactly its role and an explicit password. Successful outcomes
(`qualified`, `registered`, `activated`, `disabled` and `replayed`) exit 0. A refusal,
conflict, stale pointer or unqualified build exits 3. An unavailable runtime or database
exits 1, as does a compiled-runtime container whose removal the runner could not confirm
(`runtimeCleanupUnconfirmed`); invalid arguments or settings exit 2. Repeating a command
with the same IDs replays its original result, so a qualification stored before a failed
cleanup is not run again.

Database tests use synthetic artifact bytes to exercise transaction and authority
rules. Actual mapping invocation and release/rollback selection must also be
qualified against real compiler/runtime output; synthetic metadata alone is not
deployment evidence.
