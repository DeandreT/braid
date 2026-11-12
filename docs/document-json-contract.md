# Corrective document JSON contract

Migration `0047_document_json_contract.sql` restores the duplicate-key helper
referenced by the immutable XML migration 0042. Its function definition is the
original `document_json_unique(json)` from
`689f44c2:migrations/postgres/0041_flextext_documents.sql`, lines 2–20.

The helper examines `json`, before conversion to `jsonb` could erase duplicate
keys. Each object compares its key count with its distinct-key count, then checks
every value recursively. Arrays recursively check every element. Repeated keys
in separate objects are valid; duplicate keys within any nested object are
refused. Scalar JSON and null retain their original behavior.

The function is immutable, security-invoker, owned by `weft_migrator`, and has the
fixed `pg_catalog,weft,pg_temp` search path. Its only execute ACL is the owner.
Existing restricted XML submission and completion entry points invoke it under
their owner context.

`DocumentJsonSchemaAdmission` pins the migration checksum and checks the exact
function body, input and result signatures, ownership, execution attributes,
configuration and ACL. XML preview admission requires this contract before both
XML and legacy operations. Missing helpers, additional overloads and changed
body, owner, privileges or security settings refuse admission before private
storage or worker execution.

The historical fixture correction reconstructs the exact preview function
definitions for each supported prefix, including PostgreSQL's return-type
formatting. Populated XML conservation begins with an exact original ingestion
fixture, evolves through 0041, proves the isolated 0042 delta, and then applies
the current suffix before exercising XML. Migration 0047 adds only its helper;
0001–0046 retain their original bytes.

## Database coverage

`DocumentJsonPersistenceTests` exercises recursive duplicate keys, escaped key
equivalence, nested arrays, the restricted XML submission path and document
trigger, helper drift refusal before private IO, and populated 0046→0047
conservation followed by repeated migration with completed XML output.
`XmlPreviewDatabaseTests` exercises exact envelopes, output/input drift,
authority, cancellation and stale-claim completion fences.

Native PostgreSQL tests use the owned portable 16.15 fixture. Their authored
completion facts establish database behavior; Linux private-filesystem tests
remain in the Linux lane.

Exact source `74ca32fd3eff2722a9fd9779138b886ff233f1ee` passed locked
restore, Release build and 35 native PostgreSQL 16.15 tests: 35 executed and
passed, zero failures or skips. The owned cluster and disposable checkout were
cleaned up after every phase settled. This selection includes 12 new helper
cases (nine contract drifts), 12 existing XML database cases, six historical/
draft cases, one actual CSV case, three inventory/source cases and one empty/
repeated migration case. Migrations 0001–0046 remain unchanged.

After integration, exact `d62f4962` passed the full Release solution build with
zero warnings/errors and 135 combined creation/document/discovery/migration
tests on native PostgreSQL, zero failures or skips. Its supplementary local
receipt is `Temp/opencode/weft-creation-integration-results/integrated-d62f4962.trx`
under the application-data directory. Neither selection qualifies the two XML
filesystem-prefix conservation cases that require Linux.
