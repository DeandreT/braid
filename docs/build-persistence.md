# Durable build approval and artifact storage

Migration 0018 adds independent `braid_build_reader` and `braid_build_writer`
profiles. Both are CONNECT-only logins before migration. The installed profiles
can call only the bounded owner-defined build functions; neither can read or
mutate tables directly. Provision both logins before applying the schema.

## Environment and approval identity

An administrator creates a versioned build environment with one exact runtime
profile. A new environment version deactivates the prior version rather than
rewriting it. Publishers and administrators can approve an immutable build
intent for a current environment. The approval binds the original actor, full
intent bytes/fingerprint, native revision/object identity and regression result
digest. Revocation never removes the audit record.

Approval insertion enforces the same canonical bytes as `BuildIntent`, not only
equivalent JSON. The database serializer preserves the fixed field order,
default .NET JSON escaping and UTF-16 ordinal boundary ordering; the application
decoder independently reserializes and compares. Whitespace, reordered or
duplicate properties cannot occupy an approval ID with an unreadable intent.

`BuildControlService` reloads current project permission, environment,
authoritative native metadata and the saved regression result. Regression
comparison is recomputed, and backend, engine revision, backend build and adapter
identity must match. The database independently compares output hashes/lengths
against the immutable baseline and rejects absent or malformed content. A
persisted pass flag is not accepted as evidence.

## Durable first-writer staging

The first stage stores exact artifact bytes and their hash before object
publication is acknowledged. Artifacts are limited to 32 MiB and the approved
profile's smaller limit. Competing stages for the same intent return those first
bytes. An uncertain stage, object or final publication can therefore be retried
without recompiling nondeterministic output.

`PostgresBuildObjectStore` serves derived build objects directly from this private
database storage. Native input reads delegate to the existing native store.
Build artifacts are not inserted into the filesystem maintenance namespace, so
filesystem cleanup cannot race their publication or retention.

States advance only from staged to object-published to committed. Actor, intent
and artifact data are immutable. Publication rechecks current project access,
environment version, approval and regression identity inside the mutation
transaction. Reads make the same authorization checks. A revoked approval or
superseded environment makes its stage inaccessible but leaves the bytes
retained. Such permanently invalidated stages do not consume the active-stage
admission limit; retention/deletion requires a separate explicit policy.

## Composition

Use an actor-bound `PostgresBuildObjectStore` with the corresponding build
workflow invocation. Do not reuse one actor's object-store wrapper for another
principal. Reader credentials cannot approve, stage, publish or revoke.
Environment creation is administrator-only; other build operations require
current publisher/administrator access and original-actor ownership.

See [build workflow](build-workflow.md) for compiler and publication composition.
