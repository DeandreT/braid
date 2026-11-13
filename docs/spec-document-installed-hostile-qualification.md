# braid installed hostile/lifecycle qualification

**Independently reviewed local qualification passed: 16/16 on the first execution, zero failures or skips.** The exact selection comprises nine main hostile/lifecycle cases, one shared Office-profile denial probe, two separately pinned resource probes and four genuine default-host shutdown/qualified-extraction races. All four races witnessed interrupted overlap on their first internal attempt. The final independent review cleared this bounded local scope with no targeted blocker. AppArmor enforcement, production-environment equivalence and full hostile-input acceptance are not established; live-provider/Azure acceptance remains user-deferred.

## Reviewed installation and execution

The reviewed `SpecDocumentInstalledHostileTests.cs`, `SpecDocumentInstalledHostShutdownRaceTests.cs` and standalone `Weft.InstalledResourceProbe` were integrated into the frozen qualification context. They reuse fixture builders, friend-assembly APIs, native ABI reads and the existing nonparallel installed-document collection. Production source, native arguments and admission policies remain unchanged. The resource helper has its own reviewed source build/install/pins and is not a production dependency.

The approved image was built offline on local Btrfs with SDK `10.0.112` / runtime `10.0.12`, zero warnings/errors. All 2,821 frozen context files were verified; Gate compiled and ten linked C# regressions and 105 controller/driver selftests passed. The parent separately reviewed the measured 20 candidate values, source/manifest/native identities and immutable image configuration before approving execution. Future installations require their own protected build and independent pin approval; expected pins must not be manufactured from arbitrary installed files.

The run used independently approved `WEFT_SPEC_DOCUMENT_*` settings for this exact image, including `WEFT_SPEC_DOCUMENT_WORKERS=optional` and `WEFT_TEST_DOCUMENT_HOST_LINUX=1`. Direct-runner entrypoints invoke unchanged installation validation: opting in with root/capabilities, writable protected executable mounts/backing aliases or bad roots fails. Default production host startup and its actual factory probes also succeeded for the main nine cases.

Image: `sha256:f0b5a168c705a6eb4073bfdc67a83efd55efa7f00164ad8720064fbb06fd3f0c`.
The container ran as UID/GID 1000, read-only root, network none, all capabilities dropped, no-new-privileges and built-in default seccomp. AppArmor was unavailable; no AppArmor override or native admission bypass was used.

### Mandatory dedicated PID namespace and external baseline record

The driver ran in a dedicated protected container PID namespace: only the externally recorded PID-1 Gate controller plus this testhost's actual vstest/dotnet ancestor chain. No arbitrary child/descendant whitelist is accepted. No unrelated jobs, exec observers, overlapping test runs or sidecars may enter this namespace during qualification. Existing ancestor identities are preserved, never signaled. The existing frozen four-case driver is unchanged and gains no such prerequisite retroactively.

The reviewed external controller recorded the runtime namespace and controller identity before operations, retained independent root observation, and exposed the JSON as a read-only file bind under a protected read-only parent path. Its normalized absolute path and approved SHA-256 were supplied through the two environment variables below. This is **nonexecutable controller evidence**, not worker/native admission. Runtime namespace facts cannot be baked into the original image. Gate waited for the sealed external context before starting the exact suite.

The test-only validator requires a normalized no-symlink path, effective read-only file and parent mounts, a regular file with returned statx type/mode/link facts and **nlink=1**, and exact expected hash. It refuses a visible writable mount that exposes this file's exact backing path. Unlike executable admission it permits disjoint writable Btrfs roots on the same device: no broad same-device ban is applied to this data record. Keep the record target outside worker/runtime/loader/test-assembly trees so their unchanged stricter subtree checks are not applied to this nonexecutable bind. The controller must keep the record's source in an unmapped private 0700 directory outside all writable mounts/grants, seal the inode before test launch, and supply the expected hash through trusted launch configuration, not document/request data. The child cannot prove the external source's privacy/provenance by itself; retain those facts in the parent's run evidence. The source does not generate its own expected record/hash or accept a `dedicated=true` flag. **`Settings.Validate` and all production executable/runtime same-device alias protections remain unchanged.**

