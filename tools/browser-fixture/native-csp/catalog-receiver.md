# Original fetch receiver (experimental fixture)

Apply the normal pinned pair first, then `catalog-retirement-refusal.patch`,
`lifecycle-ingress.patch`, and `catalog-receiver-lifecycle.patch` in that
order to Chromium. These patches do not enable packaged collection.

Only the controlled project-list GET has an accepting **receiver** path.
`FetchManager::Loader` opts its concrete `ThreadableLoader` in before start.
`ResourceFetcher` assigns the request identity before publication. A private
per-loader Mojo pipe opens with that identity, a fresh loader nonce, and the
original document token. The browser service is bound to that document and
run. No page or CDP method can submit a positive receipt.

At browser admission, `S` identifies the send, `C` reserves downstream work,
and `R` reserves the receiver. `F` records forwarding only. `E` and `B`
identify response and body handoffs on the original pipe. After the concrete
client's `DidFinishLoading` returns, `A` acknowledges that receiver if the
admission, forwarding, original, nonce, and phase still match. `M`, `X`, and
`L` are missing, mismatched, and late returns. None clears `R`. Network
cancellation, detached clients, redirects, other resource classes, and
unmatched identities do not take the positive path.

`C` remains outstanding: neither client return nor the visible Angular result
settles the response consumer, embedder, or all script descendants. The
listener ledger links timers, animation callbacks, promise jobs, and the
selected loader where an original parent is available. It does not attest
that arbitrary listener bodies published only covered work. A failure remains
sticky after natural removal or disposal.

Run `catalog-receiver-control.mjs` with the controlled packaged fixture
environment used by `catalog-work-control.mjs`. The test holds the actual
project-list GET, exercises real and rejected returns, then clicks Angular's
Help link. The separate `catalog-visibility-control.mjs` uses a private X
display and the same pinned launch defaults. Its `ordinary` case asks the
browser to send its *unchanged* visible state. For replacement cases correlate
`BRAID_FIXTURE_VIS` with the original token: source 1 is ordinary update, 2
is old-page state during commit, 3 is direct unload. A replacement URL alone
does not establish which path ran. These diagnostic records aren't signed
dispatch receipts or retirement tickets.
