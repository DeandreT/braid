# Protected server-session adapter

`Braid.Identity` supplies protected minimal tickets, durable key admission and the
session-store adapter.

## Composition

`Braid.Identity` implements the twelve `ITicketStore` overloads over the
[session contracts](server-sessions.md) and the separate
[PostgreSQL store](postgres-server-sessions.md). It references Application/Domain
and the ASP.NET shared framework, with no PostgreSQL or mapping-engine dependency.
The [catalog session boundary](catalog-session-boundary.md) uses it with the
[browser-session components](browser-server-sessions.md) for cookie handlers and
epoch-bound CSRF. See [Session host](session-host.md) for host setup.

A trusted server caller configures one organization, exact application name and
existing private key directory. `DurableSessionKeyProfile.Open` admits an
existing ring; `BootstrapEmpty` is an explicit owner operation for a designated
empty directory. Neither is an HTTP or shipped administrator command. The caller
owns the certificates, profile and session ports/pools; the store borrows them.
There is no arbitrary protector injection or ephemeral recovery fallback.

The initial profile requires Linux ownership/nonsymlink/private-directory checks
and a private key-file closure. It uses standard Data Protection filesystem and
certificate APIs. Before its first Protect, every retained nonrevoked key,
including expired keys, must admit through `IKey.CreateEncryptor`.
Wrong, missing or corrupt required material must fail without generating a
replacement. The pinned ASP.NET Core 10.0.12 key manager can log an error and omit
malformed key metadata. A private Error/Critical-only startup latch therefore
checks immediately after `GetAllKeys` and after all nonrevoked `CreateEncryptor`
calls, before any Protect, and again after the startup probe/path admission.
A latched nonfatal startup error refuses fixed `ConfigurationFailure`. The sink
emits no output and never formats or retains log payloads. Recording stops on
admission, failure and disposal; later untrusted-carrier errors cannot poison the
shared profile. This guard depends on the pinned framework's synchronous error
logging and must be reassessed on framework upgrades. It retains standard Data
Protection and uses no custom key XML parser.

Certificate overlap and old key files remain necessary for dependent live
tickets. Path revalidation makes no descriptor-relative filesystem or
concurrent-mutation guarantee. Decryptability does not establish certificate-at-rest
provenance for arbitrary retained key files.

## Minimal identity and precision

The framework key is `bs1.` plus canonical unpadded base64url of 32 random bytes:
47 ASCII characters. Storage receives SHA256 of those original bytes. A separate
login epoch and original binding/version/owner snapshot are protected in a fixed
120-byte versioned envelope scoped to the configured application and organization.
No provider subject, email, token, role, project permission or arbitrary ticket
property enters that envelope. Malformed framework keys return no identity;
malformed protected envelopes and integrity failures remain categorical errors.

The canonical identity has only internal principal, organization and epoch
claims. Issue and current ticket schemes are exactly `Braid.Session.Issue` and
`Braid.Session`; the identity type is `Braid.Session`. Tickets are nonpersistent and
not refreshable by framework policy. The database retains strict microsecond
20-minute idle/60-minute absolute bounds clipped to the verified login deadline.
ASP.NET properties use whole UTC seconds: issue/absolute dates are floored when
projected and compared. A tiny lifetime may project to equal dates. No rounding
up, hidden tick property or deadline extension restores precision. Envelope and
PostgreSQL facts retain their original microseconds.

## Once-only issuance and conditional lifecycle

`BeginTrustedIssuance` accepts trusted primitive binding/version/owner/deadline
facts. It does not verify a provider or accept authority from `HttpContext.User`,
client fields or copied claims. Private request features bind the exact context
and store, reserve and consume once before awaiting, and retain refusal/consumption
after disposal or failure. A fresh request feature state permits context reuse;
a stale scope and forged Items do not. A cancelled missing-scope refusal retains
its request tombstone while preserving the original cancellation cause.

Store validates the minimal issue ticket, protects before SQL and performs one
create-only write. Only acknowledged creation with exact immutable facts and
bytes returns the original handle. A collision is a closed refusal, with no
reallocation, adoption or automatic retry. Caller ticket changes during the
write cannot become issued identity. Unknown commit or cancellation remains a
failure; the adapter cannot claim atomic database-plus-cookie delivery.

Retrieve makes a fresh scoped observation each time. Renew performs one CAS;
a conflict permits at most one distinct observation of the same later immutable
identity, recorded internally as observation rather than this write's
acknowledgement. Neither path replaces the epoch/blob or revives expiry/revocation.
Remove directly requests retained revocation, including expired/inactive rows,
without a live-reader prerequisite. Exhaustion or failed revocation is not
successful logout.

Four context-aware overloads propagate caller/request cancellation; Store also
includes its issuance token.
Eight contextless forms refuse before persistence/protection; supplied cancelled
tokens retain precedence. Uncategorized persistence exceptions become fixed
`Unavailable`, because the current store does not expose a typed integrity error.
Malformed returned positive facts and envelope mismatches are `IntegrityFailure`.
Private exception text is never classified or exported; default diagnostics and
assertion failures must not print handles, bytes, identity keys or credentials.

## Tests

The test sources cover format/real Data Protection, synthetic-port adapter
semantics, actual PostgreSQL/DP flows and a separate one-shot process probe.
The probe is test-only: bounded private stdin and an exclusive private result,
no secret arguments/environment, HTTP listener or native mapping worker. Its
ordinary output is copied to the test closure; it is not registered as a service.
