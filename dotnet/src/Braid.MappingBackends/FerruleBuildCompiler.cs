using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

/// <summary>Actor-bound source reader. No request can select a physical source path or object identity.</summary>
public sealed class BuildNativeSource(ActorContext actor, IBuildAdmissionReader admission, IPrivateObjectStore storage)
{
    public async Task<BuildBytes> ReadAsync(BuildIntent intent, CancellationToken token)
    {
        var access = await admission.ReadAsync(actor, intent, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Build source refused.");
        if (access.Actor != actor.PrincipalId || access.IntentFingerprint != intent.Fingerprint || access.Native.Id.Scope != intent.Revision ||
            access.Native.Id.Class != PrivateObjectClass.NativeArtifact || access.Native.Digest != intent.NativeDigest ||
            access.Native.ByteCount is < 1 or > BuildWorkerProtocol.MaximumNativeBytes) throw new InvalidOperationException("Build source mismatch.");
        var bytes = await storage.ReadVerifiedAsync(intent.Revision, access.Native, token).ConfigureAwait(false);
        if (bytes.LongLength != access.Native.ByteCount || Sha256Digest.FromBytes(bytes) != intent.NativeDigest) throw new InvalidOperationException("Build source mismatch.");
        return new(bytes, BuildWorkerProtocol.MaximumNativeBytes);
    }
}

/// <summary>Reads exact native CSV boundary metadata before an intent exists. It grants no approval or execution authority.</summary>
public sealed class FerruleCsvBuildBoundaryReader : ICsvBuildBoundaryReader
{
    private readonly IInspectionWorkerRunner runner;
    private readonly IRevisionReportMetadataReader sources;
    private readonly IProjectReader projects;
    private readonly IPrivateObjectStore storage;
    private readonly BuildBackendIdentity backend;

