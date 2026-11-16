# Machine authorization

Machine callers invoke a registered release target without a browser session. Each
call needs a validated bearer token for a machine principal and a current invocation
grant that binds that principal to the target. A token or grant never selects a
release, an executable or a deployment path; see
[Local release activation](local-releases.md) for the target pointer.

## Token validation

`IMachineTokenValidator` is the provider-neutral seam. It returns a `MachineCaller`:
the `MachinePrincipal` (provider, tenant and object ID), the client application ID and
the token expiry. It refuses with a closed `Denied` for any token it does not accept,
or `Unavailable` when it cannot obtain the issuer's signing keys. Refusals carry no
token text, claim or provider diagnostic.

The first adapter, `EntraMachineTokenValidator`, accepts Microsoft Entra v2 app-only
access tokens from client-credentials applications and managed identities. Its
`EntraMachineTokenOptions` fix one organizational tenant (not the consumer tenant)
and one audience, the application (client) ID of the endpoint's own app registration.
The endpoint's app registration must accept v2 access tokens.

- Discovery and signing keys use the same fixed tenant endpoints, key-scope
  admission and backchannel as the [Entra browser client](entra-browser-client.md).
  The standard configuration manager refreshes automatically twice a day; an unknown
  signing key requests at most one refresh per five minutes, and last-known-good
  configuration is admitted again before use.
- Standard validation requires an RS256 signature from an admitted key, the exact
  issuer `https://login.microsoftonline.com/{tenant}/v2.0`, the exact audience and
  finite `exp`/`nbf` with no clock skew.
- The adapter then requires `tid` equal to the tenant, `ver` `2.0`, a string `aud`,
  `iat`, and no duplicate claims. An app-only token has no `scp` claim and, where the
  optional `idtyp` claim is configured, `idtyp` `app`.
- The caller is the canonical `oid`; `sub` must equal it. The client application
  is `azp`; a v1 `appid` claim, when present, must equal it. Roles, groups and
  display claims grant nothing.

The Session host's [bearer access](api-authentication.md) validates signed-in users'
delegated tokens with the same discovery, key refresh and strict JWT rules. Each
validator accepts only its own token type: this one refuses any token with `scp`,
and the delegated validator refuses app-only tokens.

There is no issuer, key, authority or transport override in production code. A test
issuer is reachable only the way hosted acceptance reaches its sign-in issuer:
`login.microsoftonline.com` resolves to it and the process trusts only that run's
certificate authority. In-process tests use the internal HTTPS fixture transport of
the Entra protocol tests.

## Invocation grants

Migration `0024_machine_invocation.sql` adds `braid.invocation_grants` and their
append-only audit. A grant binds one machine principal (provider, tenant and object
ID) to one registered release target. A principal has at most one active grant per
target.

- A current project administrator creates and revokes grants, with the same authority
  as target registration. Each change locks the administrator's scope and the
  target and checks the role again in the database.
- A grant requires an enabled target. Replaying a grant ID returns the original
  grant; a different principal or target under that ID conflicts.
- Revocation is one-way. Replaying the original grant does not enable it again; a new
  grant ID is needed. Revoking a grant on a disabled target still applies.
- Each grant has one granted and, once revoked, one revoked audit event, checked
  complete at commit. Updating or deleting grants or audit is prohibited.

`braid_invocation_grant_writer` is a CONNECT-only login that can only execute the grant
write and read functions. Grant administration runs through
`Braid.DatabaseTool` with `BRAID_INVOCATION_GRANT_WRITER_CONNECTION_STRING`, as one named
administrator principal whose current role the database checks:

```sh
Braid.DatabaseTool grant-machine-invocation ADMIN ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET GRANT TENANT OBJECT
Braid.DatabaseTool revoke-machine-invocation ADMIN ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET GRANT
Braid.DatabaseTool list-machine-invocation-grants ADMIN ORG PROJECT MAPPING ENVIRONMENT VERSION TARGET
```

The listing is empty for a principal without the administrator role.

## Invocation ledger

The same migration adds the ledger that the machine-facing endpoint uses through
`braid_invocation_writer`, a second CONNECT-only login that can only claim, finish and
purge. Neither login has table privileges; owner-defined functions pinned in the
migration are admitted again at runtime, together with the release contract they read.

- **Claims.** A request is identified by its target, caller and caller-supplied request
  ID, with a digest of the request. A claim requires an active grant on an enabled
  target and a current activated release, and returns that release's publisher. The
  publisher only selects the authority under which the existing release invocation
  path reads the pointer, release and build; that path still makes every check. The
  same ID with a different digest or client application conflicts. Concurrent first
  claims for one request admit one attempt; each other claim reports it in progress, or a
  conflict for a different body.
- **Attempts.** A claim starts an attempt fenced by a claim ID and a 120-second lease.
  A live lease reports the request as in progress. A finish with an outdated claim, or
  after its lease has expired, writes nothing and retains no response. Unavailable, busy and release-changed results release the attempt,
  and an expired lease is recorded as interrupted, so the same ID can run again. After
  eight attempts the request is recorded as exhausted.
  Lease and retained-response expiry checks use the current clock after any lock
  wait, not the statement's earlier start time. A new or reclaimed lease starts
  when its claim is written; waiting cannot extend an earlier attempt's authority.
- **Outcomes.** Completed and failed outcomes are final. Replaying the request returns
  the recorded outcome and never runs it again. A grant revoked or a target disabled
  during an attempt releases it without delivering or retaining a result.
- **Audit.** Each attempt appends a claimed event and one completed, failed or released
  event with the caller's grant and client application. Finished events record the
  exact release, qualification, build, build intent digest, artifact digest, revision,
  runtime image, clock and response digest. The ledger verifies that provenance
  against the activated release and its build. It records the request digest, never
  request or response bytes.
- **Retention.** The response of a completed request is retained privately, separate
  from the audit, for 24 hours. Only the same caller, with an active grant on the
  enabled target, can read it. After expiry the request still replays as completed
  but without its response, and the endpoint's purge removes the retained bytes; the
  audit remains.

## Provisioning

`deploy/local/postgres-init.sh` creates both restricted logins. The migration tool
installs their schema and function grants. Application startup does not provision
credentials.
