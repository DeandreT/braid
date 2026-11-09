using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Deployment.Sandbox;

/// <summary>One sample document: the unnamed primary input, a named input or an output, with its exact bytes.</summary>
public sealed record SandboxDocument(string? Name, string? MediaType, byte[] Content)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));
    public override string ToString() => nameof(SandboxDocument);
}

/// <summary>A resolved sample: what one sandbox message sends. Source is a display label, never a path or credential.</summary>
public sealed record SandboxSample(PreviewInputDocument Primary, IReadOnlyList<PreviewInputDocument> NamedInputs,
    IReadOnlyList<PreviewParameter> Parameters, string Source)
{
    public override string ToString() => nameof(SandboxSample);
}

public sealed record SandboxSampleDescriptor(string Kind, string Reference, string Name);

/// <summary>Where sample inputs come from. Regression cases are the first source; a traffic simulator can add another.</summary>
public interface ISandboxSampleSource
{
    string Kind { get; }
    Task<IReadOnlyList<SandboxSampleDescriptor>> ListAsync(ActorContext actor, MappingScope mapping, CancellationToken token = default);
    Task<SandboxSample?> ReadAsync(ActorContext actor, MappingScope mapping, string reference, CancellationToken token = default);
}

/// <summary>One sent message and its result. Status and outcome use the mapping endpoint's names.</summary>
public sealed record SandboxMessage(Guid MessageId, Guid OrganizationId, Guid ProjectId, Guid MappingId, Guid TargetId,
    DateTimeOffset SentAt, long DurationMs, string Status, string? Outcome, bool Replayed, Guid? ReleaseId, Guid? BuildId,
    string Source, Guid? ReplayOf, IReadOnlyList<SandboxDocument> Inputs, IReadOnlyList<PreviewParameter> Parameters,
    IReadOnlyList<SandboxDocument> Outputs)
{
    /// <summary>Recorded outcomes never run again; the others may be retried under the same message ID.</summary>
    public bool Final => Status is "completed" or "failed" or "expired";
    public override string ToString() => nameof(SandboxMessage);
}

/// <summary>Receives every finished message. The history is one sink; delivery destinations can be others.</summary>
public interface ISandboxSink
{
    Task DeliverAsync(SandboxMessage message, CancellationToken token = default);
}

/// <summary>Bounded retained messages per target, newest first.</summary>
public interface ISandboxHistory : ISandboxSink
{
    Task<IReadOnlyList<SandboxMessage>> ListAsync(Guid targetId, int limit, CancellationToken token = default);
    Task<SandboxMessage?> ReadAsync(Guid targetId, Guid messageId, CancellationToken token = default);
}

public sealed record SandboxTarget(Guid TargetId, Guid EnvironmentId, long EnvironmentVersion, bool Enabled, Guid? ActiveReleaseId,
    long Version, string RuntimeImage);
public sealed record SandboxBuild(Guid BuildId, Guid RevisionId, Guid EnvironmentId, long EnvironmentVersion, DateTimeOffset CreatedAt);
public sealed record SandboxState(IReadOnlyList<SandboxTarget> Targets, IReadOnlyList<SandboxBuild> Builds, IReadOnlyList<SandboxSampleDescriptor> Samples);
public sealed record SandboxDeployResult(string Outcome, SandboxTarget? Target = null, Guid? ReleaseId = null, Guid? QualificationId = null);

