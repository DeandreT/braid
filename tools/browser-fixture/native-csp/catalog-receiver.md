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
client's `DidFinishLoading` returns, `P` issues a fresh challenge. Only the
original renderer's reply handler can echo it over that loader's pipe. `A`
clears the receiver obligation after that echo matches run, document,
request and nonce. `O` identifies a still-open receiver even after disposal. `M`, `X`, and
`L` are missing, mismatched, and late returns. None clears `R`. Network
cancellation, detached clients, redirects, other resource classes, and
unmatched identities do not take the positive path.

`C` remains outstanding: neither client return nor the visible Angular result
settles the response consumer, embedder, or all script descendants. The
listener ledger links timers, animation callbacks, promise jobs, and the
selected loader where an original parent is available. It does not attest
that arbitrary listener bodies published only covered work. A failure remains
sticky after natural removal or disposal.

Build `BRAID_UI_FIXTURE` from the checked-out `apps/web/tests/catalog.fixture.ts` using the root lockfile's esbuild and the corresponding dependency tree (also used as `NODE_PATH` for `@playwright/test`):

```sh
node tools/browser-fixture/native-csp/build-catalog-ui-fixture.mjs "$LOCKED_WORKSPACE" "$PRIVATE_OUTPUT/ui-fixture.cjs"
```

Run `catalog-receiver-control.mjs MODE` once per fresh packaged fixture for `success`, `missing`, `wrong`, `late`, `lost-reply`, `replacement`, `target-close` and `refused-enrollment`. The lost-reply case closes the native service without delivering its reply. `catalog-fault-free-control.mjs` follows the actual Angular Help link and checks destination work separately. Use `catalog-visibility-control.mjs` only for the unchanged ordinary state, and the paired controls in `old-page-commit.md` for exact-source commit/unload verification. None is a CSP cutoff or a production journal provider.
