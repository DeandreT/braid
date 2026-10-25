# Account display names

The optional account label is provider-supplied presentation text, not a unique
identifier, verified legal name, email address, account-matching key or authority.
The account menu uses Angular text interpolation and falls back to **Account**.
Organization names remain a separate workspace label.

The Entra adapter reads a scalar `name` from the cryptographically validated ID
token only at `TicketReceived`, after the standard nonce and response checks.
After resolving the tenant-stable external identity to an existing enabled
binding, the authentication owner records only the optional label and an
observation timestamp. It does not persist tokens or bulk provider claims.
Missing, blank, malformed, control/bidi-control-containing or oversized labels
become null; the limit is 200 Unicode scalar values, without truncation or
normalization. A later successful sign-in refreshes or clears the label. The
conditional PostgreSQL upsert prevents an older observation overwriting a newer
one, including concurrently completing writes.

Metadata is keyed by organization and immutable external binding, not by name or
principal alone. Authenticated session bootstrap freshly reads the current
protected server session and its binding before looking up that exact metadata
key. Anonymous and authentication-required status always return null. This does
not add ticket claims, cookie fields, permissions or identity-context input;
existing sessions can observe updated labels without changing their identity
context. The browser clears the label with its session boundary and before
dispatching sign-out or another sign-in.

`AuthSessionV1.displayName` is a required nullable JSON field. All clients using
the strict status shape must be deployed with the matching generated contract.
Non-session status remains the two-field mode response.

## Optional delivery and upgrades

When `WEFT_ACCOUNT_DISPLAY_CONNECTION_STRING_FILE` is absent, Session mode does
not create the metadata pool and always returns null labels. This is the safe
default for existing deployments and the current Azure templates. No credentials
are borrowed from the identity reader, session writer or migration owner.

To enable labels, install the metadata migration, provision the dedicated
`weft_account_display` login, and deliver its connection string as a protected
private file through `WEFT_ACCOUNT_DISPLAY_CONNECTION_STRING_FILE`. The same
private-file, explicit password and no-ambient-credential checks as other Session
connections apply. A plain `WEFT_ACCOUNT_DISPLAY_CONNECTION_STRING` is refused;
a configured but missing/unreadable file is not treated as absence. The role is
admitted on every metadata operation; configured storage failure returns the
closed unavailable authentication error rather than pretending persistence or
revocation succeeded.

For local databases, explicitly run
`deploy/local/postgres-add-account-display-role.sh` as the database administrator
with `POSTGRES_USER`, `POSTGRES_DB` and a separately delivered
`WEFT_LOCAL_ACCOUNT_DISPLAY_PASSWORD`. The installer refuses an existing role
instead of rotating credentials. It supports installation before or after the
metadata migration. For another deployment platform, use its established secret
delivery and database administrator process to apply the same role and column
grants; running the web host never provisions a role.

The dedicated login needs CONNECT, schema USAGE, SELECT/INSERT on the four
metadata columns, and UPDATE only on `display_name` and `observed_at`. It has no
principal, binding, session, payload, DDL, delete, truncate or key-update rights,
role membership or grant options. Keep it separate from all other feature roles.
Disabling delivery hides names without deleting metadata or revoking sessions.
