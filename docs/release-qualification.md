# Qualified release metadata

Release qualification and activation are separate from producing a build artifact.
These application contracts do not themselves persist a record, register an
endpoint or invoke an Azure API.

## Qualification candidate

`BuildQualification` binds one qualification ID, original actor, immutable build
intent/artifact, executing runtime image, original saved regression result and
exact saved case version. The private compiled result bytes and their digest are
part of the candidate's identity. The canonical metadata digest is not a signature
or a permission grant.

Candidate creation verifies the saved case envelope against its reference and
reopens the original successful regression result. It then recomputes the compiled
output comparison against the saved expected bytes. No caller-supplied passing
flag or most-recent preview output is accepted as a baseline. Missing baselines,
changed bytes, unsuccessful runs or different actors/revisions/runtime provenance
cannot qualify a build.

The first whole-artifact policy requires an `All` target replay, the complete
declared static input set and expected outputs for every primary/named target. A
successful selected-target case does not qualify unexecuted targets. The original
clock, parameters, input hashes and limits are bound through the replay identity.

`BuildQualificationService` recovers an own retained qualification before resolving
current execution facts. New work obtains exact authorized build, case and
regression metadata, invokes the restricted compiled runtime, then asks the store
to retain the candidate. Store implementations must recheck authority and source
identities atomically and preserve first-writer bytes. Recovery does not renew an
approval or authorize activation. The result body is private sample/output data
and must not appear in logs or metadata-only responses.

## Private PostgreSQL evidence

Migration `0021_build_qualification.sql` adds immutable qualification records.
`weft_qualification_reader` and `weft_qualification_writer` are separate
CONNECT-only logins provisioned before migration; neither receives direct table
access. Existing local installations use the explicit administrator step in
`deploy/local/postgres-add-qualification-roles.sh`. Host composition does not
automatically enable qualification or run this provisioning step.

The writer retains the canonical proof and private compiled completion bytes.
Its database function locks the original build authority, checks the confirmed
successful publication, reopens the saved baseline, and independently compares
both original and compiled output lengths and hashes. It checks all declared
inputs/outputs and the exact clock/parameter/input replay digest. Canonical byte
checks reject ambiguous JSON or a rewritten evidence identity. Record mutation,
deletion and truncation are prohibited; an identical write returns the original
proof, while a changed body under that ID conflicts.

New evidence requires current publisher authority and an active original build
approval. Reading an owned qualification requires current publisher authority
but does not require its old approval or environment to remain active. The
reader reconstructs the qualification from the original build, saved case and
regression result and verifies its exact proof/result bytes again. This recovery
does not permit activation. PostgreSQL evidence tests use synthetic artifact
bytes; the actual compiled-runtime test separately supplies evidence from real
isolated execution.

## Release intent

`ReleaseIntent` records one desired activation or rollback. It binds a registered
target ID, full mapping, exact environment version, qualification fingerprint,
build/artifact provenance and expected active release ID. A target ID is not a
URL, filesystem path or credential. Target registrations must have immutable
configuration identities.

An ordinary activation has no rollback source. Rollback requires both a retained
prior release and a nonempty expected-current pointer; it reuses a qualified
artifact rather than recompiling it. This first policy does not silently cross
environment versions or adopt another actor's qualification. An environment change
requires new explicit qualification and release admission, not a rewritten build
intent.

The intent alone has no effect. Persistence and target adapters must distinguish
requested/applying/active/failed/canceled/uncertain state, retain immutable audit
records and recover an original request before retrying an uncertain effect. A
late, canceled or revoked operation must not update the active pointer. Unknown
external outcomes are not successful activation receipts.