```text
WEFT_TEST_DOCUMENT_PID_NAMESPACE_RECORD=/protected/qualification/pid-namespace.json
WEFT_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256=<independently approved raw lowercase SHA-256>
```

Record schema (illustrative numbers only; field names are case-sensitive). `StartTicks` is Linux `/proc/<pid>/stat` field 22, parsed as an unsigned integer. `Controller` must identify PID 1 inside this namespace. `Baseline` must contain exactly that controller; only the testhost's actual ancestor chain is added by inspection. Do not include arbitrary siblings, descendants or short-lived observer/exec processes.

```json
{
  "Format": "weft.document-pid-namespace.v1",
  "PidNamespace": "pid:[4026533001]",
  "ParentPidNamespace": "pid:[4026531836]",
  "Controller": { "Pid": 1, "StartTicks": 123456 },
  "Baseline": [{ "Pid": 1, "StartTicks": 123456 }]
}
```

Each test snapshots every visible process's PID/start-time before startup/operations and compares every settlement with that identity set. Initial processes outside PID 1 and the actual testhost ancestor chain fail the test. Namespace identity must match the externally recorded value and differ from the recorded external parent's namespace; a 64-process implementation bound also applies. The external owner must confirm complete procfs visibility. The procfs self PID/start-time must match this testhost's actual identity, guarding against an ancestor namespace's procfs view. Newly surviving identities fail even if reparented to PID 1, renamed or zombie; PID reuse cannot masquerade as the old baseline identity. No processes are signaled by this census. There is no name/PPID/zombie filter, global subreaper change or best-effort kill of an unexpected process. An unexpected survivor is evidence requiring owner reconciliation.

The successful execution in the externally verified context establishes **no new surviving descendant/orphan in the complete dedicated visible namespace** at the checked settlements for these cases. It does not prove no transient child ever existed or observe another namespace. The namespace record is not production worker admission. The actual namespace was `pid:[4026532228]`, distinct from external `pid:[4026532215]`; Gate was PID 1/startTicks 830, independently attributed through host PID 964.

Main selection: **nine xUnit cases** (two format cases containing 18 hostile/budget/encryption fixtures each; four real-runner interruption cases; two controlled-host ownership cases; one installed Office cleanup-fault case):

```sh
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~Weft.Tests.SpecDocumentInstalledHostileTests' \
  --results-directory "$APPROVED_WRITABLE_RESULTS" --logger 'console;verbosity=normal'
```

Match the prepared image configuration instead of assuming Release. Supply the established writable `TMPDIR`, SDK home/cache and results environment before starting .NET; the driver does not create SDK paths or provision mounts. Dedicated Office/PDF roots must initially be empty and there must be no unrelated test-process children. The driver never recursively deletes work roots. The unchanged production runners retain their own cleanup behavior.

The shared-profile probe is a separate one-case filter; the resource probe is a separate two-case filter. The genuine default-host race suite adds four cases. All prerequisites were independently approved for the exact 16-case inventory. Do not substitute a broad `SpecDocumentInstalled` filter or treat missing approved probe settings as a passing skip.

## Main case coverage and limits

