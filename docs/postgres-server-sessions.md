# PostgreSQL server-session metadata

The implementation for [P4c1b #106](https://github.com/DeandreT/weft/issues/106)
passes locked restore, formatting verification, Release build and all 33 focused
PostgreSQL cases, with no failures or skips. Full backend and browser regression
qualification remains pending. It follows qualified, merged
[payload permission #93](project-payload-permissions.md) and session contracts #97.
Migration `0010_server_sessions.sql` has SHA256
`73b4713aa5e2bafd886abc616c6e03dbf3bb6544cb24d55ea26b8134209f0e24`;
all nine preceding migration bodies remain unchanged.

This is dormant private metadata. It adds no sign-in, cookie, `ITicketStore`,
ticket codec, Data Protection key ring, provider integration, host registration,
HTTP route or Angular behavior. Synthetic protected-ticket bytes in store tests
qualify metadata only; they do not establish cryptographic protection or working
authentication.

## Persisted facts and lifetime

`weft.server_sessions` has exactly thirteen private columns:

| Columns | Meaning |
| --- | --- |
| `handle_digest` | Globally unique 32-byte SHA256 handle digest; the raw handle is not stored. |
| `organization_id` | Exact server-configured organization, with an existing-organization FK. |
| `binding_id`, `binding_version` | Immutable external binding ID and positive issued-version snapshot. The version is not an FK to mutable current version. |
| `login_epoch` | Independent nonzero, noncredential UUID; it is not the handle or a payload capability. |
| `issued_at`, `absolute_expires_at` | Immutable issuance and fixed absolute deadline. |
| `idle_expires_at` | Bounded forward-only idle deadline. |
| `version` | Positive Int64 row version. |
| `revoked_at` | Nullable, one-way revocation timestamp. |
| `policy_version`, `codec_version` | Fixed supported version 1 for each contract. |
| `protected_ticket` | Immutable opaque blob of 1–16384 bytes; this store does not decode it. |

The binding FK preserves P4a's immutable owner. No duplicate principal owner,
provider key/subject/claim/token, email, role, membership, project permission,
key-ring material, payload or native bytes are persisted in the session row.

Policy version 1 uses finite UTC timestamps at exact microsecond precision,
within the supported CLR range and excluding endpoint sentinels. At creation,
absolute expiry is the earlier of the first database sample plus 60 minutes and
the verified-login deadline. Initial idle expiry is the earlier of that sample
plus 20 minutes and absolute expiry. Equality with either expiry denies. There
is no remember-me, grace period, clock override, saturation or automatic revival.
Issuance must precede idle expiry, and idle expiry cannot exceed absolute expiry.

Session validity does not supply organization admission, a project role,
`createProjects` or `run.payload.read`. Each later business operation retains its
own fresh authority checks. Revoking a business admission need not revoke the
identity session, and a session observation does not promise that previously
dispatched work will be rolled back.

## Separate restricted profiles and ownership

Use two independently credentialed `PostgresServerSessionDatabase` instances:

```csharp
await using var readerDatabase = new PostgresServerSessionDatabase(
    readerConnectionString, ServerSessionDatabaseProfile.Reader);
await using var writerDatabase = new PostgresServerSessionDatabase(
    writerConnectionString, ServerSessionDatabaseProfile.Writer);
var reader = new PostgresServerSessionReader(readerDatabase);
var writer = new PostgresServerSessionWriter(writerDatabase);
```

The caller owns and disposes each database/pool. The reader and writer wrappers
accept the matching profile and do not own or dispose that database. Each pool
is lazy, normalized and resetting; do not share one pool between profiles or
substitute a migrator credential.

| Relation | `weft_session_reader` SELECT | `weft_session_writer` SELECT |
| --- | --- | --- |
| `server_sessions` | All 13 columns | All 13 columns |
| `external_identity_bindings` | Exactly `binding_id`, `principal_id`, `enabled`, `version` | The same four columns |
| `principals` | Exactly `id`, `active` | The same two columns |
| `organizations` | None | Exactly `id` |
| Total | 19 column privileges | 20 column privileges |

The writer additionally receives INSERT on the thirteen session columns and
UPDATE only on `idle_expires_at`, `version`, `revoked_at`. All column privileges
are non-grantable. The writer-only organization read supports a closed creation decision for a missing
configured organization; it grants no organization admission. Neither
profile can read provider-key columns, memberships, project/payload grants,
artifacts or the migration journal. There are no table-level privileges, parent
writes, DELETE/TRUNCATE, helper EXECUTE, role memberships, ownership, CREATE/TEMP
or RLS bypass. Existing runtime profiles receive no session-table access.
P4a's strict binding ACL admission permits only the four named columns for each
of these exact new roles; its old identity-reader rights stay unchanged.

Every operation leases a bounded Read Committed transaction, read-only for the
reader and read-write for the writer, with local 1-second lock and 5-second
statement limits. It rejects preset organization context, binds the exact trusted
organization transaction-locally and freshly validates the environment, both
profiles and source-owned schema/ACL contract. Organization RLS supplements the
explicit key predicates. Drift is a failure, never a reason to adopt broader
rights or reuse an earlier positive result.

The selected transition function is read-only, `STABLE`, `SECURITY INVOKER` and
uses `statement_timestamp()` from its caller statement. It does not take
privileged parent locks or resample a volatile clock. Installed catalog forms,
STABLE invocation/snapshot behavior and exact SQL transitions remain actual
PostgreSQL qualification work; a source declaration is not that proof.

## Observation, creation and acknowledgements

`ObserveAsync` uses one fresh exact organization/digest statement joining the
session to its current binding version/enabled state and active principal. A
missing, expired, revoked, disabled, inactive or version-mismatched session
returns null. Broken references, wrong returned identity, malformed facts/schema,
storage failure and cancellation remain failures. A positive result is private
current metadata, not authentication, a lease or cached authorization.

The selected `CreateOnlyAsync` correction has these explicit stages:

1. After transaction preparation, enclose creation in a source-owned savepoint.
   Npgsql can queue `SAVEPOINT` with the following SQL; completion of that
   preparatory API call alone is not a separate server acknowledgement.
   One admission-and-INSERT statement checks returned binding/expected-owner
   integrity before ordinary missing-organization/identity denial, then checks
   initial lifetime before global digest arbitration. An initially expired
   candidate denies without probing the digest or inserting provisionally;
   arithmetic overflow is a failure.
2. Only an admitted candidate reaches
   `INSERT ... SELECT ... ON CONFLICT DO NOTHING`. A committed competing digest
   yields categorical `Conflict`, without exposing, adopting or replaying that
   row, even if it belongs to another organization. No fresh handle is generated
   and no automatic retry follows.
3. If the competing insertion rolls back, this candidate may insert. Every
   inserted candidate, including an uncontended one, closes the initial reader
   and receives one new exact-key session/binding/principal observation with one
   finite statement-time sample. Validate the complete inserted version-1,
   unrevoked facts and original expected owner/binding before ordinary current
   denial. Only a still-live, currently admitted candidate may become `Created`.
4. Expiry or ordinary identity invalidation at that final observation rolls back
   to and releases the savepoint, removing only this operation's uncommitted
   insertion. `Denied` still requires acknowledged outer COMMIT. Healthy final
   admission can become `Created` only after that COMMIT. A committed collision
   similarly requires COMMIT before returning `Conflict`.

The first statement's issue/idle/absolute lifetime remains immutable through any
arbiter wait and the final observation. Remaining TTL is conservatively shorter;
it is never reset to the later sample. Savepoint undo adds no DELETE privilege.
Integrity failure, cancellation, rollback/release failure or failed/ambiguous
COMMIT remains an exception rather than an acknowledged denial or success.

This protocol deliberately strengthens the earlier single-statement creation
source. It does not claim that the original DAL already supplied the final
observation/undo behavior. The corrected protocol has passed independent source
review and the focused PostgreSQL checks; complete regression qualification
remains required for release.

Parent changes committed before the final observation are seen. Changes after
its snapshot are not fenced through COMMIT: there is no parent-row commit fence.
Arbitrary direct writer SQL retains the trigger's statement-snapshot contract;
the additional final-acknowledgement protocol belongs to this DAL. Every later
Observe rechecks current identity.

## Renewal, revocation and failures

`RenewAsync(key, expectedVersion)` first locks only the exact scoped session
row, then executes a new final statement with fresh time, binding and principal
facts after any wait. It requires a live row and positive exact version before
deciding a no-op. A stale version conflicts even if the proposed deadline would
be unchanged. An unchanged result preserves version; a genuine bounded idle
extension increments once, retaining original identity, epoch, issue/absolute
deadline, policy/codec and exact ticket bytes. It cannot insert or revive.

`RevokeAsync(key)` also locks the exact scoped row and rechecks it in a new final
statement. It remains possible for expired rows or disabled/inactive parents;
there is no live-reader prerequisite or caller-supplied expected-version
argument. Missing/already revoked returns unchanged. The first revocation
increments once and records statement time while preserving all issued facts and
idle expiry. Tombstones cannot be deleted or truncated.

At Int64 maximum, a genuine change returns `VersionExhausted`; an allowed exact
same-state no-op remains unchanged. Exhaustion is not successful logout.
Every returned outcome, including no-op and refusal, follows acknowledged COMMIT.
No transaction spans HTTP, provider, ticket decoding or filesystem work. No
operation automatically retries. A lost commit acknowledgement can leave a
committed row or revocation while the original call still fails. Explicit later
exact-key observation is separate reconciliation, not acknowledgement of the
failed call or proof that a browser received a cookie.

Caller cancellation remains cancellation. Operational/schema/integrity failures
are categorical and do not include raw provider errors or connection text.
Never log or attach handles/digests, protected-ticket bytes, private identifiers,
provider facts, SQL, credentials or raw exception/request dumps. Store metadata
cannot be used as a public session-enumeration or grant-management API.

## Local initialization and existing-volume upgrade

Follow [project ownership setup](project-ownership.md#migrations-and-local-setup)
and the earlier identity/payload role prerequisites. The selected local compose
profile requires two separate private values:
`WEFT_LOCAL_SESSION_READER_PASSWORD` and `WEFT_LOCAL_SESSION_WRITER_PASSWORD`.
Keep administrator/migrator and all runtime credentials separate. New-volume
initialization creates the two logins with CONNECT only; the session migration
later supplies their precise schema/column rights. It creates no session,
principal, binding, membership or payload-grant row.

Changing an environment file does not rerun initialization on an existing
volume. Once this increment is integrated and released, upgrade an existing
qualified nine-migration payload-permission database with coordinated
stop/migrate/start:

1. Stop application processes/pools using that database and retain the volume.
   Complete the earlier role prerequisites first.
2. In the intended local PostgreSQL administrator environment, explicitly run
   `deploy/local/postgres-add-session-roles.sh` with the two private password
   variables and `POSTGRES_USER`/`POSTGRES_DB`. This script is for the compose
   `weft` database. It creates both exact CONNECT-only logins in one transaction.
   If either role already exists, the whole transaction refuses: it does not
   adopt, alter or rotate a role, and it does not leave the other newly created
   role behind. Investigate a refusal rather than silently broadening rights.
3. Run the existing DatabaseTool `migrate` operation with the separate private
   `WEFT_MIGRATION_CONNECTION_STRING`, then restart only after migration succeeds
   with each process's distinct restricted credential. For a source checkout,
   the tool entry is:

   ```sh
   dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- migrate
   ```

The new migration requires both roles to exist CONNECT-only before creating any
session object, grant or journal entry. Missing or broadened profiles refuse;
the migration does not create, repair or adopt roles. The exact migration
name/checksum is recorded above. Old migration bodies/checksums must not be edited
to accommodate the new roles.

Fresh, concurrent and repeated install plus a genuinely populated nine-prefix
upgrade must preserve old native/catalog/report history, active/disabled identity
bindings, active/revoked payload grants, all old rows, definitions, journal
checksums/creation times, ACLs and effective rights of old roles and PUBLIC. The only intended additions
are the session objects, exact new-role grants (thirteen old-column SELECT
entries and forty-two session-column entries), four precisely identified incoming
parent FK triggers and the new journal entry. Missing-role and broadened-role
install refusals must prove the same conservation with no new objects or
partially adopted install. Synthetic historical ticket/native/report metadata
does not claim a worker, codec or naturally elapsed lifetime.

## Qualification boundary

All 33 focused PostgreSQL cases pass on the host integration build, covering
installation/upgrade conservation, restricted rights and drift, actual schema and
STABLE invocation, lifetime/concurrency/cancellation, lost acknowledgement,
reset/restart and ordinary resource cleanup. Full discovery finds 3,018 cases.
The clean build, full backend regression and fresh browser workflows remain
separate pending qualification. This document claims no cookie, provider,
hosted sign-in or Azure qualification. Later trusted issuance must freshly supply verified binding facts,
a random handle digest, independent epoch and protected minimal-ticket bytes;
this foundation neither generates those credentials nor activates their use.
