# Document worker host

## Startup contract

With spec intake enabled, `WEFT_SPEC_DOCUMENT_WORKERS` accepts `disabled` (default) or `optional`. Disabled startup reads no worker settings and opens no workers or installation paths. With intake disabled, worker configuration is ignored. Unknown modes or malformed/missing optional-mode settings fail configuration validation.

Optional mode requests real startup. Unsupported platforms, rejected installations, failed probes and the 45-second startup deadline leave legacy intake available with no structural formats. Host cancellation propagates. Startup is attempted once; a corrected installation requires restart. The host admits Office and PDF together, owns partial-startup cleanup and both workers' disposal, and revokes bound admissions on shutdown.

Unconfirmed cleanup is a fatal startup/shutdown error, not optional unavailability. Each runner is registered with the host before its first probe. Cleanup attempts every retained owner, removes only successfully joined owners and retains failed owners for later `StopAsync`/`DisposeAsync` calls. The runners' memoized terminal cleanup faults therefore remain visible on repeated shutdown.

Installation and native-runner constructor directory probes also protect the interval before runner registration. Their shared probe helper wraps deletion failure in a distinct terminal cleanup fault; host startup retains that fault and every later stop/dispose surfaces it. This intentionally does not report a clean stop or retry uncertain filesystem deletion. An unconfirmed probe directory requires operational reconciliation before restarting. Creation failure also attempts cleanup and becomes the same terminal fault if cleanup cannot be confirmed.

The production path is Linux x64 only. It checks an unprivileged UID with no effective capabilities, read-only installation/runtime and loader paths, absence of writable overmounts or backing-filesystem aliases, and installation trees without symlinks or special files. Linux `statx` must return type/mode/owner facts. Work roots must exist, be distinct and nonoverlapping, have mode `0700`, belong to the host UID and reside on writable mounts beneath read-only parents. Installation checks describe the host's mount namespace; the deployment must preserve that namespace and prevent privileged external mutation for the entire host lifetime.

Any writable mount on the same backing filesystem as a protected executable/runtime/loader path is refused, even when its backing root is disjoint. Hardlinks can alias inodes across those disjoint roots. Private work mounts must consequently use a different backing filesystem from the protected installation.

The existing qualified Office and native PDF factories then verify build/profile identities and run their real resource/sandbox/procfs probes. Configuration supplies expected identities, never a qualification boolean. The public PDF convenience factory remains closed. The internal worker delegate and schema callback used by tests are trusted fixture seams only.

Each document capability/execution admission checks current worker liveness and the actor-bound PostgreSQL session. That session validates actual migration 46 and its schema contract plus the active matching store binding in a transaction. `SpecStoreBinding` is registered from the existing Blob endpoint/container digest. Creation discovery can resolve it and the keyed `PostgresSpecBundleDatabase` reader without opening physical storage. Creation review/list admission remains owned by its separate host composition.

## Settings

All settings below have prefix `WEFT_SPEC_DOCUMENT_` and are required in optional mode:

- `OFFICE_ASSEMBLY`: absolute Unix path to the published `Weft.DocumentWorker.dll`.
- `OFFICE_BUILD_SHA256`: approved Office assembly SHA-256.
- `OFFICE_RUNTIME_SHA256`: approved composite `ProcessCompiledRuntimeRunner` runtime identity (Office operation, worker tree, .NET runtime/host, launcher, runner assembly, interpreter and resolved native-library closure).
- `OFFICE_PROFILE_SHA256`: independently approved `SpecOfficeExtractionLimits.Digest`.
- `OFFICE_LAUNCHER`: absolute Unix path to `compile-sandbox`.
- `DOTNET_ROOT`: absolute Unix path to the installed .NET runtime tree.
- `OFFICE_WORK_ROOT`: absolute Unix path to the private Office work mount.
- `PDF_EXECUTABLE`: absolute Unix path to `weft-spec-pdf-worker`.
- `PDF_WORK_ROOT`: absolute Unix path to the private PDF work mount.
- `PDF_BUILD_SHA256`: approved native executable SHA-256.
- `PDF_PROFILE_SHA256`: independently approved `SpecPdfExtractionProfile` digest for that build.

