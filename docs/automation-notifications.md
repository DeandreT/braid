# Automatic regression and notifications

New committed revisions create a durable suite in the same database transaction.
Automatic checking is on by default for each mapping. With no saved cases the
revision is `noCases`; a mapping administrator can turn future checks off.

Each suite snapshots every current case version at revision commit. The original
case envelope, native revision, input, clock, expected outputs and old results are
never rewritten or cloned. An evaluation records the original case reference and
the **separate target revision and native digest**. Only that target's native bytes
are dispatched to the existing preview backend. The returned provenance must match
the target before comparison with the original expected output.

Document cases keep their separately pinned preview profile, output media and
EDI comparison options when evaluating a new revision. Selecting a different
revision changes neither the saved replay inputs nor the original baseline.

Document results are staged, not passed, by the ordinary run writer. A separate
`braid_comparison_settler` login reads the locked original case and staged bytes,
verifies their digests, and recomputes the saved policy with the same C# comparer
used for interactive runs. It accepts no caller-supplied pass flag. Its decision
is bound to that case version, result digest and unexpired execution lease, and
current authority is checked again before commit. Malformed documents and wrong
media fail; explicitly ignored EDI fields retain their saved comparison meaning.
Exact-byte JSON results use the database comparison path.

Before reading a staged result and again before settlement, the verifier admits
the inherited regression-run routines and schema, including the authorization
helper. These are catalog-only checks; the comparison role receives no run-role
identity, direct table access or additional execution grants.

The regression worker requires `BRAID_AUTOMATION_COMPARISON_CONNECTION_STRING`
(or its protected `_FILE` form) in addition to its run credentials. Deliver this
credential only to the trusted worker, never the API host or ordinary run-writer
clients. It has no table access and can execute only the two settlement entry
functions. There is no fallback to trusting the ordinary writer when it is absent.
Local/Azure provisioning includes this distinct CONNECT-only login. Deliver its
credential through the deployment's secret-delivery process.

Cases have independent fenced leases and at most three infrastructure attempts.
A comparison failure is terminal for that case, but other cases continue. Expired
workers cannot replace a result. Restarting a worker resumes unfinished cases and
does not resubmit successful cases. Changing the mapping's automatic setting does
not cancel or rewrite an already committed suite.

Completion rechecks the submitting actor's current execution authority. A revoked
actor cannot publish a previously computed pass: the evaluation becomes revoked,
keeps no result payload and settles the suite as failed. Manual rechecks share an
organization/actor-wide pending limit across projects.

## Build policy

Project administrators can require passing regression before build approval and
qualification. Database triggers enforce the policy, including callers outside
the HTTP host. The latest suite must pass on the exact revision and include every
current case version. Disabled checks, no cases, pending checks, failures, stale
case versions and passes on other revisions do not satisfy the policy. Existing
build approvals and qualification evidence remain immutable; the policy does not
retroactively erase them or activate a release.

The gate rechecks durable comparison evidence as well as suite state. Database
admission verifies each Automation trigger's exact `braid` function identity,
source, ownership and permissions. Partial installations are refused.

Use **Run all current cases now** on a revision to create another immutable suite
after changing a baseline. The
caller-chosen suite ID makes a retry idempotent. The new suite evaluates that same
exact revision and leaves earlier case versions, suites and results intact.

The project page links to **Automation and notifications**. Revision and mapping
lists show the recorded suite state; a missing suite means not recorded, not pass.
The mapping and revision views show the project gate and mapping switch.

## Notification centre and subscriptions

The header's **Notifications** view combines events from projects the caller can
currently see with organization invitations addressed to that principal. Every
read and read-marker operation rechecks current organization and project access.
Losing access hides retained notifications; it does not erase their history.
An existing inbox cursor keeps its position when its anchor project is revoked;
later pages still show only currently visible activity.

Each member chooses events and channels per project. There are no implicit
external subscriptions. Supported events are revision upload, regression failure
or recovery, build qualification or failure, release activation or rollback,
endpoint error-rate threshold crossing, and invitations.

The administration store's actual invitation insert and redemption transitions
produce recipient-only in-app notifications for the inviter and a known invitee.
An invitee is resolved through the enabled tenant/object principal binding, never
by matching an email address. First-time enrollment can attach its notification
only after the verified redemption creates the principal. Organization-only
invitations need no project subscription and never create unsolicited email or
webhook delivery. No invitation secret, secret hash or redemption token enters
the notification tables or envelope.

