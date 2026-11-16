# Private storage initialization

The application host opens a claim-aware filesystem root or an
[Azure Blob store](private-storage-blob.md) selected with
`BRAID_PRIVATE_STORAGE_KIND=azure-blob`. Initialize the store before starting the host.

## Explicit provisioning

For a new filesystem root, run the published storage tool:

```sh
dotnet Braid.StorageTool.dll initialize-v2 ROOT APPLICATION_ROOT [REPOSITORY_ROOT]
```

The initializer refuses existing roots and does not create missing ancestors.
The canonical path, ownership, private permissions and local Linux requirements
in the [storage contract](private-storage-v2.md) apply.

For a Blob store, create the private container first, then use the same Blob
settings as the hosts:

```sh
BRAID_PRIVATE_STORAGE_KIND=azure-blob BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT=... \
BRAID_PRIVATE_STORAGE_BLOB_CONTAINER=... BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL=managed-identity \
  dotnet Braid.StorageTool.dll initialize-blob
```

The tool prints `InitializedBlob`; an existing marker or other content returns
`UnsafeStorage`.

## Application behavior

Native publication forwards the actor and ingestion claim; derived publication
forwards the actor and report claim. The lazy registry router resolves only that
owner's restricted database family at registration. Failed resolution is not
cached, and no alternate role is tried. Writes without an owner claim refuse
before opening or consuming payload data.

Verified reads, cached reports and receipt replay do not resolve the publication
registry. Read-only paths need neither a publication writer nor a running worker.
Current report selection requires a configured backend profile; retained-by-ID
reads use the report's stored format and identity.
