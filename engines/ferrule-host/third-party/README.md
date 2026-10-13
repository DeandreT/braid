# Worker dependency metadata

The restricted worker uses Ferrule's `mapping`, `ir`, and `engine` libraries at
revision `0495822e102bba3f97d9bffd3c665adfb6138bec`. Their upstream package metadata
declares `GPL-3.0-only`. `ferrule-GPL-3.0-only.txt` retains the license text from
that exact revision. It is dependency metadata and does not declare a license
for the Weft repository.

The worker image also retains the runtime library copyright files supplied by
its Ubuntu build environment. Compiler, Rust package, and source identities are
recorded in the locked worker build and its qualification evidence.