/// <summary>
/// The identities a local sandbox derives for itself: its target per mapping, environment, profile and runtime, the
/// machine principal it invokes as, and that principal's grant. Deterministic, so a restart or retry reuses them.
/// </summary>
public static class SandboxIdentity
{
    public static Guid Derive(string purpose, params object[] parts)
    {
        var text = string.Join('\n', parts.Prepend(purpose).Select(part => Convert.ToString(part, CultureInfo.InvariantCulture)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16];
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x80);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static Guid TargetId(MappingScope mapping, BuildEnvironmentReference environment, string profileId, long profileVersion, string runtimeImage) =>
        Derive("weft.local-sandbox.target.v1", mapping.OrganizationId.ToString("D"), mapping.ProjectId.ToString("D"), mapping.MappingId.ToString("D"),
            environment.EnvironmentId.ToString("D"), environment.Version, profileId, profileVersion, runtimeImage);

    public static MachineCaller Caller(Guid organizationId, DateTimeOffset now) =>
        new(new(MachinePrincipal.Entra, Derive("weft.local-sandbox.tenant.v1", organizationId.ToString("D")),
            Derive("weft.local-sandbox.caller.v1", organizationId.ToString("D"))),
            Derive("weft.local-sandbox.application.v1", organizationId.ToString("D")), now.AddMinutes(5));

    public static Guid GrantId(Guid targetId) => Derive("weft.local-sandbox.grant.v1", targetId.ToString("D"));
}

/// <summary>
/// The local deployment sandbox. Deployment qualifies a completed build, registers or reuses the sandbox target, grants
/// the sandbox caller and activates through the release operations flow, so every authority, qualification and audit
/// check applies. Messages run through the machine invocation ledger and the active-release invocation path.
/// </summary>
public sealed class LocalSandboxService(
    IReleaseOperationsStore operationsReader, ReleaseOperationsService operations, ILocalReleaseStore releaseWriter, ILocalReleaseStore releaseReader,
    IInvocationGrantStore grants, IBuildMetadataReader buildMetadata, Func<ActorContext, BuildQualificationService> qualification,
    MachineInvocationService invocations, IMachineInvocationLedger ledger, ISandboxHistory history, IReadOnlyList<ISandboxSampleSource> sources,
    IReadOnlyList<ISandboxSink> sinks, string runtimeImage, TimeProvider time)
{
    public const int MaximumHistoryPage = 50;
    /// <summary>Each call's bound, as for the mapping endpoint.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    public async Task<SandboxState> ReadStateAsync(ActorContext actor, MappingScope mapping, CancellationToken token = default)
    {
        var targets = await TargetsAsync(actor, mapping, null, token);
        var page = await buildMetadata.ListRunsAsync(actor, mapping, 20, null, token);
        var succeeded = (page?.Runs ?? []).Where(run => run.State == BuildRunState.Succeeded && run.Artifact is not null)
            .Select(run => new SandboxBuild(run.Intent.BuildId, run.Intent.Revision.RevisionId, run.Intent.Environment.EnvironmentId,
                run.Intent.Environment.Version, run.CreatedAt)).ToArray();
        var samples = new List<SandboxSampleDescriptor>();
        foreach (var source in sources) samples.AddRange(await source.ListAsync(actor, mapping, token));
        return new(targets, succeeded, samples);
    }

    public async Task<SandboxDeployResult> DeployAsync(ActorContext actor, MappingScope mapping, Guid deployId, Guid buildId, CancellationToken token = default)
    {
        if (deployId == Guid.Empty || buildId == Guid.Empty) throw new ArgumentException("Deployment and build identifiers are required.");
        // Qualification reads the actor's own completed build in this mapping and replays its saved evidence.
        var qualificationId = SandboxIdentity.Derive("weft.local-sandbox.qualification.v1", deployId.ToString("D"));
        var qualified = await qualification(actor).PutAsync(actor, mapping, qualificationId, buildId, token);
        if (qualified.Outcome is not (BuildQualificationRequestOutcome.Created or BuildQualificationRequestOutcome.Replayed) || qualified.Qualification is null)
            return new(qualified.Outcome switch
            {
                BuildQualificationRequestOutcome.NotQualified => "notQualified",
                BuildQualificationRequestOutcome.Unavailable => "unavailable",
                BuildQualificationRequestOutcome.Conflict => "conflict",
                _ => "refused"
            });
        var intent = qualified.Qualification.Artifact.Intent;
        var reference = new ReleaseTargetReference(mapping, intent.Environment,
            SandboxIdentity.TargetId(mapping, intent.Environment, intent.Profile.Id, intent.Profile.Version, runtimeImage));
        var registered = await releaseWriter.RegisterAsync(actor, new LocalReleaseTarget(reference, intent.Profile, runtimeImage), token);
        if (registered is not (ReleaseWriteOutcome.Created or ReleaseWriteOutcome.Replayed)) return new(registered == ReleaseWriteOutcome.Conflict ? "conflict" : "refused");
        var target = (await TargetsAsync(actor, mapping, reference.TargetId, token)).SingleOrDefault();
        if (target is null) return new("refused");
        if (!target.Enabled) return new("targetDisabled", target);
        var grant = await grants.GrantAsync(actor, new InvocationGrant(SandboxIdentity.GrantId(reference.TargetId), reference,
            SandboxIdentity.Caller(mapping.OrganizationId, time.GetUtcNow()).Principal), token);
        if (grant is not (InvocationGrantOutcome.Applied or InvocationGrantOutcome.Replayed)) return new("refused", target);
        var releaseId = SandboxIdentity.Derive("weft.local-sandbox.release.v1", deployId.ToString("D"));
        // A retry reuses the original operation's reviewed version, so recovery replays instead of conflicting.
        var version = await OriginalVersionAsync(actor, mapping, reference.TargetId, releaseId, token) ?? target.Version;
        var outcome = await operations.OperateAsync(actor, reference, new ReleaseOperation(releaseId, "activate", version, qualificationId), token);
        target = (await TargetsAsync(actor, mapping, reference.TargetId, token)).SingleOrDefault() ?? target;
        return new(outcome switch
        {
            ReleaseTransitionOutcome.Activated => "activated",
            ReleaseTransitionOutcome.Replayed => "replayed",
            ReleaseTransitionOutcome.Conflict => "stale",
            ReleaseTransitionOutcome.IdempotencyConflict => "conflict",
            _ => "refused"
        }, target, releaseId, qualificationId);
    }

    public async Task<SandboxSample?> ResolveSampleAsync(ActorContext actor, MappingScope mapping, string kind, string reference, CancellationToken token = default)
    {
        var source = sources.SingleOrDefault(value => value.Kind == kind);
        return source is null ? null : await source.ReadAsync(actor, mapping, reference, token);
    }

    /// <summary>The stored inputs of an earlier message on the same target, to send again to the current release.</summary>
    public async Task<SandboxSample?> ReplaySampleAsync(ActorContext actor, MappingScope mapping, Guid targetId, Guid messageId, CancellationToken token = default)
    {
        if (await ReadAsync(actor, mapping, targetId, messageId, token) is not { } original) return null;
        var primary = original.Inputs.Single(input => input.Name is null);
        return new(new(null, primary.Content), original.Inputs.Where(input => input.Name is not null).Select(input => new PreviewInputDocument(input.Name, input.Content)).ToArray(),
            original.Parameters, "replay of " + messageId.ToString("D"));
    }

    /// <summary>Null when the target is not this mapping's sandbox target or the actor cannot read it.</summary>
    public async Task<SandboxMessage?> SendAsync(ActorContext actor, MappingScope mapping, Guid targetId, Guid messageId, SandboxSample sample,
        Guid? replayOf = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (messageId == Guid.Empty) throw new ArgumentException("A message identifier is required.");
        if (await ResolveAsync(actor, mapping, targetId, token) is not { } resolved) return null;
        var (reference, target) = resolved;
        var previous = await history.ReadAsync(targetId, messageId, token);
        if (previous is { Final: true }) return previous;
        // The current release's declared boundaries supply document media; the sandbox never guesses from content.
        var release = target.ActiveReleaseId is { } active ? await releaseReader.ReadAsync(actor, reference, active, token) : null;
        var boundaries = release?.Intent.Qualification.Artifact.Intent;
        var documents = boundaries is not null && BuildWorkerProtocol.IsDocumentProfile(boundaries.Profile.Id);
        string? Media(string? name) => documents ? boundaries!.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryInput or BuildBoundaryKind.NamedInput && value.Name == name)
            .Select(value => BuildWorkerProtocol.MediaType(value.Format)).FirstOrDefault() : null;
        var inputs = sample.NamedInputs.Prepend(sample.Primary).Select(input => new SandboxDocument(input.Name, Media(input.Name), input.CopyBytes())).ToArray();
        var started = time.GetUtcNow();
        var clock = Stopwatch.StartNew();
        MachineInvocationResult result;
        if (documents && inputs.Any(input => input.MediaType is null)) result = new(MachineInvocationStatus.Failed, Outcome: MachineInvocationOutcome.InvalidInput);
        else
        {
            var request = new MachineInvocationRequest(sample.Primary, sample.NamedInputs, sample.Parameters,
                documents ? inputs.Select(input => new MachineInputMedia(input.Name, input.MediaType!)).ToArray() : null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Deadline);
            try { result = await invocations.InvokeAsync(reference, SandboxIdentity.Caller(mapping.OrganizationId, started), messageId, request, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { result = new(MachineInvocationStatus.Unavailable, Outcome: MachineInvocationOutcome.Unavailable); }
        }
        clock.Stop();
        var outputs = new List<SandboxDocument>();
        Guid? buildId = result.ReleaseId == release?.Intent.ReleaseId ? release?.Intent.Qualification.Artifact.Intent.BuildId : null;
        if (result.Response is { } response)
        {
            using var json = JsonDocument.Parse(response);
            buildId = Guid.Parse(json.RootElement.GetProperty("buildId").GetString()!);
            foreach (var output in json.RootElement.GetProperty("outputs").EnumerateArray())
                outputs.Add(new(output.GetProperty("name").GetString(), output.TryGetProperty("mediaType", out var media) ? media.GetString() : null,
                    output.GetProperty("content").GetBytesFromBase64()));
        }
        var message = new SandboxMessage(messageId, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, targetId, started, clock.ElapsedMilliseconds,
            Name(result.Status.ToString()), result.Outcome is { } outcome ? Name(outcome.ToString()) : null, result.Replayed, result.ReleaseId, buildId,
            sample.Source, replayOf, inputs, sample.Parameters, outputs);
        await history.DeliverAsync(message, token);
        foreach (var sink in sinks) await sink.DeliverAsync(message, token);
        // Retained endpoint responses expire after a day; the sandbox history keeps its own bounded copy.
        try { await ledger.PurgeExpiredAsync(reference, 100, token); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException)) { }
        return message;
    }

    public async Task<IReadOnlyList<SandboxMessage>?> ListAsync(ActorContext actor, MappingScope mapping, Guid targetId, int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > MaximumHistoryPage) throw new ArgumentException("Invalid history limit.");
        return await ResolveAsync(actor, mapping, targetId, token) is null ? null : await history.ListAsync(targetId, limit, token);
    }

