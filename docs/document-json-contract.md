# Document JSON contract

`document_json_unique(json)` checks duplicate keys before conversion to `jsonb`
can erase them. Each object compares its key count with its distinct-key count,
then checks every value recursively. Arrays check every element. Repeated keys
in separate objects are valid; duplicate keys within any nested object are refused.

The function is immutable, security-invoker, owned by `braid_migrator`, with the
fixed `pg_catalog,braid,pg_temp` search path. Only its owner has EXECUTE. Restricted
XML submission and completion entry points invoke it under their owner context.

`DocumentJsonSchemaAdmission` pins the migration checksum and checks the exact
body, input/result signatures, ownership, execution attributes, configuration
and ACL. XML preview admission checks this contract before storage or worker
execution. Missing helpers, overloads and changed body, owner, privileges or
security settings refuse admission.

## Database coverage

`DocumentJsonPersistenceTests` covers recursive duplicate keys, escaped-key
equivalence, nested arrays, restricted XML submission, document triggers and
helper drift refusal before private I/O. `XmlPreviewDatabaseTests` covers exact
envelopes, input/output drift, authority, cancellation and stale-claim completion
fences. Use the [native PostgreSQL test procedure](native-postgres-tests.md).
