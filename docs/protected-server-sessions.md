# Protected server-session adapter

Implementation: the dormant `Weft.Identity` module supplies protected minimal
tickets, durable key admission and the session-store adapter. Implementation
review and qualification evidence are tracked separately in
[#107](https://github.com/DeandreT/weft/issues/107). This page describes behavior
and limits without reporting a test result or active sign-in feature.
The preceding PostgreSQL foundation is qualified and merged in
[PR #109](https://github.com/DeandreT/weft/pull/109).

## Dormant composition

`Weft.Identity` implements the twelve `ITicketStore` overloads over the
[session contracts](server-sessions.md) and the separate
[PostgreSQL store](postgres-server-sessions.md). It references Application/Domain
and the ASP.NET shared framework, with no PostgreSQL or mapping-engine dependency.
The Server/API does not reference or register it. No cookie, login/logout/status
route, Entra callback, CSRF handler or Angular sign-in is activated.

A trusted server caller configures one organization, exact application name and
existing private key directory. `DurableSessionKeyProfile.Open` admits an
existing ring; `BootstrapEmpty` is an explicit owner operation for a designated
empty directory. Neither is an HTTP or shipped administrator command. The caller
owns the certificates, profile and session ports/pools; the store borrows them.
There is no arbitrary protector injection or ephemeral recovery fallback.

The initial profile requires Linux ownership/nonsymlink/private-directory checks
and a private key-file closure. It uses standard Data Protection filesystem and
certificate APIs. Before its first Protect, every retained nonrevoked key,
including expired historical keys, must admit through `IKey.CreateEncryptor`.
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
tickets. Fresh-provider/process qualification must establish these promises;
a warm cache is not durable recovery or an instantaneous distributed revocation
fence. Nominal path revalidation makes no descriptor-relative filesystem or
concurrent-mutation guarantee. Decryptability does not establish certificate-at-rest
provenance for arbitrary historical key files.

## Minimal identity and precision

The framework key is `ws1.` plus canonical unpadded base64url of 32 random bytes:
47 ASCII characters. Storage receives SHA256 of those original bytes. A separate
login epoch and original binding/version/owner snapshot are protected in a fixed
120-byte versioned envelope scoped to the configured application and organization.
No provider subject, email, token, role, project permission or arbitrary ticket
property enters that envelope. Malformed framework keys return no identity;
malformed protected envelopes and integrity failures remain categorical errors.

The canonical identity has only internal principal, organization and epoch
claims. Issue and current ticket schemes are exactly `Weft.Session.Issue` and
`Weft.Session`; the identity type is `Weft.Session`. Tickets are nonpersistent and
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

## Qualification and follow-on boundaries

The test sources cover format/real Data Protection, synthetic-port adapter
semantics, actual PostgreSQL/DP flows and a separate one-shot process probe.
The probe is test-only: bounded private stdin and an exclusive private result,
no secret arguments/environment, HTTP listener or native mapping worker. Its
ordinary output is copied to the test closure; it is not registered as a service.
Their presence is not a qualification claim; the source/run evidence belongs to
the linked #107 implementation review.

[#112](https://github.com/DeandreT/weft/issues/112) retains actual cookie-handler
properties/carrier behavior, HTTPS delivery, old-cookie rotation, request renewal
policy, pooled-host behavior and epoch-bound CSRF. P4c4 then owns the dormant
catalog identity seam. Entra/provider ingress and hosted activation follow in
P4d; Angular sign-in follows in P4e. Session metadata grants no organization
admission, project role or
[payload permission](project-payload-permissions.md). Sensitive operations still
perform their own fresh database authority and explicit-grant checks.
