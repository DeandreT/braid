# Native Ferrule test fixtures

These native Ferrule JSON documents were written for Braid bridge tests. They are not copied from the Ferrule sample suite or from private mappings.

- `valid-basic.json`: direct bindings, a literal dotted source name, a nested source path, and a child target scope.
- `invalid-graph.json`: an unknown builtin, a missing dependency, and a cycle for the direct engine validation oracle.
- `invalid-function.json`: an invalid function and body with the full `u64` function identity, preserved as exact strings in diagnostics.
- `versioned-exact.json`: Ferrule's v2 native envelope with an integer above JavaScript's exact range, an integral Float, and negative zero with authoritative float metadata.
- `resource-metadata.json`: local and remote sentinel instance/configuration references that inspection must preserve as unresolved design metadata without access.
- `preview-orders.json`: a JSON preview mapping with a repeated child scope, a static named input, typed runtime parameters, the host clock and a static named output.
- `preview-csv-source.json`: the basic mapping with a stored `.csv` primary input path, which the JSON preview profile must refuse before parsing.
- `projection-owned-context.json`: explicitly owned forms: an owned inner join read through owned member reads and source positions, with one closed ordinary read; an optional-presence scope; an owned failure iteration over a named input; an owned dynamic-input driver; and object-item nullability and an undeclared JSON property in the source schema.

The worker and .NET process tests consume the same retained bytes. Expected inspection counts and native paths are independently specified in the tests; validation expectations come directly from the pinned Ferrule engine.