- Both DOCX and XLSX travel through production `SpecDocumentHostResources` startup and the actual `QualifiedOfficeProcessWorker`: traversal, external HTTP/file relationships, three DTD/entity forms, truncated ZIP, damaged CRC, compression ratio, deep XML, source/entry-count/entry-size/aggregate-expansion/XML-node/chunk budgets, canonical serialization overflow and a genuine password-encrypted Office fixture. Every refusal must be atomic, source/profile/evidence-bound and followed by empty roots/no new surviving processes; a readable extraction follows the matrix.
- These are 18 representative fixtures per format, not exhaustive grammar/fuzz claims. The smaller extraction budgets are supported request-protocol values; their distinct profile digest is asserted. They do not alter native CPU/address-space/descriptor limits or replace the independently pinned default host profile. Every fixture is first checked against the current parser reference and then independently sent through the installed qualified worker. Empty Word paragraphs and blank worksheet cells both call the parser's `Add`; the second item at `maximumChunks=1` raises a refusal caught atomically by `Extract`, discarding the first item. The new reference/child assertions require both outcomes/codes and empty chunks, rather than assuming the fixture shape still works.
- Office and PDF caller cancellation and **default** wall deadlines use separately owned, real, pinned runners after default host admission. Only the existing EOF-withholding option changes. Office records the real owned operation and GO/frame-flushed witness after parent admission. Native PDF observes a hardened live child while its operation is pending; its runner has no equivalent writer-completion observer, so this is not proof that PDF parsing itself had begun. Successful ordinary execution follows using a fresh non-withholding runner. This is recovery after interruption, not reuse of the immutable EOF-withholding options.
- The Office interruption additionally checks original/owned I/O task completion, native exit, reap, cancellation, handles, directory removal and the runner's original cleanup deadline. Test `WaitAsync` bounds are diagnostic/teardown bounds, not new product cleanup time allowances. These tests do not assert an unobserved caller-return timestamp met a hard-real-time contract.
- Stop/Dispose cases first admit and stop an unchanged production host. A **second, explicitly controlled host factory** then performs unchanged installation validation and the real qualified Office/PDF factory/probe paths, registers two extra real pinned runners through the existing ownership callback, and holds those extra owners with EOF withheld. Stop or Dispose overlaps their witnessed live operations and must return only after cancellation/settlement; retained qualified and raw references reject new work. No synthetic availability result, portable launcher or fake procfs admission is supplied.
- That controlled host case proves host ownership/drain of real registered runners. It is **not** the default-host/qualified-extraction race. The separate four-case suite below supplies genuine witnessed overlap without exposing private runner options, EOF withholding, reflection, private-field mutation or replacement workers.
- Settlement combines every direct child across `/proc/self/task/*/children` with the mandatory all-visible PID/start-time census above. Reparented survivors and zombies anywhere in that dedicated namespace fail; baseline siblings are preserved. The host/database callback is intentionally fixture-only; reuse the accepted actual PostgreSQL schema/store-admission receipt separately.
- The cleanup-fault fact uses the **actual installed Office parser** behind the exact measured runtime/launcher with EOF withheld. Existing `BeforeCleanupStage` injects a synthetic directory-removal exception after real child/reap/I/O/handle settlement. It requires empty admitted output, `CleanupFailed`, the identical retained owner fault, a retained operation directory, unavailable admission, the same memoized Stop task and repeated Stop/Dispose failure. Separate fixture teardown joins the retained owner, removes only that exact empty joined directory and verifies that removal does not rehabilitate the runner. It does not fake an OS permission denial or claim every cleanup stage/PDF failure branch was injected.

## Genuine encrypted Office fixture provenance

The test file embeds only the **final** base64 data from the retained parent evidence folder `/srv/example-build/data/qualification-run/results/encrypted-office-fixtures/`. No raw fixture files were copied to Temp or the repository. Read-only staging verification decoded both constants in memory and matched:

- DOCX: 10,752 bytes; SHA-256 `6d7544a08970faaf4329ccdd694cc3c49b9ab5323ee8fdbe252d983a8155e992`.
- XLSX: 11,776 bytes; SHA-256 `4212bc2682d54c7f67f6d609c7726a6fa639d3ad70fef9783981b34ea8700902`.

The installed test reasserts length/hash before each call. Expected result is `Unsupported / legacy-or-encrypted-office`, empty chunks and correct source/profile/evidence binding. This is refusal of genuine encrypted input, not decryption or Desktop Office acceptance. Public fixture password is `weft-fixture-only`; it is not a credential.

The parent manifest describes msoffcrypto-tool 6.1.0 ECMA-376 Agile OLE (AES-256-CBC/SHA-512, spinCount 100000), independent AES password-verifier and payload-HMAC decryption recovering byte-identical valid OOXML, fresh msoffcrypto password/integrity verification, and wrong-password/cipher-mutation refusals. The independent implementation shares the cryptography AES backend and olefile reader; it is not an entirely independent cryptographic stack. Final source ZIPs use a standard 4096-byte comment to avoid the pinned writer's tiny-stream failure; XLSX has proper worksheet/sharedStrings MIME overrides. The rejected small DOCX draft is not embedded. Source/provenance/generator/manifest remain in the parent's Btrfs evidence. Both installed format cases passed the genuine encrypted-input refusal. Fabricated OLE magic is not substituted for these fixtures.

