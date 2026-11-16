# Provider-neutral server session foundation

These contracts define private session identity, lifetime and conditional store
operations. See the [PostgreSQL store](postgres-server-sessions.md),
[ticket adapter](protected-server-sessions.md) and [Session host](session-host.md)
for their implementations and configuration.

## Private metadata and identity boundaries

`ServerSessionKey` combines a configured organization ID with the validated
SHA256 digest of a cryptographic 256-bit handle. The raw handle is neither
accepted nor generated here. `ServerSessionFacts` retains the exact key, binding
ID and positive binding-version snapshot, independent noncredential login epoch,
issued/idle/absolute times, positive Int64 row version, optional revocation time,
policy/codec version 1, and nonempty opaque ticket bytes bounded to 16 KiB.

Key, organization, binding snapshot, epoch, issue/absolute times, policy/codec,
and exact ticket bytes are immutable. Only idle expiry, row version, and one-way
revocation may change through the store. Ticket bytes copy on admission
and every output; callers cannot mutate the retained backing bytes.

Principal ownership is not duplicated in session metadata. A fresh binding
observation supplies its immutable internal owner and current activity. A create
intent includes the expected owner solely for consistency checking. The bytes
of the protected ticket remain opaque to these metadata contracts.

`CurrentServerSession` is private store metadata, not authenticated request
identity or an `ActorContext`. It grants no organization admission, project role,
project-creation capability, or production-payload access. Catalog operations
retain fresh principal, organization-admission, and exact project-membership
checks. [Payload permission](project-payload-permissions.md) remains a separate
explicit grant intersected with current project authority. Neither a cookie nor
Entra roles/claims can replace those checks.

## Explicit clock and lifetime

The policy takes an explicit observed clock value and consults no ambient clock.
Instants must be finite UTC at microsecond precision; sub-microsecond values and
reserved .NET infinity endpoint sentinels are rejected rather than rounded.
The PostgreSQL store also checks source timestamp finiteness before
materializing .NET values.

Creation uses these fixed bounds:

```text
issued   = observed database time
absolute = min(issued + 60 minutes, verified-login deadline)
idle     = min(issued + 20 minutes, absolute)
```

A deadline at/before issue time cannot create a live session. Valid bounds require
`issued < idle <= absolute`, with at most 60 minutes from issue to absolute.
Arithmetic overflow is a failure, never a saturated deadline or silent denial.
A row is live only when unrevoked, `issued <= now`, and `now` is strictly before
both deadlines. Equality is expired; there is no grace period or remember-me path.

Renewal proposes at most `min(now + 20 minutes, original absolute)`. It cannot
shorten an already later idle deadline when the observed clock regresses but the
row remains live. It changes neither issue time nor absolute expiry. These are
statement-time rules, not a guarantee of a globally monotonic wall clock or a
rollback of previously dispatched work. Framework `TimeProvider` behavior belongs
to adapters and cannot override database admission.

## Observation and conditional operations

`ServerSessionPolicy` classifies prospective metadata decisions. It performs no
lookup, mutation, commit, authentication, or authorization for a later operation.
The reader/writer ports describe what the actual store must establish.

`ObserveAsync` must observe exact organization/handle, live session, enabled
current binding with the issued version, and active binding owner together in one
fresh statement. Missing session, expiry, revocation, disabled/inactive identity,
and binding-version mismatch deny. Returned foreign keys, an orphan joined
identity, or missing parent references are integrity failures. Storage/schema
failures and cancellation remain failures, never successful anonymous state.

Creation has a deliberate order, after required argument/clock validation:

1. A returned wrong binding or immutable owner is an integrity failure.
2. Missing organization or binding, disabled binding, inactive principal, or
   binding-version mismatch returns ordinary denial.
3. Initial lifetime is evaluated: expired verified-login deadlines deny and
   arithmetic overflow fails, before any collision classification.
4. Only a live admitted candidate conflicts with an existing digest, including an
   identical retry or a digest in another organization. Otherwise it is a
   prospective creation of version 1.

The store contract forbids a digest probe or provisional insert for an expired
candidate. A live collision exposes no collided row and never becomes an upsert,
identity rewrite, or silent replay. An ambiguous create acknowledgement remains
an error; reconciliation of an exact known key is explicit.

An inserted candidate needs a second, fresh statement before acknowledgement.
The store closes the initial reader, then observes that exact inserted row and its
current binding/owner/activity with one current database-time sample. All initial
facts must still match; issue/idle/absolute bounds retain their first-statement
anchor. A committed competing insertion gives Conflict. A rolled-back competitor
permits Created only if this final observation is live and admitted. If current
ordinary admission denies, the store undoes its uncommitted insertion through a
savepoint, then returns Denied only after acknowledged outer COMMIT, leaving no
new row. This grants no DELETE privilege or retry. Parent changes after the final
snapshot remain outside this acknowledgement boundary; later reads check anew.

Renewal requires a positive exact expected row version and the same fresh live
identity observation. A stale version conflicts even for a no-op. Unchanged
renewal preserves the version; genuine idle extension increments once. Renewal
never inserts or revives a missing, expired, or revoked row, replaces an epoch or
ticket, or performs a blind retry after conflict.

Revocation is an exact, retained, one-way tombstone in the storage contract.
Missing/already revoked is an idempotent no-op. A first revocation records database
time and increments once, even when the session has expired or its parents are
inactive. It never deletes or reactivates a row or revokes an entire account.
At Int64 maximum, a genuine renewal/revocation change is refused without overflow;
an unchanged replay remains a no-op. A failed revoke is not successful logout.

`ServerSessionWriteResult` represents an acknowledged store result. Its factory
checks coherent result shape; only the actual implementation can establish a
committed write. Success follows COMMIT. Integrity/storage/cancellation and
ambiguous-commit failures remain exceptions. No transaction spans provider, HTTP,
or filesystem work.

## Privacy and store tests

Default fact/key/intent/observation JSON excludes private properties; their
`ToString` output is categorical. A write result exposes only its outcome through
default JSON. Diagnostics and assertion failures must not dump handles/digests,
ticket bytes/text, identity keys, provider claims/tokens, credentials, or raw
exception/request bodies. The redaction tests assert Boolean predicates with
fixed messages so a regression does not itself print private operands.

The [PostgreSQL store](postgres-server-sessions.md) uses restricted separate
reader/writer profiles, exact organization scope
and reset, source/schema/ACL admission, current binding/principal joins, atomic
conditional writes, and retained revocation. Reader privileges remain limited to
necessary session and identity columns. Writer adds only `organizations(id)`
SELECT for the configured organization's existence check, required session INSERT,
and mutable-column UPDATE; neither receives provider-key reads, membership or
artifact access, parent/grant writes, owner powers, deletion, or broad helpers.

The pure vectors cover fixed lifetime boundaries, byte copies/privacy, invalid
facts, identity consistency, collision ordering, stale/no-op/max-version CAS, and
synthetic revoke/renew ordering. PostgreSQL tests cover grants,
RLS, current facts after waits, concurrency, cancellation, lost acknowledgements,
and same-cluster restart.

Fresh verified sign-in rotates the handle and epoch; renewing an existing key
does not rotate either.
