# Mapping metadata editor

The metadata editor changes a saved mapping's name and description.

## Entering and saving

The mapping page offers **Edit mapping metadata** when fresh catalog context
enables metadata editing and the mapping's current actions allow it. The dedicated
`/catalog/organizations/:organizationId/projects/:projectId/mappings/:mappingId/edit`
route also checks those conditions before exposing the form. Unsupported query
parameters and malformed scope IDs do not authorize reads or saves.

The editor reads the complete metadata DTO and actual strong ETag from the
metadata endpoint. Ordinary catalog versions and revision heads are not
validators. Each deliberate Save refreshes context and mapping actions, then
sends one frozen name/description body with the received ETag in `If-Match` and
the fresh request token. It does not retry automatically.

Names use the server's .NET whitespace trimming, Unicode scalar and UTF-8 limits;
the submitted body preserves the raw name and exact description. Invalid Unicode,
NUL and oversized serialized bodies are refused before sending. A verified 200
must identify the same scope and expected accepted values, actual ETag and exact
metadata version. A no-op retains the version; changed values advance it once.

## Concurrent changes and uncertain replies

A 412 keeps the original baseline and the user's draft. **Review latest** reads
current metadata; the comparison presents original, current and draft separately.
**Keep mine** or **Use current** is an explicit choice. Saving after review is a
new deliberate conditional write, and another concurrent edit can still cause a
second conflict. Review never silently overwrites a draft or submits a PATCH.

An interrupted or unverified sent save may already have committed. The editor
retains one frozen attempt in memory. **Check current metadata** performs a GET;
matching current values do not acknowledge the lost save. Reviewing latest and
deliberately saving again creates a new attempt. A verified reply confirms that
latest attempt while retaining an informational warning about any earlier unknown
result. An explicit choice to use current values can also end editing without
claiming that an earlier save succeeded.

Drafts, bodies, ETags and request tokens are not written to browser storage, URLs
or diagnostics. Navigation asks whether to keep editing or discard; full-page
leave uses the native unsaved-change warning. Reloading or closing the tab loses
memory-only recovery information. Focus refreshes current authority without
replacing a dirty form; controls remain read-only while that refresh is pending.
Confirmed identity or organization changes clear private editor data. Current
access refusal hides it, and unavailable context is not treated as permission.

## Preservation and browser checks

Metadata editing changes no native mapping, immutable revision, current head,
saved explanation or validation result. The server's restricted metadata editor
configuration is independent of native ingestion, report generation and storage
writer configuration.

The [browser fixture](../tools/mapping-metadata-browser/README.md) uses actual
HTTP uploads and saved reports for setup, then starts a host with only catalog
reader and metadata editor credentials. Real browser PATCHes are checked against
a complete private persistence baseline; no-op saves must change nothing.
An explicit native append separately checks that metadata validators remain
unchanged.

Run `npm run test:mapping-metadata-browser` with the fixture's explicit private
bindings and pinned browser. The script uses an alternate virtual display.

The actual metadata scenario mediates each already-allowlisted browser GET through
one real upstream request and delivers the same bounded JSON bytes, status and
headers to that original browser request. Observation binds the exact Request and
browser response to successful delivery; metadata ETags and fresh authority/token
checks remain mandatory. No extra read, retry or alternate body is accepted.