Digests are raw lowercase SHA-256, without a `sha256:` prefix. Profile mismatches fail configuration validation. Paths cannot contain traversal, repeated separators, backslashes or control characters. Example installation layout: `/opt/documents/office`, `/opt/documents/launcher`, `/opt/documents/pdf`, `/opt/dotnet`; work mounts `/work/office` and `/work/pdf`. Approve pins independently of startup rather than calculating expected hashes from whatever executable happens to be installed.

## Source checks and Linux qualification

Windows portable tests cover disabled/default behavior, configuration pins/paths, worker lifetime, cancellation and mount policy. The actual PostgreSQL test verifies schema 46, inactive-store refusal, reactivation and migration-checksum refusal using an isolated local cluster. Set `WEFT_POSTGRES_BIN` to the local PostgreSQL bin directory and `DOTNET_HOST_PATH` to the pinned .NET 10 SDK executable:

```powershell
& $env:DOTNET_HOST_PATH test dotnet/tests/Weft.Tests/Weft.Tests.csproj --filter 'FullyQualifiedName~SpecDocumentHostTests|FullyQualifiedName~SpecDocumentHostPostgresTests' -m:1 -p:OpenApiGenerateDocuments=false
```

Linux source build commands (run on a Linux build machine; not executed by this Windows unit):

```sh
dotnet publish dotnet/apps/Weft.DocumentWorker/Weft.DocumentWorker.csproj -c Release -r linux-x64 --self-contained false -p:OpenApiGenerateDocuments=false
dotnet build dotnet/apps/Weft.Server/Weft.Server.csproj -c Release -p:OpenApiGenerateDocuments=false
cargo build --locked --release --manifest-path engines/spec-pdf-worker/Cargo.toml
```

Use the existing sandbox launcher's reviewed build/install procedure. Building artifacts is source verification, not installation admission. In an independently prepared Linux installation, with all approved settings above and empty dedicated work roots, execute the opt-in real startup gate:

```sh
WEFT_SPEC_DOCUMENT_WORKERS=optional WEFT_TEST_DOCUMENT_HOST_LINUX=1 \
  dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj \
  --filter FullyQualifiedName~SpecDocumentHostLinuxTests -m:1 -p:OpenApiGenerateDocuments=false
```

This gate exercises real installation validation, both workers' startup probes, admission revocation and empty work roots after shutdown. It uses a fixture schema callback; run the actual PostgreSQL host test separately. Without explicit opt-in it reports a skip; with opt-in on the wrong platform or an unqualified installation it fails.

The same Linux test class also includes a real hardlink regression: it creates disjoint directories, mutates a file through its hardlink and verifies refusal using synthetic bind-mount records. This test runs automatically on Linux without mount privileges. It does not claim to exercise actual read-only bind mounts.

Actual Linux execution remains pending. Deployment qualification must additionally exercise readable/malformed/encrypted/image-only documents, resource exhaustion, filesystem/network/process denial, cancellation, cleanup and shutdown under the final deployment restrictions. Portable tests and source compilation do not establish those results.

### Recorded source verification

On Windows with .NET 10.0.112 and isolated PostgreSQL 16.15: the host/intake/Office/PDF regression selection passed 218 tests, with eight skips (seven existing Linux HTTP gates and the new opt-in installation gate). After adding strict lowercase-pin validation and native-entrypoint failure coverage, the host selection passed 19 tests with the one Linux installation skip. Actual schema-46 validation ran successfully in both selections. Linux build and startup commands above have not been executed here.

After independent-review fixes for disjoint-root hardlink aliases and retained cleanup ownership: focused host tests passed **24**, with **two Linux skips**; the full host/intake/Office/PDF regression selection passed **227**, with **nine skips** (seven existing Linux HTTP gates plus installation and hardlink tests). Cleanup regressions cover pre-pair factory failure, unavailable and admitted worker disposal failures, repeated stop/dispose faults, and propagation of unknown startup failures. These runs include actual PostgreSQL schema-46 checks. Actual Linux hardlink and installation execution remain pending.

After the pre-registration filesystem-probe fix: focused host tests passed **30**, with **two Linux skips**; regression selection passed **233**, with **nine skips**. Both production probe routines are exercised with actual directory creation and nonempty-directory deletion failure, injected access-denied deletion, retained identical faults across startup/stop/dispose, and successful directory cleanup. These portable tests invoke the constructor's probe routine directly; they do not claim Linux constructor/process qualification.
