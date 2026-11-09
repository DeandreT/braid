# Supplementary managed privacy probe

This controller launches only synthetic children that it owns. It first proves a
normal dumpable positive control can be attached to and read by its ordinary
non-root, capless parent. It then checks the real sandbox launcher after a managed
startup report and after rejected privacy mutations on Main and another managed
thread. It never traces startup, inherits a tracer, grants a ptracer, changes Yama,
adds capabilities or changes deployment policy. A blocked positive control is
inconclusive, not evidence of isolation.

The scope is continuity from successful native hardening through .NET startup and
execution. Trusted deployment origin and an immutable admitted installation are
prerequisites. These checks cannot establish absence of a preattached tracer or
preopened memory handle before hardening. Process, executable-file and credential
provenance are recorded for the owned synthetic children, not used as remote
attestation.

Use the existing checksum-verified SDK 10.0.112, CoreCLR/hostfxr 10.0.12 and the
launcher built from the source being qualified. No package restore is required:

```text
python3 tools/compile-sandbox/privacy_negative_probe.py prepare --dotnet-root ROOT --directory FIXTURE
# Execute the emitted compile_command with that pinned dotnet/csc installation.
python3 tools/compile-sandbox/privacy_negative_probe.py run --dotnet-root ROOT --launcher LAUNCHER --probe-assembly FIXTURE/weft-privacy-probe.dll --work-root WORK
```

`FIXTURE` must be a new path; `WORK` must exist on approved local storage. Run in
the deployment's non-root, read-only installation boundary, with the fixture
mounted read-only as a trusted test entrypoint. The fixture has its own test
identity and must not be admitted as the production runtime. For explicit
falsification against an old launcher, select `--contract legacy --expect vulnerable`.
That outcome demonstrates a defect, never a qualification pass for protection.

One 30-second deadline covers both children and all report/attach/memory phases.
One additional two-second cleanup budget covers every owned child. Unconfirmed
settlement fails the probe and retains its exact owned directory instead of
letting temporary-directory cleanup erase it. Its `ownership.json` records each
owned PID, start ticks and initial command. Resolve that ownership before cleanup.

Run controller unit checks natively with:

```text
py -3.13 -m unittest discover -s tools/compile-sandbox -p test_*.py
```

This is supplementary evidence only. Qualify the actual production runner calls,
fixed pinned-csc deterministic artifacts, read-only runtime success/refusal,
limits, cancellation and process/pipe/directory settlement separately. It is not
evidence of an XML canonical-comparison call. See the boundary and qualification
requirements in `docs/compiled-service-runtime.md` and `docs/build-compiler.md`.

## Actual Linux boundary qualification

Use a frozen source export and approved local build/output/cache paths. Verify
the Btrfs mount identity and host free-space reserve before Linux writers. Keep
Windows artifacts out of the Linux input/output set and serialize the heavy
qualification lane. Reuse the existing pinned Rust toolchain, SDK, packages and
native worker/compiler images, without installs or policy changes.

1. Stage the frozen inputs in an owned qualification area accessible to numeric
   UID/GID **65532:65532**, without changing shared SDK ancestor permissions.
   Use owned staging or the established private read-only bind arrangement;
   give only owned CLI-home/output/scratch paths the required write access.
   Require matching real/effective/saved/filesystem IDs and empty inherited,
   permitted, effective and ambient capabilities. Capless root is not equivalent.
   Run Rust 1.98.1 format, clippy and release tests for `engines/compile-sandbox`.
   The native probes exercise
   ABI 3, 4, 5 and the real kernel ABI, plus both exec calls, credential/capability
   setters, dumpability 0/1 and upper-bit aliases, and another thread.
2. Build the real launcher and the .NET tests with the pinned Linux SDK. Set
   `WEFT_FERRULE_HOST_EXECUTABLE`, `WEFT_COMPILE_SANDBOX_EXECUTABLE` and
   `DOTNET_ROOT` to those exact builds. Always retain
   `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`, including isolated CLI homes.
3. Run `FerruleServiceCompileTests`, `FerruleCompiledServiceTests`, the identity
   tests and the service admission/transport tests. Use the existing worker and
   compiler image pins for equivalence cases, not newly registered pipelines.
   Require actual deterministic csc artifact equality, successful read-only
   compiled execution, fixed empty-frame refusal, asynchronous managed startup,
   rejected mutations, deadlines/cancellation and confirmed child/pipe/directory
   settlement. A skip or failed prerequisite does not qualify the omitted case.
4. Run the supplementary positive/negative controller above without an attach
   exemption. Exercise missing pinned hostfxr, a wrong hosting library/export,
   stale pins, old launch contracts and alternate framework/probing selections.
   Rebuild and measure the exact read-only non-root service image and verify its
   admission and real runtime/compile calls as UID/GID 65532:65532 with empty
   active capabilities. Keep Docker reference operations in the existing
   authorized controller, not the service account; do not grant that account
   Docker-socket access or alter groups. Bind equivalence comparisons to the
   same exact frames, worker, toolchain and captured source. The initial native
   loader's library layout must be
   the measured immutable layout, not an unmeasured loader-cache-only search root.
5. Keep compiler/runtime pin changes explicit through normal qualification and
   admission. Never auto-approve a replacement identity or fall back to exec.
   XML stays disabled until separately integrated and qualified; preserve its
   aggregate two-second cleanup boundary when integrating this launcher repair.
