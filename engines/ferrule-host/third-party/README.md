# Worker dependency metadata

The restricted worker uses Ferrule's `mapping`, `ir`, `engine`, `cli`, `mfd`,
`codegen`, and `codegen-csharp` libraries at revision
`576012aa17c403d9c3fcb8659ed4d3a47ba06864`, with every transitive Ferrule
package locked to the same revision. The `cli` library supplies only its public
in-memory payload API to the preview operation; the worker does not run the
Ferrule command-line program. `codegen` and `codegen-csharp` supply only
in-memory C# source generation to the build operation. `mfd` supplies the closed
in-memory package importer and exporter. Their upstream package
metadata declares `GPL-3.0-only`. `ferrule-GPL-3.0-only.txt` retains the license
text from that exact revision. It is dependency metadata and does not declare a
license for the Weft repository.

The worker image also retains the runtime library copyright files supplied by
its Ubuntu build environment. Compiler, Rust package, and source identities are
recorded in the locked worker build and its qualification evidence.
