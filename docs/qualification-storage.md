# Linux qualification storage

Keep an operator-managed JSON profile outside the checkout. Pin values from
your approved storage inventory; do not generate the expectations from the
candidate work directory on every run. On Linux the compiler builder takes the
profile's absolute path via `--storage-profile` or `BRAID_STORAGE_PROFILE`.
The WSL browser fixture takes `--storage-profile` at preparation time and checks
the profile again when it runs. Use a canonical path without symlinks. The file
and every ancestor must belong to root or the invoking user, with no group or
other write permission. For the root-operated browser fixture, use a root-owned
profile in a root-controlled directory.

| Field | Expected value |
| --- | --- |
| `target` | Absolute mountpoint for the approved Btrfs volume |
| `source` | Exact `findmnt` SOURCE, including its subvolume suffix if reported |
| `uuid` | Filesystem UUID |
| `fsroot` | Exact `findmnt` FSROOT |
| `requiredOptions` | Nonempty JSON array of exact mount-option strings. All runs require writable Btrfs; WSL also requires a pinned compression option (for example `["rw", "compress=zstd:3"]`). |
| `windowsHostMount` | WSL only: absolute mountpoint of the Windows drive holding the backing disk. Its available space must be at least 25 GiB. |

The browser fixture additionally requires `workRoot` (exports and publications),
`dataRoot` (private fixture state), `cacheRoot` (tool home and NuGet caches),
`dockerRoot` (Docker Engine's actual data directory), and `dotnet` and `bun`
(Linux executable paths). Provide existing canonical absolute paths on the
approved writable Btrfs mount. Use separate work and data directories, private
operator-controlled storage and a profile writable only by trusted operators.

Example structure for native Linux; substitute values before use:

```json
{
  "target": "<approved-mountpoint>",
  "source": "<approved-device-and-subvolume>",
  "uuid": "<approved-filesystem-uuid>",
  "fsroot": "<approved-subvolume-root>",
  "requiredOptions": ["rw"]
}
```

For WSL, add `windowsHostMount` and the required compression option. For a
browser run, add the six browser fields above. Missing settings, a different
device or mount, or insufficient host disk space stop the corresponding tool.