    private readonly string inspectionProfile;
    public FerruleCsvBuildBoundaryReader(IInspectionWorkerRunner runner, IRevisionReportMetadataReader sources,
        IProjectReader projects, IPrivateObjectStore storage, bool xml = false)
    {
        if (runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Build)
            throw new ArgumentException("CSV metadata inspection requires the compiler profile.", nameof(runner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        inspectionProfile = xml ? BuildWorkerProtocol.XmlProfile : BuildWorkerProtocol.CsvProfile;
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        var identity = runner is ICompilationIdentitySource source ? source.CompilationBuildIdentity : runner.BuildIdentity;
        backend = new("ferrule", BuildWorkerProtocol.Engine, InspectionTransport.RequireImage(identity!), xml ? FerrulePreviewBackend.XmlAdapterBuild : FerrulePreviewBackend.CsvAdapterBuild);
    }

    public async Task<CsvBuildBoundaryObservation?> ReadAsync(ActorContext actor, RevisionReportSource source, CancellationToken token = default)
    {
        if (!await Current().ConfigureAwait(false)) return null;
        var native = await storage.ReadVerifiedAsync(source.Scope, source.NativeObject, token).ConfigureAwait(false);
        if (native.Length is < 1 or > BuildWorkerProtocol.MaximumNativeBytes || native.LongLength != source.NativeObject.ByteCount ||
            Sha256Digest.FromBytes(native) != source.NativeObject.Digest)
            throw new InvalidOperationException("CSV native metadata source mismatch.");
        if (!await Current().ConfigureAwait(false)) return null;
        // No build intent exists yet. The native digest binds this read-only inspection's correlation field.
        var request = new BuildWorkerProtocol.Request(BuildWorkerProtocol.Protocol, Guid.NewGuid().ToString("D"), source.Scope,
            source.NativeObject.Digest.Value, source.NativeObject.Digest.Value, "inspect", inspectionProfile);
        var process = await runner.RunAsync(BuildWorkerProtocol.EncodeRequest(request, native), token).ConfigureAwait(false);
        if (process.Failure != WorkerProcessFailure.None) return null;
        var reply = BuildWorkerProtocol.DecodeReply(process.Output, request);
        if (!await Current().ConfigureAwait(false) || reply.Header.Status != "supported") return null;
        if (reply.Header.Files.Length != 0) throw new InvalidOperationException("CSV metadata inspection returned unexpected files.");
        return new(source.Scope, source.NativeObject.Digest, backend, reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary).ToArray());

        async Task<bool> Current()
        {
            var project = new ProjectScope(source.Scope.OrganizationId, source.Scope.ProjectId);
            var access = await projects.GetAccessAsync(actor, project, token).ConfigureAwait(false);
            if (access is null || access.Scope != project || access.PrincipalId != actor.PrincipalId || !access.Can(ProjectAction.UploadRevision)) return false;
            var current = await sources.GetSourceAsync(actor, source.Scope, token).ConfigureAwait(false);
            return current is not null && current.Actor == actor.PrincipalId && current.Source == source && source.SourceBackendId == backend.BackendId;
        }
    }
}

/// <summary>Actual pinned Ferrule emission and .NET compilation through a fixed isolated worker transport.</summary>
public sealed class FerruleBuildCompiler : IBuildCompiler
{
    public const string Adapter = FerrulePreviewBackend.AdapterBuild;
    public static BuildRuntimeProfile Profile { get; } = new(BuildWorkerProtocol.Profile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    public static BuildRuntimeProfile DocumentProfile { get; } = new(BuildWorkerProtocol.DocumentProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    public static BuildRuntimeProfile X12InterchangesProfile { get; } = new(BuildWorkerProtocol.X12InterchangesProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    public static BuildRuntimeProfile CsvProfile { get; } = new(BuildWorkerProtocol.CsvProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    public static BuildRuntimeProfile XmlProfile { get; } = new(BuildWorkerProtocol.XmlProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    private readonly IInspectionWorkerRunner runner;
    private readonly BuildNativeSource sources;
    private readonly bool xmlEnabled;
    private readonly HashSet<NativeDocumentFormat> enabledFormats;
    public static BuildRuntimeProfile NativeProfile(string id) => BuildWorkerProtocol.NativeFormat(id) is null ? throw new ArgumentException("Unknown byte profile.", nameof(id)) : new(id, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    public FerruleBuildCompiler(IInspectionWorkerRunner runner, BuildNativeSource sources, bool xmlEnabled = false, IReadOnlyList<NativeDocumentFormat>? nativeFormats = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(sources);
        if (runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Build)
            throw new ArgumentException("Build requires the fixed compiler worker profile.", nameof(runner));
        this.runner = runner; this.sources = sources;
        this.xmlEnabled = xmlEnabled;
        enabledFormats = new(nativeFormats ?? []);
        if (!enabledFormats.IsSubsetOf(NativeDocumentFormat.CatalogFormats)) throw new ArgumentException("Unsupported native byte format.", nameof(nativeFormats));
        _ = InspectionTransport.RequireImage(CompilationIdentity);
    }
    private string CompilationIdentity => runner is ICompilationIdentitySource source
        ? source.CompilationBuildIdentity ?? throw new ArgumentException("The selected runner does not serve compilation.")
        : runner.BuildIdentity;
    public BuildBackendIdentity Backend => new("ferrule", BuildWorkerProtocol.Engine, CompilationIdentity, Adapter);
    public BuildBackendIdentity DocumentBackend => new("ferrule", BuildWorkerProtocol.Engine, CompilationIdentity, FerrulePreviewBackend.DocumentAdapterBuild);
    public BuildBackendIdentity CsvBackend => new("ferrule", BuildWorkerProtocol.Engine, CompilationIdentity, FerrulePreviewBackend.CsvAdapterBuild);
    public BuildBackendIdentity XmlBackend => new("ferrule", BuildWorkerProtocol.Engine, CompilationIdentity, FerrulePreviewBackend.XmlAdapterBuild);
    public BuildBackendIdentity NativeBackend(NativeDocumentFormat format) => new("ferrule", BuildWorkerProtocol.Engine, CompilationIdentity, format.AdapterBuild);
    private bool Admits(BuildIntent intent) => (intent.Profile == Profile && intent.Backend == Backend ||
        (intent.Profile == DocumentProfile || intent.Profile == X12InterchangesProfile) && intent.Backend == DocumentBackend ||
        intent.Profile == CsvProfile && intent.Backend == CsvBackend && BuildWorkerProtocol.AdmitsCsvPlan(intent.Boundaries) ||
        xmlEnabled && intent.Profile == XmlProfile && intent.Backend == XmlBackend || BuildWorkerProtocol.NativeFormat(intent.Profile.Id) is { } native && enabledFormats.Contains(native) && intent.Profile == NativeProfile(intent.Profile.Id) && intent.Backend == NativeBackend(native)) &&
        intent.Boundaries.All(boundary => BuildWorkerProtocol.AdmitsBoundary(intent.Profile.Id, boundary.Format, boundary.CsvDialect));

    public async Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var refusal = BuildPreflightService.DocumentRefusal(intent);
        if (!xmlEnabled && intent.Profile == XmlProfile) refusal = BuildPreflightReason.UnsupportedXmlBuild;
        var nativeFormat = BuildWorkerProtocol.NativeFormat(intent.Profile.Id);
        if (nativeFormat is not null && !enabledFormats.Contains(nativeFormat)) refusal = intent.Profile.Id switch { BuildWorkerProtocol.FlexProfile => BuildPreflightReason.UnsupportedFlexTextBuild, BuildWorkerProtocol.ProtoProfile => BuildPreflightReason.UnsupportedProtobufBuild, _ => BuildPreflightReason.UnsupportedXbrlBuild };
        if (refusal != BuildPreflightReason.None)
            return new(intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries,
                CapabilityStatus.Unsupported, refusalReason: refusal);
        var backend = nativeFormat is not null ? NativeBackend(nativeFormat) : intent.Profile.Id == BuildWorkerProtocol.XmlProfile ? XmlBackend : intent.Profile.Id == BuildWorkerProtocol.CsvProfile ? CsvBackend : BuildWorkerProtocol.IsDocumentProfile(intent.Profile.Id) ? DocumentBackend : Backend;
        var profile = intent.Profile;
        if (!Admits(intent))
            return intent.Profile.Id == BuildWorkerProtocol.CsvProfile
                ? new(intent.Revision, intent.NativeDigest, backend, profile, intent.Boundaries, CapabilityStatus.Unsupported, refusalReason: BuildPreflightReason.UnsupportedCsvPlan)
                : new(intent.Revision, intent.NativeDigest, Backend, Profile, intent.Boundaries, CapabilityStatus.Unsupported);
        var native = await sources.ReadAsync(intent, token).ConfigureAwait(false);
        var reply = await RunAsync(intent, native, "inspect", token).ConfigureAwait(false);
        if (reply.Header.Files.Length != 0) throw new InvalidOperationException("Build inspection returned unexpected bytes.");
        return new(intent.Revision, intent.NativeDigest, backend, profile,
            reply.Header.Status == "supported" ? reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary).ToArray() : intent.Boundaries,
            reply.Header.Status == "supported" ? CapabilityStatus.Supported : CapabilityStatus.Unsupported,
            refusalReason: reply.Header.Status switch
            {
                "unsupportedCsvPlan" => BuildPreflightReason.UnsupportedCsvPlan,
                "unsupportedXmlBuild" => BuildPreflightReason.UnsupportedXmlBuild,
                "unsupportedFlexTextBuild" => BuildPreflightReason.UnsupportedFlexTextBuild,
                "unsupportedXlsxBuild" => BuildPreflightReason.UnsupportedXlsxBuild,
                "unsupportedProtobufBuild" => BuildPreflightReason.UnsupportedProtobufBuild,
                "unsupportedPdfBuild" => BuildPreflightReason.UnsupportedPdfBuild,
                "statefulDatabaseBuild" => BuildPreflightReason.StatefulDatabaseBuild,
                "unsupportedXbrlBuild" => BuildPreflightReason.UnsupportedXbrlBuild,
                _ => BuildPreflightReason.None
            });
    }

    public async Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default)
    {
        if (!Admits(intent) || native.Digest != intent.NativeDigest) throw new BuildContractException(BuildContractFailure.InvalidInput);
        var reply = await RunAsync(intent, native, "compile", token).ConfigureAwait(false);
        if (reply.Header.Status != "supported") throw new BuildContractException(BuildContractFailure.CapabilityUnavailable);
        var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile, reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary).ToArray(), CapabilityStatus.Supported);
        if (new BuildPreflightService().Evaluate(intent, capability, token).Outcome != BuildPreflightOutcome.Supported ||
            reply.Header.Files.Length != 1 || reply.Header.Files[0].Path != "Ferrule.Generated.dll") throw new InvalidOperationException("Build output identity mismatch.");
        var bytes = reply.Contents[0];
        // PE signature only establishes assembly shape here; execution remains owned by the isolated runtime target.
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new InvalidOperationException("Build output is not a managed assembly.");
        token.ThrowIfCancellationRequested();
        return new(bytes, intent.Profile.MaximumArtifactBytes);
    }

    private async Task<BuildWorkerProtocol.Reply> RunAsync(BuildIntent intent, BuildBytes native, string operation, CancellationToken token)
    {
        var header = BuildWorkerProtocol.For(intent, operation);
        var response = await runner.RunAsync(BuildWorkerProtocol.EncodeRequest(header, native.Copy()), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response.Failure != WorkerProcessFailure.None) throw new InvalidOperationException($"Compiler worker unavailable: {response.Failure}/{response.AdmissionFailure}.");
        return BuildWorkerProtocol.DecodeReply(response.Output, header);
    }
}
