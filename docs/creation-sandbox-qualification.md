# braid creation service-child qualification

`CreationSandboxQualificationTests` exercises the actual `braid-ferrule-host` through
`ProcessWorkerRunner`, with the fixed `Authoring` and `AuthoredPreview` profiles.
It uses `FerruleInspectionBackend.AuthorAsync` and `PreviewAuthoredAsync`, not the
portable protocol examples.

## Prerequisites and execution

Use a Linux source checkout and the repository-pinned Rust and .NET toolchains.
Build the real host from that checkout (including its native system dependencies):

```sh
cargo build --locked --release --manifest-path engines/ferrule-host/Cargo.toml
export BRAID_FERRULE_HOST_EXECUTABLE="$(realpath engines/ferrule-host/target/release/braid-ferrule-host)"
test -x "$BRAID_FERRULE_HOST_EXECUTABLE"
```

If Cargo uses an external `CARGO_TARGET_DIR`, set the executable path to that
directory's release binary instead. Use the main host's built-in
`--service-sandbox` mode; `ProcessWorkerOptions` supplies this argument and the
profile limits. A portable authoring/preview example is not an eligible host.

Choose an existing, writable local Btrfs temporary root for the run. Keep the
checkout, Cargo/.NET output and temporary work off Windows-mounted paths and
cloud-synced storage. Set `TMPDIR` before starting .NET so the shared temporary-root helper
uses that filesystem:

```sh
export TMPDIR=/srv/example-build/work/creation-qualification-tmp
mkdir -p "$TMPDIR"
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj \
  --filter FullyQualifiedName~CreationSandboxQualificationTests \
  --logger 'console;verbosity=normal'
```

Use the configuration and restore/build options appropriate to the prepared
checkout; `--no-build` is appropriate only after building these test sources.
Windows and runs without `BRAID_FERRULE_HOST_EXECUTABLE` report explicit skips
through `ServiceWorkerFact`. A configured but missing, incompatible or unhardened
host fails qualification rather than being treated as a successful skip.

## Checks

- All 36 pairs of JSON, XML, CSV, TSV, X12 and EDIFACT compile and execute two
  fields with both original and changed samples. Fixtures mirror the existing
  format tests, including EDI envelopes and XML namespaces/attributes.
- Both runners must identify the same exact executable SHA-256. Compilation
  retains the compiler-plan digest, deterministic native bytes, validation,
  projection and build identity. Preview retains the exact project/authoring,
  proposal/native/sample digests, output digest and build identity.
- The backend refuses creation requests dispatched with another fixed process
  profile before starting a child. The transport's frame-admission helpers also
  reject creation protocol headers under other profiles. These check backend and
  transport profile admission.
- Malformed authoring/sample inputs, digest mismatch and oversized plan/sample
  inputs produce bounded refusals.
- Cancellation and wall deadlines are checked separately for both profiles.
  The existing internal EOF-withholding seam keeps a real sandboxed worker alive;
  it does not replace the runner or weaken sandbox verification. A successful
  operation follows each interrupted operation.
- Normal, refused and interrupted operations leave no live worker child and an
  empty private work root. Cleanup deletes only an empty root so leftovers remain
  available if a settlement assertion fails.

The normal runner verifies the readiness marker, procfs controls and resource
limits before sending request bytes, and clears the child's environment. These
tests use the real protocol runner. Run the Rust
`sandbox_qualification` suite separately for direct network/process/filesystem
denial probes.
Record the source revision/pending patch set, toolchain versions, executable hash,
kernel/filesystem context and actual command/test results with the qualification
evidence.
