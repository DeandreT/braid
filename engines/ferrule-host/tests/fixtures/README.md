These native Ferrule JSON documents were written specifically for Weft bridge qualification. They are not copied from the Ferrule sample suite or from private mappings.

- `valid-basic.json`: direct bindings, a literal dotted source name, a nested source path, and a child target scope.
- `invalid-graph.json`: an unknown builtin, a missing dependency, and a cycle for the direct engine validation oracle.
- `invalid-function.json`: an invalid function and body with the full `u64` function identity, preserved as exact strings in diagnostics.
- `versioned-exact.json`: Ferrule's v2 native envelope with an integer above JavaScript's exact range, an integral Float, and negative zero with authoritative float metadata.
- `resource-metadata.json`: local and remote sentinel instance/configuration references that inspection must preserve as unresolved design metadata without access.

The worker and .NET process qualification suites consume the same retained bytes. Expected inspection counts and native paths are independently specified in the tests; validation expectations come directly from the pinned Ferrule engine.
