# Installed document hostile-input and lifecycle checks

The installed suites exercise Office/PDF parsing, resource bounds, interruption,
cleanup and host shutdown through the configured production runners. Installation
settings are described in [document host configuration](spec-document-host.md).

## Prepare the test environment

Use a protected installation with the exact worker, launcher, managed runtime and
assembly pins. Configure `BRAID_SPEC_DOCUMENT_WORKERS=optional` and
`BRAID_TEST_DOCUMENT_HOST_LINUX=1`, with the matching `BRAID_SPEC_DOCUMENT_*` paths
and identities. Root/capabilities, writable executable backing aliases and invalid
roots fail installation admission.

Run the tests in a dedicated container PID namespace. PID 1 is the external
controller; the only other initial processes are the testhost's actual
vstest/dotnet ancestor chain. Keep unrelated jobs, exec observers, overlapping test
runs and sidecars out of this namespace. The controller records namespace and
PID/start-time identities before launch and supplies the record read-only under
a protected parent:

```text
BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD=/protected/qualification/pid-namespace.json
BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256=<trusted raw lowercase SHA-256>
```

The record uses this case-sensitive schema; the numbers are examples:

```json
{
  "Format": "braid.document-pid-namespace.v1",
  "PidNamespace": "pid:[4026533001]",
  "ParentPidNamespace": "pid:[4026531836]",
  "Controller": { "Pid": 1, "StartTicks": 123456 },
  "Baseline": [{ "Pid": 1, "StartTicks": 123456 }]
}
```

`StartTicks` is unsigned `/proc/<pid>/stat` field 22. Baseline contains exactly
PID 1; the validator adds only the testhost's actual ancestors. It requires a
normalized nonsymlink path, read-only file and parent mounts, regular single-link
file and matching hash, and refuses writable aliases of that backing path. Keep
the source inode in an unmapped private 0700 directory and seal it before launch.
Keep this nonexecutable record outside worker/runtime/loader/test-assembly trees.

Verify complete procfs visibility. The record namespace must differ from its
external parent's namespace and match the testhost's own procfs identity. The
census bounds visible processes to 64 and compares PID/start-time pairs at each
settlement. New survivors, including zombies or children reparented to PID 1,
fail without being signaled. Reconcile unexpected survivors before reuse.

Supply writable `TMPDIR`, SDK home/cache and result locations before starting
.NET. Dedicated Office/PDF work roots must initially be empty. The driver does
not create SDK paths or recursively delete roots.

```sh
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~Braid.Tests.SpecDocumentInstalledHostileTests' \
  --results-directory "$APPROVED_WRITABLE_RESULTS" --logger 'console;verbosity=normal'
```

Match the prepared installation's build configuration. Run the shared-profile,
resource and shutdown-race classes with their separate filters below.

## Hostile input and interruption

`SpecDocumentInstalledHostileTests` covers:

- DOCX/XLSX traversal, external HTTP/file relationships, DTD/entity forms,
  truncated ZIPs, damaged CRCs, compression ratio, deep XML, byte/entry/node/chunk
  budgets, canonical serialization overflow and password-encrypted Office input.
  Refusals must be atomic, retain source/profile/evidence binding and leave roots
  empty with no surviving new process. Follow refusals with readable extraction.
- Caller cancellation and default deadlines through pinned Office/PDF runners,
  using EOF withholding. Witness a real owned operation before interruption.
  Verify native exit/reap, original I/O task completion, cancellation, handles and
  directory cleanup. Follow interruption with a fresh successful runner.
- Stop/Dispose with real registered runners held by EOF withholding. Shutdown
  must wait for their cancellation and settlement; retained references reject
  new work.
- Office directory-removal fault injection through `BeforeCleanupStage` after
  native/I/O/handle settlement. Require empty admitted output, `CleanupFailed`,
  the same retained owner fault, unavailable admission and repeated Stop/Dispose
  failure. Teardown joins the owner and removes only its exact empty directory;
  deleting it must not rehabilitate the runner.

