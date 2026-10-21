# Local release activation

The local release adapter changes a PostgreSQL active-release pointer. It does
not create an Azure resource, install an HTTP endpoint, provision a machine
credential or run an external deployment command.

## Target and request identity

An administrator registers one immutable `LocalReleaseTarget`: a mapping,
environment ID/version, runtime image and artifact profile. Registration checks
the active environment. Disabling a target is one-way; replaying its original
registration returns the original disabled target rather than enabling it.

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

Database tests use synthetic artifact bytes to exercise transaction and authority
rules. Actual mapping invocation and release/rollback selection must also be
qualified against real compiler/runtime output; synthetic metadata alone is not
deployment evidence.
