# Standalone browser CSP custody controls

These C++20 controls model per-call bookkeeping for a browser-to-`LocalFrame`
CSP notification. They run without Chromium, Mojo, a browser, or the package.
They do not enable collection.

- A send is reserved against the intended frame, document, process incarnation,
  and producer lease. Independent IDs keep otherwise identical calls separate.
- Receiving, queueing, or seeing the native event phase is not completion.
  The modeled DOM result requires the exact original event and recipient at
  the phase and on dispatch return. Dropped work, missing results, wrong
  endpoints, replay, and replacing an original cannot settle its obligation.
- `FrameAncestors` uses a reporting/Inspector result, not a DOM result.
  That branch is distinct from the `report-only` policy disposition, which
  can still produce a DOM event on other paths.

Run from the repository root with a private `OUTPUT` directory:

```sh
clang++ -std=c++20 -Wall -Wextra -Werror -pedantic \
  tools/browser-fixture/native-csp/browser-csp-custody-test.cc \
  -o "$OUTPUT/browser-csp-custody-test"
"$OUTPUT/browser-csp-custody-test"
```

The model takes identity, dispatch, and loss observations as inputs. It
does not verify how Chromium obtains those observations, transport ordering,
or a durable receipt or cutoff. The
[stock browser control](browser-origin-csp.md) covers observable CSP
behavior separately. Neither is a clean verdict.