    public async Task<SandboxMessage?> ReadAsync(ActorContext actor, MappingScope mapping, Guid targetId, Guid messageId, CancellationToken token = default)
    {
        if (await ResolveAsync(actor, mapping, targetId, token) is null) return null;
        var message = await history.ReadAsync(targetId, messageId, token);
        return message is not null && message.MappingId == mapping.MappingId && message.ProjectId == mapping.ProjectId ? message : null;
    }

    private async Task<(ReleaseTargetReference, SandboxTarget)?> ResolveAsync(ActorContext actor, MappingScope mapping, Guid targetId, CancellationToken token)
    {
        if (targetId == Guid.Empty) return null;
        if ((await TargetsAsync(actor, mapping, targetId, token)).SingleOrDefault() is not { } target) return null;
        return (new(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), target.EnvironmentId, target.EnvironmentVersion), targetId), target);
    }

    /// <summary>The mapping's sandbox targets: those whose identity is the one this instance derives for its runtime.</summary>
    private async Task<IReadOnlyList<SandboxTarget>> TargetsAsync(ActorContext actor, MappingScope mapping, Guid? targetId, CancellationToken token)
    {
        var result = new List<SandboxTarget>();
        await foreach (var row in RowsAsync(actor, mapping, targetId, "targets", token))
        {
            var environment = new BuildEnvironmentReference(new(mapping.OrganizationId, mapping.ProjectId), row.GetProperty("environment_id").GetGuid(),
                long.Parse(row.GetProperty("environment_version").GetString()!, CultureInfo.InvariantCulture));
            var profile = row.GetProperty("profile");
            var id = row.GetProperty("target_id").GetGuid();
            var image = row.GetProperty("runtime_image").GetString()!;
            if (image != runtimeImage || id != SandboxIdentity.TargetId(mapping, environment, profile.GetProperty("id").GetString()!,
                profile.GetProperty("version").GetInt64(), image)) continue;
            result.Add(new(id, environment.EnvironmentId, environment.Version, row.GetProperty("enabled").GetBoolean(),
                row.GetProperty("active_release_id").ValueKind == JsonValueKind.Null ? null : row.GetProperty("active_release_id").GetGuid(),
                long.Parse(row.GetProperty("version").GetString()!, CultureInfo.InvariantCulture), image));
        }
        return result;
    }

    private async Task<long?> OriginalVersionAsync(ActorContext actor, MappingScope mapping, Guid targetId, Guid operationId, CancellationToken token)
    {
        await foreach (var row in RowsAsync(actor, mapping, targetId, "actions", token))
            if (row.GetProperty("operation_id").GetGuid() == operationId)
                return long.Parse(row.GetProperty("expected_version").GetString()!, CultureInfo.InvariantCulture);
        return null;
    }

    private async IAsyncEnumerable<JsonElement> RowsAsync(ActorContext actor, MappingScope mapping, Guid? targetId, string section,
        [EnumeratorCancellation] CancellationToken token)
    {
        // Use the operations store's bounded pages, including for old deployment recovery.
        for (var offset = 0; offset <= 1000000; offset += 100)
        {
            var rows = await operationsReader.ReadAsync(actor, mapping, targetId, new(section, offset, 100), token);
            if (rows is not { ValueKind: JsonValueKind.Array } array) yield break;
            foreach (var row in array.EnumerateArray()) yield return row;
            if (array.GetArrayLength() < 100) yield break;
        }
        throw new InvalidOperationException("The sandbox operations history exceeds the supported query bound.");
    }

    private static string Name(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>The mapping's saved regression cases as samples; full case data still requires current sample-run authority.</summary>
public sealed class RegressionCaseSampleSource(IRegressionCaseReader cases) : ISandboxSampleSource
{
    public string Kind => "case";

    public async Task<IReadOnlyList<SandboxSampleDescriptor>> ListAsync(ActorContext actor, MappingScope mapping, CancellationToken token = default) =>
        (await cases.ListAsync(actor, mapping, 50, null, token)).Cases
            .Select(value => new SandboxSampleDescriptor(Kind, value.CaseId.ToString("D") + "/" + value.Version.ToString(CultureInfo.InvariantCulture), value.Name)).ToArray();

    public async Task<SandboxSample?> ReadAsync(ActorContext actor, MappingScope mapping, string reference, CancellationToken token = default)
    {
        var parts = reference.Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var id) || id == Guid.Empty ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1) return null;
        var value = await cases.ReadAsync(actor, mapping, id, version, token);
        return value is null ? null : new(value.Replay.Primary, value.Replay.NamedInputs, value.Replay.Parameters,
            $"case {value.Name} (v{version.ToString(CultureInfo.InvariantCulture)})");
    }
}
