using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

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

/// <summary>Actual pinned Ferrule emission and .NET compilation through a fixed isolated worker transport.</summary>
public sealed class FerruleBuildCompiler : IBuildCompiler
{
    public const string Adapter = FerrulePreviewBackend.AdapterBuild;
    public static BuildRuntimeProfile Profile { get; } = new(BuildWorkerProtocol.Profile, 1, "dotnet", "10.0.112", "weft.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes);
    private readonly IInspectionWorkerRunner runner;
    private readonly BuildNativeSource sources;
    public FerruleBuildCompiler(IInspectionWorkerRunner runner, BuildNativeSource sources)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(sources);
        if (runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Build)
            throw new ArgumentException("Build requires the fixed compiler worker profile.", nameof(runner));
        if (runner.BuildIdentity.Length != 71 || !runner.BuildIdentity.StartsWith("sha256:", StringComparison.Ordinal) ||
            runner.BuildIdentity[7..].Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new ArgumentException("An immutable build image is required.");
        this.runner = runner; this.sources = sources;
    }
    public BuildBackendIdentity Backend => new("ferrule", BuildWorkerProtocol.Engine, runner.BuildIdentity, Adapter);

    public async Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default)
    {
        if (intent.Backend != Backend || intent.Profile != Profile || intent.Boundaries.Any(boundary => boundary.Format != "json"))
            return new(intent.Revision, intent.NativeDigest, Backend, Profile, intent.Boundaries, CapabilityStatus.Unsupported);
        var native = await sources.ReadAsync(intent, token).ConfigureAwait(false);
        var reply = await RunAsync(intent, native, "inspect", token).ConfigureAwait(false);
        if (reply.Header.Files.Length != 0) throw new InvalidOperationException("Build inspection returned unexpected bytes.");
        return new(intent.Revision, intent.NativeDigest, Backend, Profile,
            reply.Header.Status == "supported" ? reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary).ToArray() : intent.Boundaries,
            reply.Header.Status == "supported" ? CapabilityStatus.Supported : CapabilityStatus.Unsupported);
    }

    public async Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default)
    {
        if (intent.Backend != Backend || intent.Profile != Profile || native.Digest != intent.NativeDigest) throw new BuildContractException(BuildContractFailure.InvalidInput);
        var reply = await RunAsync(intent, native, "compile", token).ConfigureAwait(false);
        if (reply.Header.Status != "supported") throw new BuildContractException(BuildContractFailure.CapabilityUnavailable);
        var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, Backend, Profile, reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary).ToArray(), CapabilityStatus.Supported);
        if (new BuildPreflightService().Evaluate(intent, capability, token).Outcome != BuildPreflightOutcome.Supported ||
            reply.Header.Files.Length != 1 || reply.Header.Files[0].Path != "Ferrule.Generated.dll") throw new InvalidOperationException("Build output identity mismatch.");
        var bytes = reply.Contents[0];
        // PE signature only establishes assembly shape here; execution remains owned by the isolated runtime target.
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new InvalidOperationException("Build output is not a managed assembly.");
        token.ThrowIfCancellationRequested();
        return new(bytes, Profile.MaximumArtifactBytes);
    }

    private async Task<BuildWorkerProtocol.Reply> RunAsync(BuildIntent intent, BuildBytes native, string operation, CancellationToken token)
    {
        var header = BuildWorkerProtocol.For(intent, operation);
        var response = await runner.RunAsync(BuildWorkerProtocol.EncodeRequest(header, native.Copy()), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response.Failure != WorkerProcessFailure.None) throw new InvalidOperationException("Compiler worker unavailable.");
        return BuildWorkerProtocol.DecodeReply(response.Output, header);
    }
}
