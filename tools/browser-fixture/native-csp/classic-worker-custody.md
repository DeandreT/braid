# Classic dedicated worker custody model

This standalone test models custody of a classic dedicated worker's original
lease. It takes synthetic inputs and is not linked into Chromium or the Braid
collector. A successful model assertion proves neither worker collection nor
a browser-wide verdict.

The positive control binds the creator at factory bind, reserves a one-use
worker lease before the worker starts, and distinguishes the requested type
from the type observed at scope bind and evaluation. It requires the original
worker's ingress seal, graceful retirement and ordered endpoint drain. A nested
worker gets its own lease.

The negative controls check missing or module script type, an unbound or
replaced original, renderer incarnation reuse after loss, abandoned or forced
retirement, missing or replayed drain, and unsupported shared worker, service
worker and worklet observations. A later clean worker does not clear an earlier
refusal.

Run from this directory with `OUTPUT` set to a task-owned directory:

```sh
clang++ -std=c++20 -Wall -Wextra -Werror -fsanitize=address,undefined \
  classic-worker-custody-test.cc -o "$OUTPUT/classic-worker-custody-test"
timeout 10 "$OUTPUT/classic-worker-custody-test"
```

No Chromium checkout, browser, or network is needed.