Encrypted fixtures are embedded in the test source and rechecked by length/hash.
They must return `Unsupported / legacy-or-encrypted-office` with empty chunks
and correct binding. The fixture password is `braid-fixture-only`, not a credential.

Settlement combines `/proc/self/task/*/children` with the complete namespace
census. Deadline checks use the runner's original cleanup deadline.

## Shared Office profile probe

`Braid.CompiledSandboxProbe` checks outside-read, private-write, assembly-metadata,
socket, fork and descriptor-owner signal denial, environment clearing and sealed
privacy. Install its assembly, runtimeconfig/deps and required closure read-only,
with separate assembly and Office-operation composite runtime pins:

```text
BRAID_TEST_DOCUMENT_PROFILE_PROBE_ASSEMBLY=/protected/probe/Braid.CompiledSandboxProbe.dll
BRAID_TEST_DOCUMENT_PROFILE_PROBE_BUILD_SHA256=<raw assembly SHA-256>
BRAID_TEST_DOCUMENT_PROFILE_PROBE_RUNTIME_SHA256=<raw Office-operation composite identity>
FullyQualifiedName~Braid.Tests.SpecDocumentInstalledSharedProfileProbeTests
```

The test uses `ProcessCompiledRuntimeRunner` with `Operation.OfficeExtraction`
and unchanged native arguments. Keep the synthetic readable marker outside the
probe's read grants. Remove only that owned marker. The probe's identity is
separate from the parser's; never substitute one hash for the other.

## Resource probe

`dotnet/tools/Braid.InstalledResourceProbe` is standalone test tooling. Install its
closure read-only and configure its separate pins:

```text
BRAID_TEST_DOCUMENT_RESOURCE_PROBE_ASSEMBLY=/protected/resource-probe/Braid.InstalledResourceProbe.dll
BRAID_TEST_DOCUMENT_RESOURCE_PROBE_BUILD_SHA256=<raw assembly SHA-256>
BRAID_TEST_DOCUMENT_RESOURCE_PROBE_RUNTIME_SHA256=<raw Office-operation composite identity>
FullyQualifiedName~Braid.Tests.SpecDocumentInstalledResourceProbeTests
```

Both cases admit the production Office/PDF host and use the Office-operation
runner's fixed controls:

- `memory`: a 64 KiB mmap succeeds; a 4 GiB reservation fails with `ENOMEM` under
  the 3 GiB address-space ceiling. No large pages are touched. Require successful
  task/reap/handle/directory settlement and no surviving new process identities.
- `overflow`: emit exactly `SpecOfficeProcessProtocol.MaximumResponseBytes + 1`
  bytes (10 MiB + 2048 + 1). Require `OutputLimitExceeded`, empty admitted output,
  complete settlement and a still-available runner, followed by a successful
  memory probe. The stdout pipe cap is separate from the OS file-size limit.

The runners use fixed operation caps, not the portable test harness's adjustable
`Cap` properties. Parser serialization budgets are separate from transport stdout
overflow. `TimedOut` denotes the parent wall deadline; generic `WorkerFailed`
does not identify SIGXCPU or OOM. Office has 30-second CPU/wall controls, 3 GiB
address space and a 512 MiB managed heap setting. PDF has 10-second CPU/wall,
512 MiB address space and 64-descriptor limits.

## Default-host shutdown races

```text
FullyQualifiedName~Braid.Tests.SpecDocumentInstalledHostShutdownRaceTests
```

The suite crosses Office/PDF with Stop/Dispose. Each attempt constructs the default
`SpecDocumentHostResources`, admits qualified workers and submits a captured
approximately 7 MiB valid document. Procfs inspection witnesses a live direct
child's PID/start-time and fixed controls while the qualified call is pending;
dispatch Stop/Dispose immediately after that sample.

A completed extraction winning the race is not overlap. Settle each attempt
before the next; failure to witness overlap within eight attempts is inconclusive.
Protocol, admission and cleanup failures propagate rather than becoming retries.
Require qualified continuations and native owners to settle, retained references
to reject new work, and roots/census to be clear.
