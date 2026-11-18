# Local release activation

The local release adapter changes a PostgreSQL active-release pointer.

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
audit. `braid_release_reader` and `braid_release_writer` are independent logins with
no direct table access. Provision the logins before applying the schema.

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

On a local Development instance, the [local sandbox](local-sandbox.md) runs the same
qualification, registration and activation in one step from the web app.

`Braid.ReleaseTool` qualifies builds and changes the target pointer for a
[mapping endpoint](mapping-endpoint.md). It ships in the endpoint image and runs as the
entrypoint's `release` command. Each command acts as one named principal (`ACTOR`), and
the database checks that principal's current authority as for any other caller. The
tool uses restricted logins only, reads every setting from its environment and prints
one JSON line, for example `{"outcome":"activated","releaseId":"…","qualificationId":"…"}`.

```sh
Braid.ReleaseTool qualify-build ACTOR ORG PROJECT MAPPING BUILD QUALIFICATION
Braid.ReleaseTool export-package ACTOR ORG PROJECT MAPPING BUILD > mapping.package
Braid.ReleaseTool stage-package ACTOR ORG PROJECT MAPPING BUILD QUALIFICATION < mapping.package
Braid.ReleaseTool register-release-target ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET
Braid.ReleaseTool activate-release ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE QUALIFICATION EXPECTED|none
Braid.ReleaseTool rollback-release ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE SOURCE EXPECTED
Braid.ReleaseTool disable-release-target ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET
```

`export-package` is the exception to JSON output: it writes only the framed binary
package to redirected stdout. Use a private directory, `umask 077` and a new file;
discard partial output on nonzero exit. Export requires the six qualification reader
logins, current build admission and an own successful published build. The frame is
a four-byte big-endian manifest length, the canonical v1 manifest, then exactly the
declared DLL bytes. There are no archive paths or extra entries.

`stage-package` uses the same settings and regression checks as `qualify-build`.
It rejects truncated, changed or trailing bytes and binds the supplied manifest to
the independently authorized publication before executing the supplied DLL in the
selected isolated runtime. An exact retry can recover the retained qualification
without dispatching again. Neither command changes the active target. Use the
qualification ID with `activate-release`; rollback retains the original proof.
For these local targets PostgreSQL already holds the immutable serving bytes. There
is no second writable DLL copy or per-replica install to reconcile. A disconnected
host with no access to that publication cannot import a package on its own authority.

### Multi-replica targets

Use `register-replica-target` instead of `register-release-target` for a serving
pool. Supply the same target arguments and set `BRAID_REPLICA_VERIFIERS` to the
comma-separated lowercase SHA-256 hashes of 1–32 distinct random 32-byte replica
credentials (hash the raw bytes, not their hex text). The administrator registers
the target and its complete roster in one transaction. Reordering the same roster
is safe. An existing roster cannot be reduced; changing membership or rotating
credentials requires a new target. Legacy single targets keep their original gate.

On **each** replica, use `stage-replica ACTOR ORG PROJECT MAPPING ENVIRONMENT VERSION
TARGET BUILD < mapping.package`. Supply the qualification settings, release writer
login and only that replica's `BRAID_REPLICA_CREDENTIAL` (64 hex characters). Run it
with the serving replica's pinned local Docker runtime; pooled and remote runners
are refused because their replies do not authenticate an individual serving member.
The credential authenticates the replica; a publisher or another member cannot
substitute a replica name. A successful `ready` result binds an independent, fresh
qualification to that member. The DLL remains in the authorized shared artifact store.

Activation and rollback both require every member to have qualified the **same
published build** on the target runtime within the last ten minutes (database
time). Any one of those qualifications can be the release qualification. The
database rechecks readiness under the target lock at application, not just at
request time. Missing or expired readiness never displaces the active release.
Rerun staging to renew; `withdraw-replica ACTOR ORG PROJECT MAPPING ENVIRONMENT
VERSION TARGET` invalidates this publisher's receipts for this member before a
planned removal. A crashed replica's receipt expires; it is not a live health probe.

- `qualify-build` runs `BuildQualificationService` for one of the actor's completed
  builds. It replays the build's saved regression evidence through the compiled runtime
  and stores the qualification. It needs `BRAID_COMPILED_RUNTIME_IMAGE`, the compiler
  identity of the build in `BRAID_FERRULE_WORKER_IMAGE`, and one login per setting:
  `BRAID_QUALIFICATION_WRITER_CONNECTION_STRING` (`braid_qualification_writer`), and the
  readers `BRAID_QUALIFICATION_BUILD_READER_…` (`braid_build_reader`),
  `…_BUILD_RUN_READER_…` (`braid_build_run_reader`), `…_ACCESS_…` (`braid_runtime`),
  `…_REPORT_READER_…` (`braid_report_reader`), `…_RUN_READER_…` (`braid_run_reader`) and
  `…_CASE_READER_…` (`braid_case_reader`), each ending in `_CONNECTION_STRING`. The local
  runtime is the Docker compiled runtime, so the command needs Docker access, and
  `BRAID_DOCKER_EXECUTABLE` optionally names the CLI.
- `register-release-target` registers the target with the Ferrule compiler's artifact
  profile and `BRAID_COMPILED_RUNTIME_IMAGE` as its runtime image. It uses
  `BRAID_RELEASE_WRITER_CONNECTION_STRING`; so does `disable-release-target`.
- `activate-release` requests and applies an activation of one of the actor's
  qualifications. `EXPECTED` is the current release, or `none` for the first activation.
  `rollback-release` reactivates an earlier release `SOURCE` of the same target with
  that release's qualification, under a new release ID. Both also use
  `BRAID_QUALIFICATION_READER_CONNECTION_STRING`.

Each login must name exactly its role and an explicit password. Successful outcomes
(`qualified`, `registered`, `activated`, `disabled` and `replayed`) exit 0. A refusal,
conflict, stale pointer or unqualified build exits 3. An unavailable runtime or database
exits 1, as does a compiled-runtime container whose removal the runner could not confirm
(`runtimeCleanupUnconfirmed`); invalid arguments or settings exit 2. Repeating a command
with the same IDs replays its original result, so a qualification stored before a failed
cleanup is not run again.
