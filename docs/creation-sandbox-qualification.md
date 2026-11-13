# braid creation service-child qualification

`CreationSandboxQualificationTests` exercises the actual `weft-ferrule-host` through
`ProcessWorkerRunner`, with the fixed `Authoring` and `AuthoredPreview` profiles.
It uses `FerruleInspectionBackend.AuthorAsync` and `PreviewAuthoredAsync`, not the
portable protocol examples. No production admission flag is added or bypassed.

## Prerequisites and execution

Use a Linux source checkout and the repository-pinned Rust and .NET toolchains.
Build the real host from that checkout (including its native system dependencies):

```sh
cargo build --locked --release --manifest-path engines/ferrule-host/Cargo.toml
export WEFT_FERRULE_HOST_EXECUTABLE="$(realpath engines/ferrule-host/target/release/weft-ferrule-host)"
test -x "$WEFT_FERRULE_HOST_EXECUTABLE"
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
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj \
  --filter FullyQualifiedName~CreationSandboxQualificationTests \
  --logger 'console;verbosity=normal'
```

Use the configuration and restore/build options appropriate to the prepared
checkout; `--no-build` is appropriate only after building these test sources.
Windows and runs without `WEFT_FERRULE_HOST_EXECUTABLE` report explicit skips
through `ServiceWorkerFact`. A configured but missing, incompatible or unhardened
host fails qualification rather than being treated as a successful skip.

## Checks and limits of the evidence

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
  transport profile admission, not arbitrary raw-protocol rejection inside the
  executable.
- Malformed authoring/sample inputs, digest mismatch and oversized plan/sample
  inputs produce bounded refusals. These are representative boundaries, not a
  complete adversarial grammar or resource-exhaustion matrix.
- Cancellation and wall deadlines are checked separately for both profiles.
  The existing internal EOF-withholding seam keeps a real sandboxed worker alive;
  it does not replace the runner or weaken sandbox verification. A successful
  operation follows each interrupted operation.
- Normal, refused and interrupted operations leave no live worker child and an
  empty private work root. Cleanup deletes only an empty root so leftovers remain
  available if a settlement assertion fails.

The normal runner verifies the readiness marker, procfs controls and resource
limits before sending request bytes, and clears the child's environment. These
tests do not inject a fake protocol runner. Run the existing Rust
`sandbox_qualification` suite separately for direct network/process/filesystem
denial probes. Passing this .NET suite alone does not qualify the entire hostile
matrix, Docker equivalence, service endpoint enablement or deployment readiness.
Record the source revision/pending patch set, toolchain versions, executable hash,
kernel/filesystem context and actual command/test results with the qualification
evidence. The completed local runs below have narrower scope than whole-project
or deployment acceptance.

## Recorded local qualification — 2026-11-12

The Linux runs used base `930ba1f5` plus uncommitted qualification changes,
repository-pinned toolchains and local Btrfs execution storage. Production runtime
code and admission gates were unchanged. The initial focused selection passed
**7 tests, zero failures or skips**, after a test-only PostgreSQL clock-precision
correction. The earlier Windows regression selection remains historical evidence:
**140 passed, three skipped**, rather than Linux sandbox execution.

The actual `CreationSandboxQualificationTests` selection passed **all three facts,
zero failures or skips**:

- `All_36_pairs_author_and_preview_exact_bytes_with_the_same_sandboxed_build`:
  all 36 real format pairs, with the original and changed samples.
- `Fixed_profiles_refuse_cross_profile_dispatch_and_bound_invalid_inputs`:
  fixed-profile admission and bounded refusals.
- `Cancellation_and_deadline_join_live_children_for_both_creation_profiles`:
  actual cancellation, deadlines, recovery and cleanup for both profiles.

Native Rust checks passed 53 test cases and the 12 compile-sandbox/eight
Ferrule sandbox scenario groups. A native .NET selection passed **110 tests with
three historical Docker-equivalence skips**; a managed selection passed **52 with
one historical skip**. These are overlapping selections, not additive totals.

The four matching Docker-equivalence cases were subsequently executed and passed
**4/4, zero failures or skips** using approved local images containing the same
native executable bytes:

- `sha256:a2829f58ff84a790b365ebad3232ec5c41be53e6647d5b592a1797b59d6ed5b1`
- `sha256:c7f3ddeb8abd177c556ac3043c59c3484d6872a8408a82175321a69c2703db3a`

That run followed a narrow permission correction to the local staging directory.
It establishes those four local equivalence results, not production-environment
equivalence. The WSL kernel reported `AppArmor=N`; the built-in seccomp and native
Landlock controls were unchanged. No privileged/unconfined mode, capability
addition or policy override was used. AppArmor enforcement was not qualified.

## CI prerequisite follow-up

The generic CI workflow now builds the actual PDF worker and required portable
authoring compiler example with Rust `1.98.1`, release mode and locked dependencies,
installs them under `RUNNER_TEMP`, and exports their executable paths before the
existing exhaustive .NET shards. Both prerequisite builds passed locally offline;
the 15 CI launcher unit tests also passed. This is local validation, not a hosted
CI result. Existing triggers, shard coverage, required checks and optional
qualification flags remain unchanged.

The separately reviewed installed document hostile/resource/lifecycle/census/race
selection subsequently passed **16/16 on its first execution, zero failures or
skips**, including four witnessed qualified-extraction/default-host shutdown
races. See [installed hostile qualification](spec-document-installed-hostile-qualification.md)
for the approved image, retained evidence, settlement and compositional CPU/PDF
limits. This closes the reviewed local Linux qualification scope, not whole-project
or deployment acceptance. AppArmor enforcement and production-environment
equivalence remain unestablished; live-provider and Azure acceptance remain
explicitly deferred.
