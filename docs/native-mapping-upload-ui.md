# Upload a new native mapping

This increment adds an Angular upload page at a project's `mappings/new` route.
The project shows **Upload mapping** only when the current catalog feature and
that project's `createMappings` action are both enabled. The page refreshes
identity and project access before each deliberate upload or recovery action;
the server remains authoritative.

## Original file and metadata

The initial backend is visibly fixed to **Ferrule native JSON**. One file creates
one mapping and its first immutable revision. The page accepts 1–4,194,304 bytes;
the configured server or proxy can refuse a smaller request. It reads an owned
binary copy and sends those exact bytes, without parsing or rewriting native
JSON. Native validation can save either a valid or a decoded-invalid revision;
a decode or unsupported-format rejection is shown separately.

The user reviews the mapping name, description and saved filename before
submission. Name validation follows the existing .NET trim and Unicode rules.
Description remains literal textarea text. The saved filename is editable display
metadata, with a 255-byte safe-basename limit; it never selects a storage path.

The first deliberate request freezes the original file, encoded metadata and one
idempotency key. A deliberate original-file resend uses that same frozen request.
It cannot silently substitute a newly selected file or changed metadata.

## Acknowledgement and recovery

A confirmed committed response identifies the exact saved revision. The page
checks current ordinary read access separately before offering its revision link;
failure of that read does not undo the saved acknowledgement. Retrying current
access sends only reads.

A lost response, malformed result, timeout or **Stop waiting** can leave an
unknown outcome. The page keeps that uncertainty visible. A later refusal or a
read does not prove whether the original upload committed. Recovery is explicit:
resend the original request, or read/resume an exact operation learned from an
admitted original response. There is no lookup by key, catalog search for a
possible match, automatic upload retry or background polling.

Known pending, source-required, conflicting retry and lost-claim results remain
distinct. A source-free resume sends neither native bytes nor the original key.
Every deliberate action rechecks the original identity and current project
authority. A read can observe an operation; it cannot claim or execute work.

Reads wait at most eight seconds and upload/recovery writes at most 30 seconds.
Stopping the browser wait does not cancel an already dispatched server request.

## Tab lifetime and navigation

The client retains one upload intent in private tab memory. It does not save the
file, key or recovery state in browser storage or navigation URLs. Access and
identity changes hide private recovery details and invalidate stale callbacks.
Navigation uses an explicit leave decision while a draft or recovery state is at
risk. Reloading or closing the tab loses that memory; the before-unload warning
does not provide durable recovery. Explicit abandonment also does not cancel or
prove the outcome of server work.

Appending to an existing mapping is a separate increment. This page does not edit
native mapping rules or execute, preview or deploy a mapping.

## Verification boundary

The focused browser suite combines controlled frontend protocol/admission cases
with one real packaged-host scenario. The latter uses actual native workers and
PostgreSQL to cover saved native bytes, replay after HTTP response-delivery loss,
restart, permission changes, decoded-invalid admission and decode/format refusal.
Controlled responses establish frontend behavior only. The final implementation
and qualification report must record exact committed source and actual results;
implementation and qualification are tracked separately in [#84](https://github.com/DeandreT/weft/issues/84).

The packaged scenario observes its allowlisted catalog reads and upload responses
through a test proxy. Each read makes one real API request, retains its complete
JSON entity, and delivers those exact bytes to the originating browser request.
Checks bind the actual response status and request identity to the existing fresh
identity, project, token and immutable revision assertions. Saved revision reads
also require a fresh pending-to-ready page transition. A missing current value or
a mismatching capability or token still fails; an earlier response cannot replace
it. This qualifies the mediated API and frontend flow. Native browser cookie
ordering, browser-to-server cancellation propagation and network streaming timing
have separate verification boundaries. Other browser suites are unchanged.
