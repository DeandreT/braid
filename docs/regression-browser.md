# Regression browser

The revision page loads its regression panel separately from preview execution.
Use **Load regression cases** to read the server's current capabilities and saved
case metadata. The panel does not infer enablement or sample access from a local
role label.

## Fixtures and versions

Publishers can create a fixture or append a new immutable version. The editor
uses the revision currently displayed on the page and the server-selected runtime.
An appended version does not alter an older case or its run history.

Inputs and expected outputs accept UTF-8 text or exact-byte files. File content is
not parsed or reformatted. Additional input/output names are explicit, the clock
is fixed, and typed parameter values remain strings so large numbers and lexical
spelling survive. The first input is primary; a blank expected-output name marks
the primary output. An empty expected-output set cannot pass comparison.

When capabilities advertise a document runtime, the editor offers an explicit
**Execution profile** choice and an expected media selector for every output.
Input families still come from native mapping boundaries, not file extensions.
The document profile supports exact bytes/media or ordered EDI segments; the
latter exposes explicit separator, control-reference and date/time choices.
Editing an existing document case restores its media and policy choices, and
same-owner draft recovery preserves them without silently switching profiles.

The server checks the authoritative byte, document and parameter bounds. The
editor also checks its configured limits before sending. No fixture silently
captures production payloads or derives expected bytes from an unverified result.

## Running and reading

**Run saved version** submits one exact case version. The resulting run retains
that revision and baseline even when the mapping's head or active runtime changes.
Run history is scoped to the current submitter. Queued/running results may be
refreshed or canceled; finished results show their explicit comparison outcome,
bounded differences and exact downloadable output bytes.

EDI differences include bounded interchange/segment/element/repetition/component
locations. Invalid-envelope diagnostics identify the expected or actual side;
they are not passing comparisons. Downloads use the retained media type and a
matching JSON, X12 or EDI extension while preserving exact bytes.

Output text is escaped and shown only as a bounded UTF-8 preview. Download is a
separate action that preserves the binary bytes. Unknown or inconsistent response
shapes are rejected, never treated as passing tests.

## Private state and uncertain requests

Sample data lives only in the revision page's memory. It is not written to local
storage, session storage, browser URLs or logs. Session boundaries, catalog
refreshes and revision changes retire outstanding callbacks and clear displayed
cases, history, results and capabilities.

The open editor and any uncertain publication or run request belong to the
mode, principal, organization and Session identity context that loaded the
panel, and to the displayed revision. A failed, timed-out or negative session
check, or a refresh that replaces the panel, only suspends that work in a
page-scoped provider. The panel then states that work is kept, and the next
**Load regression cases** by the same owner on the same revision restores it
without sending anything. A fresh context for another owner, another revision,
leaving the page or an explicit replacement or abandonment discards it. A
restored editor requires current publication permission. An explicit denial of
sample access also removes previously displayed samples, results, drafts and
download controls, even if the authenticated identity is unchanged.

After an uncertain publication or run response, the panel retains the original
request and offers explicit check/retry actions. Retrying reuses its original ID
and body; it does not silently make a new case or run. Leaving the page clears
in-memory recovery information, so check saved cases/history before creating
replacement work. Forgetting uncertain work requires explicit confirmation.

Publication waits for every selected file read to settle. Changing or leaving a
view retires unfinished file reads; their callbacks cannot update a new editor.

Controlled HTTP browser checks cover presentation behavior. They do not replace
actual API, database, worker and hosted-session qualification.

The actual fixture is in `tools/regression-browser`. Its native Windows launcher
exports a committed source snapshot, creates verified Linux publications on
Btrfs and runs the ordinary API/worker against an owned PostgreSQL instance.
`playwright.regression-live.config.ts` drives that fixture without mocked replies;
see the fixture README for setup, acceptance and cleanup.
