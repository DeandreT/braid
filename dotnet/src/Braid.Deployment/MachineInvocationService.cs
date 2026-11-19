using Braid.Deployment.Triggers;
using System.Globalization;
using System.Text.Json;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Deployment;

public sealed record MachineInputMedia(string? Name, string MediaType, CsvDocumentDialect? CsvDialect = null, XmlDocumentMetadata? XmlMetadata = null);

/// <summary>
/// One machine request for the whole qualified artifact: the primary input, every declared named input and
/// typed parameters. Its digest binds input names, lengths and SHA-256 digests and the exact parameters.
/// </summary>
public sealed class MachineInvocationRequest
{
    public MachineInvocationRequest(PreviewInputDocument primary, IReadOnlyList<PreviewInputDocument> namedInputs, IReadOnlyList<PreviewParameter> parameters,
        IReadOnlyList<MachineInputMedia>? inputMedia = null)
    {
        ArgumentNullException.ThrowIfNull(primary); ArgumentNullException.ThrowIfNull(namedInputs); ArgumentNullException.ThrowIfNull(parameters);
        if (primary.Name is not null || namedInputs.Any(input => input is null || input.Name is null) || parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Invalid machine invocation request.");
        Primary = primary;
        NamedInputs = Array.AsReadOnly(namedInputs.OrderBy(input => input.Name, StringComparer.Ordinal).ToArray());
        Parameters = Array.AsReadOnly(parameters.OrderBy(parameter => parameter.Name, StringComparer.Ordinal).ToArray());
        if (NamedInputs.Select(input => input.Name).Distinct(StringComparer.Ordinal).Count() != NamedInputs.Count ||
            Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != Parameters.Count)
            throw new ArgumentException("Input and parameter names must be unique.");
        if (inputMedia is not null)
        {
            if (inputMedia.Any(value => value is null || (value.MediaType == XmlDocumentPolicy.MediaType || value.XmlMetadata is not null
                    ? value.CsvDialect is not null || !XmlDocumentPolicy.Admits(value.XmlMetadata, XmlDocumentRole.Input, value.MediaType)
                    : CsvDocumentPolicy.AdmitsMediaType(value.MediaType)
                    ? !CsvDocumentPolicy.Admits(value.CsvDialect, value.MediaType)
                    : value.CsvDialect is not null || !DocumentPreviewPolicy.AdmitsMediaType(value.MediaType))) ||
                inputMedia.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != inputMedia.Count ||
                inputMedia.Count != NamedInputs.Count + 1 ||
                !inputMedia.Select(value => value.Name).ToHashSet(StringComparer.Ordinal).SetEquals(NamedInputs.Prepend(primary).Select(value => value.Name)))
                throw new ArgumentException("Every document input requires its explicit supported media type.");
            InputMedia = Array.AsReadOnly(inputMedia.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        }
        foreach (var input in NamedInputs.Prepend(primary))
        {
            var declared = InputMedia?.Single(value => value.Name == input.Name);
            if (input.XmlMetadata != declared?.XmlMetadata || input.CsvDialect != declared?.CsvDialect || input.ByteCount == 0 && declared?.CsvDialect is null)
                throw new ArgumentException("Empty CSV input requires its explicit admitted dialect.");
        }
        if (InputMedia?.Any(value => value.CsvDialect is not null) == true &&
            (InputMedia.Count != 1 || InputMedia[0].Name is not null)) throw new ArgumentException("CSV requires primary-only input.");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", InputMedia is null ? "braid.mapping-invocation-request.v1" : "braid.document-invocation-request.v1");
            json.WriteStartArray("inputs");
            foreach (var input in NamedInputs.Prepend(primary))
            {
                json.WriteStartObject();
                json.WriteString("name", input.Name);
                json.WriteNumber("bytes", input.ByteCount);
                json.WriteString("sha256", input.Digest.Value);
                if (InputMedia is not null)
                {
                    var declared = InputMedia.Single(value => value.Name == input.Name);
                    json.WriteString("mediaType", declared.MediaType);
                    if (declared.XmlMetadata is { } xml) { json.WritePropertyName("xmlMetadata"); XmlDocumentMetadataJson.Write(json, xml, XmlDocumentRole.Input); }
                    if (declared.CsvDialect is { } dialect)
                    {
                        json.WritePropertyName("csvDialect");
                        BuildCsvDialect.Write(json, dialect);
                    }
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("parameters");
            foreach (var parameter in Parameters)
            {
                json.WriteStartObject();
                json.WriteString("name", parameter.Name);
                json.WriteString("kind", parameter.Kind.ToString().ToLowerInvariant());
                json.WriteString("value", parameter.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        Digest = Sha256Digest.FromBytes(stream.ToArray());
    }

    public PreviewInputDocument Primary { get; }
    public IReadOnlyList<PreviewInputDocument> NamedInputs { get; }
    public IReadOnlyList<PreviewParameter> Parameters { get; }
    public IReadOnlyList<MachineInputMedia>? InputMedia { get; }
    public Sha256Digest Digest { get; }
    public override string ToString() => nameof(MachineInvocationRequest);
}

/// <summary>Completed and Failed carry a recorded outcome; the other statuses ran nothing and recorded nothing final.</summary>
public enum MachineInvocationStatus { Completed, Failed, Expired, Conflict, InProgress, Refused, NoRelease, Unavailable }

public sealed record MachineInvocationResult(MachineInvocationStatus Status, bool Replayed = false, MachineInvocationOutcome? Outcome = null,
    byte[]? Response = null, Guid? ReleaseId = null)
{
    public override string ToString() => nameof(MachineInvocationResult);
}

/// <summary>
/// Runs a machine request against the target's current release through <see cref="LocalReleaseInvocation"/>, under the
/// authority of that release's publisher. The ledger claims the request first, so a recorded outcome is returned again
/// instead of running twice. Host claims pin their activated release; historical ledger claims fence the active pointer.
/// </summary>
public sealed class MachineInvocationService
{
    private readonly IMachineInvocationLedger ledger;
    private readonly ILocalReleaseStore releases;
    private readonly Func<ActorContext, LocalReleaseInvocation> invocations;
    private readonly TimeProvider time;
    private readonly PreviewLimits limits;
    private readonly TimeSpan releaseBound;

    /// <param name="releaseBound">
    /// How long releasing a canceled call's attempt may take (10 seconds by default); a lost release expires with the
    /// lease. A host with a fixed call deadline passes what its deadline leaves for it.
    /// </param>
    public MachineInvocationService(IMachineInvocationLedger ledger, ILocalReleaseStore releases, Func<ActorContext, LocalReleaseInvocation> invocations,
        TimeProvider time, PreviewLimits limits, TimeSpan? releaseBound = null)
    {
        ArgumentNullException.ThrowIfNull(ledger); ArgumentNullException.ThrowIfNull(releases); ArgumentNullException.ThrowIfNull(invocations);
        ArgumentNullException.ThrowIfNull(time); ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumOutputBytes > (MachineInvocationPolicy.MaximumRetainedBytes - 64 * 1024) / 4 * 3)
            throw new ArgumentException("The output limit must fit the retained response bound.", nameof(limits));
        if (releaseBound is { } bound && bound <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(releaseBound));
        this.ledger = ledger; this.releases = releases; this.invocations = invocations; this.time = time; this.limits = limits;
        this.releaseBound = releaseBound ?? TimeSpan.FromSeconds(10);
    }

    public async Task<MachineInvocationResult> InvokeAsync(ReleaseTargetReference target, MachineCaller caller, Guid requestId,
        MachineInvocationRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        var key = new MachineInvocationKey(target, caller.Principal, requestId);
        var claim = await ledger.ClaimWithMetadataAsync(key, caller.ApplicationId, request.Digest,
            checked(request.Primary.ByteCount + request.NamedInputs.Sum(input => input.ByteCount)), token).ConfigureAwait(false);
        switch (claim.Status)
        {
            case MachineInvocationClaimStatus.Recorded: return Recorded(claim.Outcome!.Value, claim.RetainedResponse, claim.ReleaseId, replayed: true);
            case MachineInvocationClaimStatus.Conflict: return new(MachineInvocationStatus.Conflict);
            case MachineInvocationClaimStatus.InProgress: return new(MachineInvocationStatus.InProgress);
            case MachineInvocationClaimStatus.Refused: return new(MachineInvocationStatus.Refused);
            case MachineInvocationClaimStatus.NoRelease: return new(MachineInvocationStatus.NoRelease);
            case MachineInvocationClaimStatus.Capacity: return new(MachineInvocationStatus.Unavailable, Outcome: MachineInvocationOutcome.Busy);
        }
        var publisher = claim.Publisher ?? throw new InvalidOperationException("A claimed machine invocation has no publisher.");
        var clock = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        Attempt attempt;
        try { attempt = await RunAsync(new ActorContext(publisher), target, requestId, request, claim.ReleaseId, clock, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Release the attempt so the caller can retry under the same ID; a lost release expires with the lease.
            using var release = new CancellationTokenSource(releaseBound);
            try { await ledger.FinishAsync(key, claim.ClaimId, caller.ApplicationId, MachineInvocationOutcome.Unavailable, null, null, null, release.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
            throw;
        }
        var finish = await ledger.FinishAsync(key, claim.ClaimId, caller.ApplicationId, attempt.Outcome, attempt.Provenance,
            attempt.Provenance is null ? null : clock, attempt.Response, token).ConfigureAwait(false);
        return finish switch
        {
            MachineInvocationFinishStatus.Recorded => Recorded(attempt.Outcome, attempt.Response, attempt.Provenance?.ReleaseId, replayed: false),
            MachineInvocationFinishStatus.Revoked => new(MachineInvocationStatus.Refused),
            _ => new(MachineInvocationStatus.Unavailable)
        };
    }

    public async Task<TriggeredInvocationResult> InvokeTriggeredAsync(ITriggeredInvocationLedger triggered, ReleaseTargetReference target, Guid workId,
        MachineCaller caller, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(triggered); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        token.ThrowIfCancellationRequested();
        var claim = await triggered.ClaimPinnedAsync(target, workId, caller, token).ConfigureAwait(false);
        switch (claim.Status)
        {
            case TriggerClaimStatus.Recorded:
                return new(claim.Outcome == MachineInvocationOutcome.Completed ? TriggeredInvocationStatus.Completed : TriggeredInvocationStatus.Failed, claim.Outcome);
            case TriggerClaimStatus.InProgress: return new(TriggeredInvocationStatus.InProgress);
            case TriggerClaimStatus.Refused: return new(TriggeredInvocationStatus.Refused);
            case TriggerClaimStatus.Fenced: return new(TriggeredInvocationStatus.Fenced);
            case TriggerClaimStatus.ReleaseSuperseded: return new(TriggeredInvocationStatus.ReleaseSuperseded);
        }
        var pin = claim.Pin!; var configuration = claim.Configuration!;
        var clock = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        Attempt attempt;
        try
        {
            // The stored request is rebuilt from the admitted configuration version and must reproduce the stored digest.
            var request = PilotRouteInputs.Request(configuration, claim.CopyInput());
            attempt = request.Digest != claim.RequestDigest
                ? new(MachineInvocationOutcome.IntegrityFailure)
                : await RunAsync(new ActorContext(pin.Publisher), target, claim.RequestId, request, pin.Provenance.ReleaseId, clock, token, requireActivePin: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var release = new CancellationTokenSource(releaseBound);
            try
            {
                await triggered.FinishAndEnqueueAsync(target, workId, claim.ClaimId, caller.ApplicationId, MachineInvocationOutcome.Unavailable,
                    null, null, null, null, release.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
            throw;
        }
        catch (ArgumentException) { attempt = new(MachineInvocationOutcome.InvalidInput); }
        // Every produced output needs an explicit route and every route a produced output; otherwise nothing is enqueued.
        if (attempt.Outcome == MachineInvocationOutcome.Completed &&
            !configuration.RoutesCover(attempt.Outputs!.Outputs.Select(output => output.Key.IsPrimary ? null : output.Key.Name)))
            attempt = new(MachineInvocationOutcome.IntegrityFailure, attempt.Provenance);
        if (attempt.Provenance is { } provenance && provenance != pin.Provenance)
            attempt = new(MachineInvocationOutcome.ReleaseChanged);
        var finish = await triggered.FinishAndEnqueueAsync(target, workId, claim.ClaimId, caller.ApplicationId, attempt.Outcome, attempt.Provenance,
            attempt.Provenance is null ? null : clock, attempt.Response, attempt.Response is null ? null : attempt.Outputs, token).ConfigureAwait(false);
        return finish switch
        {
            TriggerFinishStatus.Recorded => new(attempt.Outcome switch
            {
                MachineInvocationOutcome.Completed => TriggeredInvocationStatus.Completed,
                MachineInvocationOutcome.Unavailable or MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged => TriggeredInvocationStatus.Retry,
                _ => TriggeredInvocationStatus.Failed
            }, attempt.Outcome),
            TriggerFinishStatus.Fenced => new(TriggeredInvocationStatus.Fenced),
            TriggerFinishStatus.ReleaseSuperseded => new(TriggeredInvocationStatus.ReleaseSuperseded),
            _ => new(TriggeredInvocationStatus.Retry)
        };
    }

    private sealed record Attempt(MachineInvocationOutcome Outcome, MachineInvocationProvenance? Provenance = null, byte[]? Response = null, RegressionOutputSet? Outputs = null);

    private async Task<Attempt> RunAsync(ActorContext actor, ReleaseTargetReference target, Guid requestId, MachineInvocationRequest request,
        Guid? pin, string clock, CancellationToken token, bool requireActivePin = false)
    {
        try
        {
            var before = await releases.ReadTargetAsync(actor, target, token).ConfigureAwait(false);
            if (before is null || !before.Enabled || before.Target.Reference != target) return new(MachineInvocationOutcome.Refused);
            if (requireActivePin && before.ActiveReleaseId != pin) return new(MachineInvocationOutcome.ReleaseChanged);
            if ((pin ?? before.ActiveReleaseId) is not { } active) return new(MachineInvocationOutcome.ReleaseChanged);
            var release = await releases.ReadAsync(actor, target, active, token).ConfigureAwait(false);
            if (release is null || release.Phase != ReleasePhase.Active || release.Intent.ReleaseId != active)
                return await ChangedAsync(actor, target, pin is null ? active : null, MachineInvocationOutcome.Refused, null, token).ConfigureAwait(false);
            var provenance = MachineInvocationProvenance.From(release.Intent);
            var intent = release.Intent.Qualification.Artifact.Intent;
            var documents = BuildWorkerProtocol.IsDocumentProfile(intent.Profile.Id);
            if (documents != (request.InputMedia is not null)) return new(MachineInvocationOutcome.InvalidInput, provenance);
            if (request.InputMedia is { } media)
            {
                var boundaries = intent.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryInput or BuildBoundaryKind.NamedInput).ToArray();
                if (boundaries.Length != media.Count || media.Any(value => !boundaries.Any(boundary => boundary.Name == value.Name &&
                    BuildWorkerProtocol.AdmitsBoundary(intent.Profile.Id, boundary.Format, boundary.CsvDialect) &&
                    BuildWorkerProtocol.MediaType(boundary.Format) == value.MediaType && boundary.CsvDialect == value.CsvDialect && boundary.XmlMetadata == value.XmlMetadata)))
                    return new(MachineInvocationOutcome.InvalidInput, provenance);
            }
            RegressionReplay replay;
            try { replay = new(PreviewTargetSelection.All, clock, limits, request.Primary, request.NamedInputs, request.Parameters, token); }
            catch (RegressionCaseException error)
            { return new(error.Failure == RegressionCaseFailure.ResourceLimit ? MachineInvocationOutcome.ResourceLimit : MachineInvocationOutcome.InvalidInput, provenance); }
            var result = await invocations(actor).ExecuteAsync(actor, target, replay, token, pin).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var outcome = result.Outcome switch
            {
                CompiledBuildOutcome.Completed => MachineInvocationOutcome.Completed,
                CompiledBuildOutcome.Refused => MachineInvocationOutcome.Refused,
                CompiledBuildOutcome.Unsupported => MachineInvocationOutcome.Unsupported,
                CompiledBuildOutcome.InvalidInput => MachineInvocationOutcome.InvalidInput,
                CompiledBuildOutcome.InvalidParameter => MachineInvocationOutcome.InvalidParameter,
                CompiledBuildOutcome.ExecutionFailed => MachineInvocationOutcome.ExecutionFailed,
                CompiledBuildOutcome.ResourceLimit => MachineInvocationOutcome.ResourceLimit,
                CompiledBuildOutcome.IntegrityFailure => MachineInvocationOutcome.IntegrityFailure,
                CompiledBuildOutcome.Busy => MachineInvocationOutcome.Busy,
                _ => MachineInvocationOutcome.Unavailable
            };
            if (outcome == MachineInvocationOutcome.Completed && (result.Outputs is null ||
                result.Outputs.Outputs.Count != intent.Boundaries.Count(boundary => boundary.Kind is BuildBoundaryKind.PrimaryOutput or BuildBoundaryKind.NamedOutput) ||
                result.Outputs.Outputs.Any(output => !intent.Boundaries.Any(boundary =>
                    boundary.Kind == (output.Key.IsPrimary ? BuildBoundaryKind.PrimaryOutput : BuildBoundaryKind.NamedOutput) &&
                    boundary.Name == output.Key.Name && BuildWorkerProtocol.AdmitsFormat(intent.Profile.Id, boundary.Format) &&
                    BuildWorkerProtocol.MediaType(boundary.Format) == output.MediaType && boundary.CsvDialect == output.CsvDialect && boundary.XmlMetadata == output.XmlMetadata))))
                outcome = MachineInvocationOutcome.IntegrityFailure;
            var response = outcome == MachineInvocationOutcome.Completed ? Encode(requestId, provenance, clock, result.Outputs!, documents) : null;
            if (response?.Length > MachineInvocationPolicy.MaximumRetainedBytes) (outcome, response) = (MachineInvocationOutcome.ResourceLimit, null);
            return await ChangedAsync(actor, target, pin is null ? active : null, outcome, provenance, token, response, response is null ? null : result.Outputs).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A store or transport failure produced no outcome; the caller may retry the same request.
            return new(MachineInvocationOutcome.Unavailable);
        }
    }

    // A rollback, new activation or disable during the attempt makes it retryable, never a stale delivery.
    private async Task<Attempt> ChangedAsync(ActorContext actor, ReleaseTargetReference target, Guid? active, MachineInvocationOutcome outcome,
        MachineInvocationProvenance? provenance, CancellationToken token, byte[]? response = null, RegressionOutputSet? outputs = null)
    {
        var after = await releases.ReadTargetAsync(actor, target, token).ConfigureAwait(false);
        if (after is null || !after.Enabled) return new(MachineInvocationOutcome.Refused, provenance);
        if (active is not null && after.ActiveReleaseId != active) return new(MachineInvocationOutcome.ReleaseChanged, provenance);
        return new(outcome, provenance, response, outputs);
    }

    private static MachineInvocationResult Recorded(MachineInvocationOutcome outcome, byte[]? response, Guid? release, bool replayed) => outcome switch
    {
        MachineInvocationOutcome.Completed when response is null => new(MachineInvocationStatus.Expired, replayed, outcome, null, release),
        MachineInvocationOutcome.Completed => new(MachineInvocationStatus.Completed, replayed, outcome, response, release),
        MachineInvocationOutcome.Unavailable or MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged =>
            new(MachineInvocationStatus.Unavailable, replayed, outcome),
        _ => new(MachineInvocationStatus.Failed, replayed, outcome, null, release)
    };

    /// <summary>The successful response document. Its exact bytes are retained and returned for a replay.</summary>
    public static byte[] Encode(Guid requestId, MachineInvocationProvenance provenance, string clock, RegressionOutputSet outputs, bool documents = false)
    {
        ArgumentNullException.ThrowIfNull(provenance); ArgumentNullException.ThrowIfNull(outputs);
        if (!documents && outputs.Outputs.Any(value => value.MediaType != PreviewPolicy.MediaType))
            throw new ArgumentException("Legacy responses cannot relabel document output media.");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", documents ? "braid.document-invocation-response.v1" : "braid.mapping-invocation-response.v1");
            json.WriteString("requestId", requestId.ToString("D"));
            json.WriteString("releaseId", provenance.ReleaseId.ToString("D"));
            json.WriteString("buildId", provenance.BuildId.ToString("D"));
            json.WriteString("revisionId", provenance.RevisionId.ToString("D"));
            json.WriteString("artifactDigest", provenance.ArtifactDigest.Value);
            json.WriteString("currentDateTime", clock);
            json.WriteStartArray("outputs");
            foreach (var output in outputs.Outputs.OrderBy(value => value.Key.IsPrimary ? 0 : 1).ThenBy(value => value.Key.Name, StringComparer.Ordinal))
            {
                var bytes = output.CopyBytes();
                json.WriteStartObject();
                json.WriteString("name", output.Key.IsPrimary ? null : output.Key.Name);
                json.WriteString("sha256", output.Digest.Value);
                if (documents) json.WriteString("mediaType", output.MediaType);
                if (output.XmlMetadata is { } xml) { json.WritePropertyName("xmlMetadata"); XmlDocumentMetadataJson.Write(json, xml, XmlDocumentRole.Output); }
                if (output.CsvDialect is { } dialect)
                {
                    if (!documents) throw new ArgumentException("CSV output requires document mode.");
                    json.WritePropertyName("csvDialect");
                    BuildCsvDialect.Write(json, dialect);
                }
                json.WriteBase64String("content", bytes);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
}
