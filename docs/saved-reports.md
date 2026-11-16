# Saved revision reports

The saved revision page has separate explanation and validation panels.

## Reading and generating

Opening a revision, returning to the tab or choosing Read again reads saved
reports. When a current (not pinned) report reads as never generated and the
viewer may generate reports, the page requests it once for that selection, as
if Generate had been chosen. A failed attempt is never repeated automatically.
Someone who can't generate reports, or a workspace with generation turned off,
sees a prominent notice that says so instead. Explanation and validation load
independently; a failed report request leaves the other report and revision
details available. A denied access check clears private report content.

Generate explanation and Generate validation request work when no result
exists. A failed attempt can offer Try derivation again. These actions
check current access first. Controls follow current service availability and
mapping actions; a displayed role name does not grant an action.

An accepted request can remain in progress. The page observes it with a short,
bounded sequence of reads and then offers Check again if needed. Check again
only reads. Navigating away cancels browser requests and observation; an already
accepted generation request may still complete. Returning to the revision reads
its actual state; only a report still never generated is requested again.

## Understanding results

Each panel distinguishes the latest attempt, a successful saved result, whether
its payload could be read, and the operation's outcome. A successful result can
remain saved while its payload is missing or fails an integrity check. The page
shows that identity and refusal without drawing an empty successful mapping.

The explanation uses the existing searchable Source → rule → target viewer,
including output selection, detailed rules and controls, exact values, lineage,
native evidence and explanation gaps. Partial or unavailable explanations remain
explicit even when a search finds no rows.

Validation reports their own validity decision and counts. A usable report can
say that the native mapping is invalid; an unreadable or absent report gives no
validity decision. Explanation coverage and validation validity are separate.
Shortened notice text or locations are identified separately from total and
shown notice counts.

Repair is available only for an identified current saved result whose object
is missing or fails integrity checks. It requests work for that exact result.
If another request changes the selection first, the page reports the conflict
and asks for a fresh read. It never silently repairs a replacement result or
repeats the request automatically.

## Saved links

Links to saved explanations and validation identify actual successful results
within the complete organization, project, mapping and revision URL. An attempt
that is still pending or failed does not become a saved-result link. Generations
and revision sequences remain exact strings.

A linked validation view explicitly names the successful explanation it relates
to. Finding links navigate only within that verified explanation. If the linked
read is unavailable, Read validation without links makes a new authorized read;
it does not reuse cached findings as proof of access.

Saved result links keep their exact revision and result identities after later
uploads or current-selection changes. Read current reports for this revision
removes report selectors while retaining the same revision. View current head
is a separate catalog action that resolves the mapping's latest revision once.

## Verification

The report suite uses `npm run test:reports-browser` at the packaged API's exact
configured origin with disposable PostgreSQL, private storage and the pinned
native worker. Its fixture instructions live in
[the report browser tools](../tools/reports-browser/README.md). Actual mappings
are uploaded through product HTTP before the browser reads or generates reports.
Controlled browser cases supplement the real persistence scenario and identify
their scope separately. Catalog and shell navigation remain separate regression
suites. All headed checks use an alternate virtual display.