## Separate installed shared-Office-profile probe

The repository's source-built `dotnet/tools/Weft.CompiledSandboxProbe` already tests outside-read, private-write, assembly-metadata, socket, fork and descriptor-owner signal denial, environment clearing and sealed privacy. Its current commands are an outside-file path and a synthetic `exit=N` mode. It has **no** CPU-spin, address-space-reservation or stdout-flood command.

The same test file includes a separate fact that launches this existing probe through `ProcessCompiledRuntimeRunner` with **`Operation.OfficeExtraction`**, the installed Office launcher/.NET root/work root and unchanged fixed native runtime arguments. Office operation selects the parent's protocol cap/probe-refusal/identity markers; it does not force a different native managed profile or append an Office command to the hosted assembly. The normal runner still performs native readiness/procfs/resource verification before writing the probe's input. This is not an Office-format request or a qualified parser wrapper, so it deliberately does not call the parser-specific `ProbeAsync` expecting the Office decode-refusal diagnostic.

The known probe was built from reviewed repository source, with its runtimeconfig/deps/required closure in a separate protected tree in the same image revision, then independently pinned and approved. Do not copy an unknown binary, reuse the parser's expected hash, or loosen the native profile to admit it. The fixture revalidates installation policy with the probe as the measured managed tree, checks its exact assembly SHA-256 and the separately approved **Office-operation composite runtime identity**, then checks exact probe output and unchanged metadata. It creates one synthetic marker under the PDF work root, verifies the parent can actually read it, and removes only that exact owned file. The marker must be outside the probe's read grants. An accidentally readable marker makes the test fail. The installed case passed.

Additional settings, lowercase raw 64-character SHA-256 values without `sha256:` prefixes:

```text
WEFT_TEST_DOCUMENT_PROFILE_PROBE_ASSEMBLY=/protected/probe/Weft.CompiledSandboxProbe.dll
WEFT_TEST_DOCUMENT_PROFILE_PROBE_BUILD_SHA256=<independently approved assembly hash>
WEFT_TEST_DOCUMENT_PROFILE_PROBE_RUNTIME_SHA256=<independently approved Office-operation composite identity>
```

Explicit separate selection, using the same prepared-image/no-build command shape:

```text
FullyQualifiedName~Weft.Tests.SpecDocumentInstalledSharedProfileProbeTests
```

This is an **installed shared-profile probe**, not prohibited operations executed inside the exact Office parser binary. It is not PDF-profile denial qualification. Existing native Ferrule/compile-sandbox results remain shared-control evidence and should not be relabeled.

## Separate new resource probe and compositional proof matrix

`dotnet/tools/Weft.InstalledResourceProbe/Weft.InstalledResourceProbe.csproj` and `dotnet/tools/Weft.InstalledResourceProbe/Program.cs` are standalone qualification tooling, not production dependencies. The project pins net10.0 / runtime 10.0.12 with roll-forward disabled and has no package/project references. It was reviewed, built on approved Btrfs, installed read-only in its own image directory and independently approved with separate assembly and Office-operation composite runtime hashes. Both installed resource cases passed. Do not substitute its hash for the Office parser hash.

Additional trusted test settings:

```text
WEFT_TEST_DOCUMENT_RESOURCE_PROBE_ASSEMBLY=/protected/resource-probe/Weft.InstalledResourceProbe.dll
WEFT_TEST_DOCUMENT_RESOURCE_PROBE_BUILD_SHA256=<independently approved raw assembly SHA-256>
WEFT_TEST_DOCUMENT_RESOURCE_PROBE_RUNTIME_SHA256=<independently approved raw Office-operation composite identity>
```

Separate selection:

```text
FullyQualifiedName~Weft.Tests.SpecDocumentInstalledResourceProbeTests
```

Both cases first admit the production Office/PDF host, then revalidate the protected probe tree, its exact assembly pin and its separate composite identity. They use the unchanged real Office-operation runner: fixed native arguments, runtime environment/GC configuration and pre-input marker/UID/capabilities/filter-count/all-resource-limit/hidden-environment admission. Probe self-checks are extra accidental-portable-invocation guards, not an admission bypass.