The notification outbox commits with the source event. Events contain only their
type, IDs, timestamp and a braid link, never payloads, parameters, mapping names,
email addresses, tokens, worker messages or private diagnostics. Event uniqueness
and delivery identities make source replay safe. Receivers should deduplicate on
the event ID: external delivery is at least once because a connection can fail
after the receiver accepts it.

Project administrators can inspect delivery status and fixed outcome categories,
not raw provider responses. Each delivery has at most five attempts, delayed by
30, 60, 120 and 240 seconds. Ten consecutive destination failures disable the
destination. Re-enabling requires an explicit administrator action; old terminal
deliveries are not silently resent. Revoked membership or subscriptions stop
queued external delivery. Accepted SMTP/HTTP submissions are shown as delivered;
this does not establish that a person read the message.

Endpoint monitoring counts completed admitted execution attempts, including released failures, in a project-configured
window (60–3600 seconds), with a minimum sample count and error percentage. It
emits once when a window crosses the threshold, not once per failed request.

## Private host configuration

The API and regression worker read `BRAID_NOTIFICATION_CONFIG_FILE`, an absolute,
owner-readable private JSON file (at most 64 KiB). Do not commit this file. On
Linux it must be an owned, regular, single-link 0600 file beneath protected
ancestors; the existing private-host-file reader verifies it before parsing. The worker uses its existing
regression credentials and starts automation with the regression worker; no new
database login or pipeline is required.

Notifications can also run independently of mapping execution. Enable
`BRAID_NOTIFICATIONS_ENABLED=true` in the API and worker, and set
`BRAID_NOTIFICATION_CONNECTION_STRING` to the existing restricted `braid_run_writer`
profile. In Session hosting use the protected `_FILE` form instead. The existing
regression writer setting is a supported fallback. A notification-only worker
sets `BRAID_PREVIEW_WORKER_ENABLED=false`; it needs no worker image, sample-reader
credentials or payload storage. When the regression worker is enabled, it
already handles notification delivery and no second pump is registered.

An operator registers destinations and verified recipient addresses. The public
API and UI select **IDs only**: they cannot add arbitrary URLs, email recipients
or secret keys. Configuration shape (all values below are placeholders):

```json
{
  "origin": "https://braid.example.com",
  "hosted": true,
  "webhooks": [{
    "organizationId": "11111111-1111-1111-1111-111111111111",
    "projectId": "22222222-2222-2222-2222-222222222222",
    "id": "operations",
    "url": "https://hooks.example.com/braid",
    "secret": "BASE64_OF_32_TO_64_RANDOM_BYTES"
  }],
  "recipients": [{
    "organizationId": "11111111-1111-1111-1111-111111111111",
    "principalId": "33333333-3333-3333-3333-333333333333",
    "address": "verified-recipient@example.com"
  }],
  "email": {
    "provider": "smtp",
    "sender": "braid@example.com",
    "smtpHost": "smtp.example.com",
    "smtpPort": 587,
    "username": "PRIVATE_USERNAME",
    "password": "PRIVATE_PASSWORD"
  }
}
```

SMTP uses STARTTLS with normal certificate verification and never uses default
process credentials. For the configured ACS adapter, use `provider: "acs"`,
`sender`, `acsEndpoint` (the service's HTTPS `*.communication.azure.com` origin)
and `acsAccessKey` (Base64 key) instead of SMTP settings.

Webhooks use HTTPS port 443 only, reject userinfo/query/fragment, disable redirects
and proxies, re-resolve DNS at each attempt, reject any non-public resolved address
and connect directly to a validated IP while retaining the hostname for TLS.
Private, link-local, multicast, mapped loopback, translation and documentation
ranges are not permitted in hosted mode. A configured non-hosted test profile may
use HTTPS loopback; normal TLS verification still applies. Neither delivery logs
nor exceptions expose URLs or credentials.

The UTF-8 JSON body is signed with HMAC-SHA256 over ASCII timestamp, a dot, and the
**exact body bytes**. Headers are `X-Braid-Timestamp` (Unix seconds),
`X-Braid-Signature: sha256=<lowercase hex>` and `X-Braid-Event-Id`. Receivers should
require a short timestamp tolerance (for example five minutes), compare signatures
in constant time, and reject repeated event IDs after processing.

## HTTP API

Generated operations are in `contracts/braid-v1.json`. All routes use the existing
session/bearer boundary, fresh project authority, CSRF for browser writes,
bounded input, and `Cache-Control: no-store`.
