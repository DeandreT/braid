# braid specification Blob qualification

The specification adapter has Azure SDK/network emulator tests in
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

The adapter-only cases use a synthetic registry. The composed case uses real
PostgreSQL transactions and injects a failure after upload.

## Emulator selection

The existing `BRAID_AZURITE_IMAGE` mode retains its immutable Linux image pin and
resource limits. Alternatively, tests can use an already running emulator with
`BRAID_AZURITE_LOOPBACK_ENDPOINT=http://127.0.0.1:<port>/devstoreaccount1`.
The modes are mutually exclusive. External endpoints must use that exact
loopback host and account path, without credentials, query or fragment.

External mode never starts or stops the supplied service. Its caller owns that
process and must stop it after the tests, including on failure. Tests create
unique containers with atomic fixture-ownership metadata; cleanup verifies that
ownership before removal and reports failures. Production endpoint admission
and authentication are unchanged.

With both PostgreSQL and emulator fixtures configured, run:

```text
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~BlobSpecObjectStoreAzuriteTests|FullyQualifiedName~SpecBlobPersistenceTests|FullyQualifiedName~BlobPrivateObjectStoreTests|FullyQualifiedName~AzuriteEndpointTests"
```

Windows PostgreSQL uses `BRAID_POSTGRES_BIN` and an existing, empty execution
work directory in `BRAID_POSTGRES_WORK_ROOT`. Linux uses the pinned Docker
PostgreSQL fixture. Do not point either fixture at a customer database.