- `memory`: a native 64 KiB mmap reservation must succeed; a 4 GiB reservation must fail with exact `ENOMEM` under the unchanged 3 GiB address-space ceiling. No large pages are touched. Require exact successful report, original task/reap/handle/directory settlement and no new process identities. This is native virtual-address-space resource evidence, not managed OOM, physical RSS/swap exhaustion or parser memory consumption.
- `overflow`: a closed command emits exactly `SpecOfficeProcessProtocol.MaximumResponseBytes + 1` bytes (10 MiB + 2048 + 1). The test checks its constant matches the production cap, requires `OutputLimitExceeded` and **empty admitted output**, complete real settlement and still-available runner, then runs a successful memory probe through that same runner. No cap is lowered. The configured OS file-size limit remains unchanged and is not the stdout pipe cap.

Scope of the reviewed combined proof:

- **Office parser behavior:** genuine readable/hostile/budget/encrypted input goes through the exact installed parser, independently pinned host/launcher/runtime and source/result binding.
- **Office denied operations:** the existing reviewed managed denial probe runs under the exact shared Office launcher profile with separately pinned protected probe closure and actual pre-input admission. This is valid compositional boundary evidence, not a claim that `Weft.DocumentWorker.dll` attempted a socket. Ordinary OOXML has no required socket/fork command; no impossible parser-specific syscall gate is introduced.
- **Office CPU control:** reuse the accepted compile-sandbox native qualification's real spinning-program CPU termination and production limit-setting algorithm, joined to this installed profile's verified exact soft/hard 30-second CPU values and 30-second parent wall limit. This is shared-control plus installed-admission evidence, not a new observed 30-second CPU termination. The optional probe `cpu` action is present for future diagnostics but is **not selected** by the tests. If later run through the normal runner, `TimedOut` means parent wall timeout; `WorkerFailed` is a generic failed execution/transport result, not proof of SIGXCPU/OOM. A direct 40-second signal/CPU-tick observer is optional stronger attribution, not a new required framework or authorization to weaken limits.
- **PDF controls:** `engines/spec-pdf-worker/src/main.rs` directly includes the reviewed Ferrule sandbox source. Reuse the accepted Rust library and eight native Ferrule scenario-group evidence for denied operations/address-space refusal/CPU termination, joined to the actual installed PDF's pre-input verification of its fixed 10-second CPU/wall, 512 MiB address-space and 64-descriptor profile. Real installed parser fixture and interruption results are separate workload/lifecycle evidence. This does not claim the PDF parser consumed resources to exhaustion or require a malicious replacement PDF executable.
- **Cleanup/lifecycle:** actual installed Office directory-stage fault injection proves quarantine/retained failure; normal/canceled/deadline cases and real-owner host drain prove their stated settlement paths. Existing shared-runner/host failure-injection receipts retain their original scope. Default-host/qualified-extraction overlap remains distinct below.

### Distinctions retained

### Parent stdout cap

Neither `ProcessCompiledRuntimeOptions` nor `ProcessWorkerOptions` currently has an adjustable output-cap option. Their real runners use fixed operation/profile caps. The `Cap` properties in `SpecDocumentWorkerTests.PortableWorker` and `SpecPdfProcessTests.Child.Run` are test-only portable harness controls; they are not installed-runner options. There is no honest existing-option reduced-cap test to add here without a production change, which this task does not authorize.

The canonical-serialization budget fixture above checks parser evidence serialization, **not** transport stdout overflow or an OS resource limit. Existing Office/PDF parsers bound their normal output below their transport ceilings; ordinary parser success cannot demonstrate a hostile output producer crossing that ceiling.

The new separate resource probe supplies the closed flood command under the unchanged Office-operation runner; it does not add an adjustable-cap option or change the existing reviewed denial probe. No PDF replacement is introduced.

### CPU and memory exhaustion

The existing managed denial probe cannot perform these attacks; the new standalone resource probe is separately reviewed/pinned and does not invent commands in the old binary:

- CPU attribution remains as specified in the compositional matrix; optional `cpu` is guarded by exact native profile self-checks and has no parent-timeout-as-CPU-success oracle.
- Native address-space refusal has a concrete new test/source action, preserving the 512 MiB managed heap setting and all native limits. No native RSS requirement is invented.
- The probe's reviewed build/install receipt and independently approved assembly/composite identities are retained with the passing resource cases. The ordinary parser and existing denial-probe identities remain separate.

## Genuine default-host qualified-extraction races

`SpecDocumentInstalledHostShutdownRaceTests` separately tests Office/PDF crossed with Stop/Dispose. Each attempt constructs the unchanged default `SpecDocumentHostResources`, admits its actual qualified workers and submits a captured approximately 7 MiB valid DOCX/PDF. There is no extra runner, EOF withholding, injected factory, caller-cancellation stimulus, reflection or production seam change. Read-only procfs inspection witnesses a live direct child by PID/start-time with the exact fixed controls while the qualified call is pending; Stop/Dispose is dispatched immediately after the final pending sample. A completed extraction that wins the race is not passing overlap evidence; each attempt must settle before another, and no witness across eight bounded attempts fails as inconclusive. Protocol/admission/cleanup failures propagate rather than becoming retries.

The retained TRX records **all four on attempt 1**: Office/Stop `88@1404`, Office/Dispose `121@1561`, PDF/Stop `72@1360`, PDF/Dispose `105@1445`. Each has `pending-at-shutdown=True`, terminal `unavailable` and `witnessed-interrupted-overlap=True`. Qualified continuations and native owners settled, retained references rejected new work, and roots/census were clear. This proves the witnessed pending qualified-call/default-host shutdown overlap, not the exact parser instruction or request-input-consumption phase at the observation instant.

### Other remaining boundaries

Full grammar/fuzz exhaustiveness, Desktop Office interoperability, every individual cleanup/reap/pipe failure branch and native RSS/OOM behavior are not claimed by this representative matrix. They are not silently promoted into new mandatory requirements. Process accounting is bounded to the complete externally verified dedicated PID namespace. A different/final installation still needs its own approved identities and executed results.

## Retained result and conservation

Gate/driver exits were zero, Gate wait/reap/disposal confirmed, and the owned container was removed with before/after daemon identity and absence checks. Office/PDF roots settled empty, UID/GID 1000, mode `0700`. The external namespace record remained hash/inode/single-link/read-only protected; executable admission stayed unchanged. Original source, harness and frozen context manifests were conserved. These are actual compiled/installed execution receipts, not source-only preparation.

The 30 reviewed text evidence files plus unchanged `retained.sha256` were credential/private-payload scanned, exclusively copied and hash-verified before/after under:

The private qualification archive's
`2026-11-12 Weft local qualification/linux-wsl-930ba1f5/installed-hostile-sixteen-case/`
folder.

- Manifest SHA-256: `bd8ed4278ccf5250647ac8ab9ab712bea41ff0a3bc2f4502786e9e4470bface6`.
- Raw TRX SHA-256: `593b047d4a8fb184d57fc4bfe57085d31b8ac7f3c144dec4f10e5060a7fa1bef`.
- `final-review.md` records final review/publication; original receipts retain their historical pre-publication wording.

Prior local evidence remains separate: initial installed four cases passed on `sha256:a9cfc1028e157c3391c58d7036ccfb7bbb771077f5712bf714309a96980c4f6c`; matching-image Docker equivalence passed 4/4 on `sha256:a2829f58ff84a790b365ebad3232ec5c41be53e6647d5b592a1797b59d6ed5b1` and `sha256:c7f3ddeb8abd177c556ac3043c59c3484d6872a8408a82175321a69c2703db3a` after a local directory-permission correction. Both pinned Rust CI prerequisite builds passed locally offline and 15 CI launcher unit tests passed. No hosted CI result is claimed; overlapping selections must not be summed.

Public PDF activation and filesystem spec-store refusal gates remain unchanged. Actual PostgreSQL admission is covered separately by the 124-test host regression receipt, not by these drivers' fixture-only schema callback. AppArmor, production equivalence, live-provider/Azure acceptance and deployment remain outside the closed local qualification scope.
