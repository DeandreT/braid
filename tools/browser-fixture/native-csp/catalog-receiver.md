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

### Reproduce the controlled packaged run on Linux

Use the retained transfer from packaged commit `06766b14`: the wrapper verifies the complete `publications/server` tree (including `wwwroot`), and `runtime/dotnet` against pinned file inventories. `workspace/node_modules` must be the matching lock-verified install. Keep the fixture scratch parent private and its absolute name short enough for Chromium's Unix sockets. With the already admitted paired binary and bundle digests exported (see README):

```bash
export BRAID_PACKET=/home/deandre/Downloads/braid-4-catalog-evidence/transfer
D=/mnt/perok/build/braid/issue5-native.3dtXZPXI
export BRAID_CHROME="$D/tree/chromium-157.0.8090.0/out/CspProbe/chrome"
export BRAID_DRIVER="$D/artifacts/driver-src/packages/playwright-core"
export BRAID_SCRATCH="$D/ltmp"
for mode in success missing wrong late lost-reply hold-challenge replacement target-close refused-enrollment fault-free; do
  bash tools/browser-fixture/native-csp/run-packaged-receiver.sh "$mode" || exit 1
done
```

Each invocation starts a private packaged server at `127.0.0.1:45921` inside a fresh loopback-up namespace, drops capabilities, and has a 60-second outer deadline. `hold-challenge` issues `P`, waits for the fence, then delivers the challenge. Browser rejection leaves `O`; the renderer only records `receiver-ack` on a positive response to Confirm. The reserved zero renderer epoch is a refusal-only control: an incumbent lifecycle owner makes the actual second Begin fail at birth. Use the separate loopback-down wrapper in `old-page-commit.md` for commit/unload; do not run those controls with this server.
