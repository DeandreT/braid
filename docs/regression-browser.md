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

The server checks the authoritative byte, document and parameter bounds. The
editor also checks its configured limits before sending. No fixture silently
captures production payloads or derives expected bytes from an unverified result.

## Running and reading

**Run saved version** submits one exact case version. The resulting run retains
that revision and baseline even when the mapping's head or active runtime changes.
Run history is scoped to the current submitter. Queued/running results may be
refreshed or canceled; finished results show their explicit comparison outcome,
bounded differences and exact downloadable output bytes.

Output text is escaped and shown only as a bounded UTF-8 preview. Download is a
separate action that preserves the binary bytes. Unknown or inconsistent response
shapes are rejected, never treated as passing tests.

## Private state and uncertain requests

Sample data lives only in the current panel's memory. It is not written to local
storage, session storage, browser URLs or logs. Session, identity and revision
changes retire outstanding callbacks and clear private presentation state.
An explicit denial of sample access also removes previously displayed samples,
results and download controls, even if the authenticated identity is unchanged.

After an uncertain publication or run response, the panel retains the original
request and offers explicit check/retry actions. Retrying reuses its original ID
and body; it does not silently make a new case or run. Leaving the page clears
in-memory recovery information, so check saved cases/history before creating
replacement work. Forgetting uncertain work requires explicit confirmation.

Publication waits for every selected file read to settle. Changing or leaving a
view retires unfinished file reads; their callbacks cannot update a new editor.

Controlled HTTP browser checks cover presentation behavior. They do not replace
actual API, database, worker and hosted-session qualification.
