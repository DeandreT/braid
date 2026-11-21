# Isolated native CSP qualification fixture (#5)

No browser suite imports this fixture. There is no browser-clean verdict. `worker-src` in the local control is a **document** blocking worker creation, not evidence from a running worker. The nine journal tests use local inputs, not native observations.

## Build the pinned pair

Use an empty task-owned build directory. Obtain Chromium release source `157.0.8090.0`, revision `2ea1b71af70d7fc5d41325dda8df37c1f98ac531` (official `.tar.xz` SHA-256 `59d273ca268798f0e3fd5a703cd7233d44a912a6e9adb0597d9b8e61d991eb0a`). Verify the digest **before** extracting, verify `build/util/LASTCHANGE` against the revision, and use the GN, sysroot, Clang, Rust and test fonts pinned in its `DEPS`. If the release compiler lacks `cr_build_revision`, restore the verified pinned Clang package using Chromium's `tools/clang/scripts/update.py`; don't bypass GN's version check. The test-fonts GCS package belongs under `third_party/test_fonts/test_fonts`.

From Chromium's source directory (`FIXTURE` is the absolute path to this directory):

```sh
git apply --check "$FIXTURE/native-phase.patch" && git apply "$FIXTURE/native-phase.patch"
buildtools/linux64/gn gen out/CspProbe --threads=2 --args='is_debug=false is_component_build=false is_official_build=false symbol_level=0 blink_symbol_level=0 v8_symbol_level=0 use_remoteexec=false chrome_pgo_phase=0'
third_party/ninja/ninja -C out/CspProbe -j2 blink_unittests chrome
unshare --user --map-current-user --net timeout 30 out/CspProbe/blink_unittests --gtest_filter=ContentSecurityPolicyTest.FixtureLocalCaptureIsNotBrowserCutoff --test-launcher-jobs=1 --test-launcher-retry-limit=0 --test-launcher-print-test-stdio=always
```

Run builds under a storage monitor. On the Linux qualification host verify `/dev/sdb1` Btrfs on `/mnt/perok`; stop on ENOSPC, below 60 GiB free, or both below 128 MiB device-unallocated **and** below 600 MiB logical DUP metadata headroom. Retain build intermediates while developing the pair.

In a separate Playwright checkout at `606ecd8b34bac100db32561b6278ffd76e5eb3cd` (`v1.64.0`), apply `driver-launch.patch`, run `npm ci --ignore-scripts --no-audit --no-fund` then `GOMAXPROCS=2 node utils/build/build.js --disable-install` with a private npm cache and `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`. Do not substitute Playwright's default browser.

## Bounded controls

Use task-owned, trusted, immutable inputs. Set `BRAID_CHROME` to the built Chrome executable, `BRAID_DRIVER` to the source-built `packages/playwright-core` directory, and `BRAID_CHROME_SHA256` and `BRAID_BUNDLE_SHA256` to the admitted digests of Chrome and `lib/coreBundle.js` from the build receipt. These digest checks are **not** verification of a hostile writable installation. Set `HOME` and `TMPDIR` to private empty fixture directories. Run:

```sh
node --test "$FIXTURE/receipt-probe.test.mjs"
unshare --user --map-current-user --net timeout 30 node "$FIXTURE/local-control.mjs"
```

The browser sandbox remains enabled. No credentials, external service, capture, shared browser profile, or remote debugger endpoint is needed. The real policy comes from an intercepted fixture HTTP response. A page event synchronizes the stimulus only. The native unit test separately verifies one enrolled delegate's before-handler field match and settlement after return. Neither test is a cutoff.
