# Offline activation of private storage

The configured application host opens only claim-aware storage: a v2 filesystem
root, or an [Azure Blob store](private-storage-blob.md) when
`WEFT_PRIVATE_STORAGE_KIND=azure-blob`. It passes the actual ingestion or report
claim to the corresponding restricted publication registry.
Opening a root, verified reads, cached reports and receipt replay do not resolve
that registry. The host never initializes, adopts or activates a root.

This integration and its activation procedure are being implemented and
qualified in [#66](https://github.com/DeandreT/weft/issues/66). They do not add a
maintenance command; bounded cleanup follows in #62.

## Explicit provisioning

For a genuinely new root, use the published storage tool:

```sh
dotnet Weft.StorageTool.dll initialize-v2 ROOT APPLICATION_ROOT [REPOSITORY_ROOT]
```

For a Blob store, create the private container first (the tool never creates it),
then run the tool with the same Blob settings as the hosts:

```sh
WEFT_PRIVATE_STORAGE_KIND=azure-blob WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT=... \
WEFT_PRIVATE_STORAGE_BLOB_CONTAINER=... WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL=managed-identity \
  dotnet Weft.StorageTool.dll initialize-blob
```

It prints `InitializedBlob`; an existing marker or any other content prints
`UnsafeStorage`. Blob stores have no v1 profile and need no activation.

The existing `initialize` command continues to create the original v1 library
profile. It does not provision a root for the configured v2 application host.
Both initializers refuse existing roots, and neither creates missing ancestors.
The canonical path, ownership, private permissions and local Linux requirements
in the [storage contract](private-storage-v2.md) apply.

To convert one existing v1 root, first stop and join every application, worker
or tool using that root, then run:

```sh
dotnet Weft.StorageTool.dll activate-v2 --offline ROOT APPLICATION_ROOT [REPOSITORY_ROOT]
```

`--offline` is the operator's assertion that all users have stopped. The tool
does not discover or stop processes, and the flag is not proof of quiescence.
Changing a marker cannot stop an old writer already paused after its final
validation. Every activation or resume invocation requires the flag before any
filesystem work. A successful command prints `Activated` or `AlreadyActive`;
contention prints `Busy` and returns a nonzero exit status. Failure output uses
closed categories without submitted paths or private receipt contents.

Activation preserves the original RootId, every payload byte and inode, and
unknown legacy temporary files. It creates a new control namespace. There is
no recursive production inventory, payload adoption, deletion, rollback or
automatic cleanup. New v1 opens reject the v2 marker after activation.

## Positive recovery evidence

Activation reserves `.weft-private-storage.activation` under the selected root.
The private directory contains a permanent gate, a staged control tree, a
counterpart marker and an immutable receipt. Its control tree moves without
replacement to `.weft-private-control`. Neither reserved path is adopted merely
because its name or contents look familiar.

The canonical receipt has a fixed 628-byte encoding: an 8-byte `WEFTV2A` magic
and newline, big-endian version/profile/length, three network-order UUIDs,
the actual boot/mount-namespace/EUID and offline acknowledgement, nine Linux
identities, and the exact old marker, new marker and control header with their
SHA-256 digests. Each Linux identity contains inode, device major/minor and
mount ID. The identities bind the root, activation directory, permanent gate,
control parent, control namespace, control header, both markers and receipt
itself. The receipt stage's actual inode is observed before its bytes are
encoded; installation preserves that inode.

The complete receipt is installed and synced before the authoritative marker
changes. Immediately before the pivot, activation rechecks every required
named and held identity, marker/header bytes, receipt, gate and recorded
incarnation. One descriptor-relative atomic marker exchange installs the v2
marker and retains the original v1 marker inode in the counterpart slot.
The successful result is prepared before this exchange and returned immediately
after it; no fallible filesystem, database, cancellation or observer work follows.

| Observed state | Result |
| --- | --- |
| No activation artifacts and an exact safe v1 root | Prepare one new activation. |
| Partial preparation without an installed valid receipt | Refuse and retain all artifacts unchanged. |
| Exact receipt, old root marker, new counterpart and staged control tree | Install only that recorded control tree, then continue. |
| Exact receipt, old root marker, new counterpart and installed control tree | Recheck and perform the marker exchange. |
| Exact receipt, new root marker, retained old counterpart and installed control tree | Return the same receipt as `AlreadyActive` without mutation. |
| Missing, substituted, malformed, foreign or ambiguous required evidence | Refuse and retain evidence unchanged. |

A lost activation reply is resolved from the exact exchanged state, not by
rewriting a marker. Same-byte replacement of a receipt, marker, gate or control
artifact cannot substitute for its recorded inode. Interrupted activation resume
requires the recorded live-host incarnation; cross-reboot activation recovery
and power-loss durability are not claimed.

Ordinary v2 opening and reads validate the current root/control protocol and
remain independent of historical activation evidence. They can reopen a safe
activated root after a restart without reopening the activation receipt.

## Application behavior

Native publication forwards the existing actor and ingestion claim; derived
publication forwards the existing actor and report claim. The lazy registry
router resolves only that owner's restricted database family at actual new
registration. Failed resolution is not cached, and no alternate role is tried.
The ownerless v2 write method refuses before opening or consuming payload data.
Deliberately supplied legacy application adapters retain their old interface.

Read-only and cached paths do not require a publication writer or running
worker. Current report selection still requires a configured current backend
profile; retained-by-ID reads can use the historical format when no current
image profile is configured. This procedure changes no report selection rules,
service deadlines, workflow leases, object caps, database schemas or grants.
