# Isolated native CSP qualification fixture (#5)

No browser suite imports this fixture. There is no browser-clean verdict. `worker-src` in the local control is a **document** blocking worker creation, not evidence from a running worker. The nine journal tests use local inputs, not native observations.

## Build the pinned pair

Use an empty task-owned build directory. Obtain Chromium release source `157.0.8090.0`, revision `2ea1b71af70d7fc5d41325dda8df37c1f98ac531` (official `.tar.xz` SHA-256 `59d273ca268798f0e3fd5a703cd7233d44a912a6e9adb0597d9b8e61d991eb0a`). Verify the digest **before** extracting, verify `build/util/LASTCHANGE` against the revision, and use the GN, sysroot, Clang, Rust and test fonts pinned in its `DEPS`. If the release compiler lacks `cr_build_revision`, restore the verified pinned Clang package using Chromium's `tools/clang/scripts/update.py`; don't bypass GN's version check. The test-fonts GCS package belongs under `third_party/test_fonts/test_fonts`.

After verifying and extracting Chromium and checking out Playwright at `606ecd8b34bac100db32561b6278ffd76e5eb3cd`, apply the complete stack **once** to pristine inputs. `$FIXTURE` is the absolute path to this directory. The helper applies `native-phase.patch`, `worker-lifecycle.patch`, `inspector-receipt.patch`, `worker-transport.patch`, `document-birth.patch`, `browser-fence.patch`, then `publication-veto.patch` to Chromium, and `driver-launch.patch` to Playwright.

```sh
python3 "$FIXTURE/apply-receipt-pair.py" "$CHROMIUM_SRC" "$PLAYWRIGHT_SRC"
cd "$CHROMIUM_SRC"
buildtools/linux64/gn gen out/CspProbe --threads=2 --args='is_debug=false is_component_build=false is_official_build=false symbol_level=0 blink_symbol_level=0 v8_symbol_level=0 use_remoteexec=false chrome_pgo_phase=0'
third_party/ninja/ninja -C out/CspProbe -j2 blink_unittests chrome
unshare --user --map-current-user --net timeout 30 out/CspProbe/blink_unittests --gtest_filter=ContentSecurityPolicyTest.FixtureLocalCaptureIsNotBrowserCutoff --test-launcher-jobs=1 --test-launcher-retry-limit=0 --test-launcher-print-test-stdio=always
```

Run builds under a storage monitor. On the Linux qualification host verify `/dev/sdb1` Btrfs on `/mnt/perok`; stop on ENOSPC, below 60 GiB free, or both below 128 MiB device-unallocated **and** below 600 MiB logical DUP metadata headroom. Retain build intermediates while developing the pair.

In a separate Playwright checkout at `606ecd8b34bac100db32561b6278ffd76e5eb3cd` (`v1.64.0`), after the helper has applied `driver-launch.patch`, run `npm ci --ignore-scripts --no-audit --no-fund` then `GOMAXPROCS=2 node utils/build/build.js --disable-install` with a private npm cache and `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`. Do not substitute Playwright's default browser.

## Bounded controls

Use task-owned, trusted, immutable inputs. Set `BRAID_CHROME` to the built Chrome executable, `BRAID_DRIVER` to the source-built `packages/playwright-core` directory, and `BRAID_CHROME_SHA256` and `BRAID_BUNDLE_SHA256` to the admitted digests of Chrome and `lib/coreBundle.js` from the build receipt. These digest checks are **not** verification of a hostile writable installation. Set `HOME` and `TMPDIR` to private empty fixture directories. Run:

```sh
node --test "$FIXTURE/receipt-probe.test.mjs"
unshare --user --map-current-user --net timeout 30 node "$FIXTURE/local-control.mjs"
```

The browser sandbox remains enabled. No credentials, external service, capture, shared browser profile, or remote debugger endpoint is needed. The real policy comes from an intercepted fixture HTTP response. A page event synchronizes the stimulus only. The native unit test separately verifies one enrolled delegate's before-handler field match and settlement after return. Neither test is a cutoff.

## Dispatch receipt API (local fixture only)

`browser-receipt.mjs` runs in the same isolated **loopback-down** namespace as `local-control.mjs`; use an outer `timeout 120`. It launches its own verified source-built driver and sandboxed executable. The private pipe session must implement `Audits.armBraidDispatch({epoch}) → {generation}` and `Audits.readBraidDispatch({epoch}) → {generation, dispatch}`. The latter seals this delegate, not the browser. A missing method, session loss, mismatched generation or missing observation is a refusal, never an instruction to reconnect using stock Playwright. Node journals the redacted commitment and signs its receipt after fsync; the driver verifies that receipt before releasing the original session. No replay against a replacement page is permitted.

This version enrolls the current document **after navigation**. It cannot attest the navigation, another page, a reload, a worker, earlier or unknown producers, or a whole run. The Node custody model refuses even when all locally issued tickets have settled. A packaged suite needs independent startup enrollment, a closed native producer inventory and a pre-close settlement protocol for each original page/context. It must use a separate namespace with only loopback up and task-owned loopback services; do not enable loopback in the offline control or reuse its profile, journal or cleanup owner.

## Classic dedicated worker receipt

With the same admitted pair, environment and loopback-down namespace, run `timeout 60 node "$FIXTURE/worker-receipt.mjs"`. The separate worker fixture allows its classic script; **the worker response** sets `connect-src 'none'`. Before permitting the blocked fetch, Node persists and signs the original reservation and native WorkerToken. Completion must extend that exact journal prefix with the same token and a native dispatch identity, after dispatch returns and the original worker retires gracefully. The driver verifies the signed acknowledgment before closing the original document. A module worker is a negative control. The worker is scoped to a fresh isolated renderer; this receipt does not inventory other renderers or establish a whole-run cutoff.

## Reserving the next fixture document

`Audits.reserveBraidDocument({epoch, frameId})` on the original private page session returns a one-use reservation for the next mapped HTML document in the specified root or immediate local child frame. Reserve before navigating. The renderer installs its delegate before `DocumentInit::CreateDocument`, and binds the created document before initialization. Only pending reservation state transfers across an agent replacement; an old receipt must never transfer. Read the actual document token and completed dispatch using `Audits.readBraidDispatch` on the same logical session. Test with `timeout 40 node "$FIXTURE/navigation-control.mjs"` in the offline fixture namespace. The controls target the fixed fixture origin and don't attest the seed page, arbitrary origins, blocked or abandoned navigations, browser-wide ingress, or final disposal. Don't use this method as the packaged adapter's `drain` or `cutoff` implementation.

The [browser-session fence transport](browser-fence.md) binds the original document inventory and signed checkpoint across context disposal. It is refusal-only.

For the stricter opt-in load receipt and why it cannot close a general scriptable document, see [promise and host publication](reaction-boundary.md).
