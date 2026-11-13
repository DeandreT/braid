# braid specification Blob qualification

The specification adapter now has actual Azure SDK/network emulator tests in
`BlobSpecObjectStoreAzuriteTests`, plus a composed PostgreSQL/Blob recovery test
in `SpecBlobPersistenceTests`. They supplement the offline SDK-seam contracts in
`SpecObjectStorageTests`.

## What is exercised

- Original and extraction publication, exact reads and create-only replay,
  including unchanged ETags on replay.
- Permanent zero-payload retirement fences, including retirement before a
  delayed upload and empty payloads.
- Corruption refusal, bounded source consumption, exact scope, current container
  privacy and store-marker incarnation checks.
- Real restricted PostgreSQL writer/reader/maintenance sessions, their schema
  admission, and actual Blob publication. A synthetic exception after the
  awaited upload but before database completion rolls back the real PostgreSQL
  acknowledgement. Fresh database pools and adapters recover the same claim,
  verify the physical object and publish the durable inventory result.
- Content-grant revocation, pending/published tombstones and actual physical
  retirement through the restricted maintenance session.

The adapter-only cases deliberately use a synthetic registry. The composed case
uses real PostgreSQL transactions; its fault injection is still synthetic, not a
cloud outage or a killed-host experiment.

## Emulator selection

The existing `WEFT_AZURITE_IMAGE` mode retains its immutable Linux image pin and
resource limits. Alternatively, tests can use an already running emulator with
`WEFT_AZURITE_LOOPBACK_ENDPOINT=http://127.0.0.1:<port>/devstoreaccount1`.
The modes are mutually exclusive. External endpoints must use that exact
loopback host and account path, without credentials, query or fragment.

External mode never starts or stops the supplied service. Its caller owns that
process and must stop it after the tests, including on failure. Tests create
unique containers with atomic fixture-ownership metadata; cleanup verifies that
ownership before removal and reports failures. Production endpoint admission
and authentication are unchanged.

With both PostgreSQL and emulator fixtures configured, run:

```text
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~BlobSpecObjectStoreAzuriteTests|FullyQualifiedName~SpecBlobPersistenceTests|FullyQualifiedName~BlobPrivateObjectStoreTests|FullyQualifiedName~AzuriteEndpointTests"
```

Windows PostgreSQL uses `WEFT_POSTGRES_BIN` and an existing, empty execution
work directory in `WEFT_POSTGRES_WORK_ROOT`. Linux uses the pinned Docker
PostgreSQL fixture. Do not point either fixture at a customer database.

## Historical Windows check — 2026-11-12

On base `930ba1f5` plus the pending test changes, locked restore and the full
Release solution build passed with zero warnings/errors. The actual network
selection passed **76/76, zero skipped or failed**:

- 14 specification Blob adapter cases.
- One composed durable PostgreSQL/specification Blob case exercising both
  original and extraction recovery.
- 40 existing Blob-specific cases.
- 21 endpoint/fixture-ownership cases.

Execution used native Windows Node 22.23.3, Azurite 3.35.0 and PostgreSQL 16.15.
Azurite was loopback-only with in-memory storage, telemetry disabled and bounded
extent/V8 memory settings. Package integrity and test-source hashes were
recorded. The owned emulator and database processes were stopped afterward.

That Windows run established local emulator/adapter/database interoperability;
it did not execute the pinned Linux emulator image. The separate Linux results
below do not replace or enlarge its historical scope.

## Completed local Linux check — 2026-11-12

On base `930ba1f5` plus uncommitted qualification changes, the storage selection
using the pinned Linux PostgreSQL and Azurite fixtures passed **159 tests, zero
failures or skips**. It included XML conservation checks and actual durable
PostgreSQL/Blob recovery, including
`SpecBlobPersistenceTests.Real_registry_recovers_uploaded_original_and_extraction_then_revokes_and_retires`.
This extends the emulator evidence to the pinned Linux fixtures and the real
database/Blob composition, rather than only an offline SDK seam.

The initial focused selection separately passed **7/7, zero skipped or failed**
after a test-only database clock-precision correction. These focused, Windows
and Linux selections overlap; their counts must not be summed. Production
runtime code, endpoint admission and activation settings were unchanged.

Real Azure storage and live-provider acceptance were explicitly deferred. Local
PostgreSQL/Azurite success is not Azure acceptance, document-worker containment,
production-environment equivalence or deployment qualification. The bounded
installed-document results are recorded separately in
[installed document worker qualification](spec-document-installed-qualification.md).

The separately approved installed hostile/lifecycle selection also passed
**16/16 on its first execution, zero failures or skips**, including four witnessed
qualified-extraction/default-host shutdown races. Its reviewed local Linux scope
is closed; see [installed hostile qualification](spec-document-installed-hostile-qualification.md)
for the protected image, retained receipts, cleanup and compositional CPU/PDF
limits. Prior matching-image Docker equivalence passed **4/4** and the CI launcher
unit selection passed **15 tests locally**; neither is hosted CI or Azure evidence.
