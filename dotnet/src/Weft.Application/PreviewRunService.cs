using System.Text;
using Weft.Domain;

namespace Weft.Application;

public enum PreviewExecutionOutcome
{
    /// <summary>The attempt recorded a terminal outcome, including published outputs on success.</summary>
    Completed,
    /// <summary>The attempt returned the run to the queue after an infrastructure failure or shutdown.</summary>
    Released,
    /// <summary>Another attempt holds a live lease, or the run is not yet eligible.</summary>
    Busy,
    /// <summary>The run already finished, possibly here because of cancellation, authority or attempts.</summary>
    Finished,
    Missing,
    /// <summary>The lease was superseded; this attempt published nothing.</summary>
    ClaimLost
}

/// <summary>
/// Owns preview run submission, submitter reads and cancellation. Every transition
/// re-checks the submitter's current project authority in the store.
/// </summary>
public sealed class PreviewRunService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IPreviewRunStore _store;
    private readonly IPreviewRunReader _reader;
    private readonly TimeProvider _time;
    private readonly PreviewRunOptions _options;

    public PreviewRunService(IPreviewRunStore store, IPreviewRunReader reader, TimeProvider time, PreviewRunOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = PreviewRunExecutor.Validate(options ?? new());
    }

    public async Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(submission);
        if (Refusal(submission) is { } refusal)
            throw new PreviewRunException(refusal);
        var document = PreviewRequestDocument.Encode(submission, _options.Limits);
        var record = new PreviewRunRecord(submission, document, PreviewRequestDocument.Clock(_time.GetUtcNow()),
            _options.MaxAttempts, _options.Retention);
        return await _store.SubmitAsync(actor, record, cancellationToken).ConfigureAwait(false);
    }

    public Task<PreviewRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default) => _reader.ReadAsync(actor, organizationId, runId, cancellationToken);

    public Task<PreviewOutputContent?> ReadOutputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default) => _reader.ReadOutputAsync(actor, organizationId, runId, ordinal, cancellationToken);

    public Task<PreviewRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null,
        CancellationToken cancellationToken = default) => _reader.ListAsync(actor, mapping, limit, afterRunId, cancellationToken);

    public Task<IReadOnlyList<PreviewInputDescriptor>?> ReadInputDescriptorsAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default) => _reader.ReadInputDescriptorsAsync(actor, organizationId, runId, cancellationToken);

    public Task<PreviewOutputContent?> ReadInputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default) => _reader.ReadInputAsync(actor, organizationId, runId, ordinal, cancellationToken);

    public Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default) => _store.CancelAsync(actor, organizationId, runId, cancellationToken);

    private string? Refusal(PreviewRunSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission.Revision);
        ArgumentNullException.ThrowIfNull(submission.Target);
        ArgumentNullException.ThrowIfNull(submission.Primary);
        ArgumentNullException.ThrowIfNull(submission.NamedInputs);
        ArgumentNullException.ThrowIfNull(submission.Parameters);
        if (submission.RequestKey == Guid.Empty)
            return "A preview request key is required.";
        if (submission.Target.Kind == PreviewTargetKind.Named && !BoundedName(submission.Target.Name))
            return "The selected target name is outside its bounds.";
        if (submission.Primary.Name is not null || submission.Primary.ByteCount == 0)
            return "The primary input must be a nonempty unnamed document.";
        if (submission.NamedInputs.Count + 1 > PreviewPolicy.MaximumInputDocuments)
            return "The request exceeds the input document count limit.";
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = submission.Primary.ByteCount;
        foreach (var input in submission.NamedInputs)
        {
            if (input is null || !BoundedName(input.Name) || !names.Add(input.Name!) || input.ByteCount == 0)
                return "Named inputs must be nonempty documents with distinct bounded names.";
            total += input.ByteCount;
        }
        if (total > _options.Limits.MaximumInputBytes)
            return "The input documents exceed the preview input limit.";
        if (submission.Parameters.Count > PreviewPolicy.MaximumParameters)
            return "The request exceeds the runtime parameter count limit.";
        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in submission.Parameters)
        {
            if (parameter is null || !BoundedName(parameter.Name) || !parameters.Add(parameter.Name) || !Enum.IsDefined(parameter.Kind)
                || parameter.Value is null || !Utf8Within(parameter.Value, PreviewPolicy.MaximumParameterTextBytes)
                || (parameter.Kind == PreviewParameterKind.Boolean && parameter.Value is not ("true" or "false")))
                return "Runtime parameters must have distinct bounded names and admitted values.";
        }
        return null;
    }

    private static bool BoundedName(string? name) =>
        !string.IsNullOrEmpty(name) && !name.Contains('\0', StringComparison.Ordinal) && Utf8Within(name, PreviewPolicy.MaximumNameBytes);

    private static bool Utf8Within(string text, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(text) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>
/// Executes leased attempts of stored preview runs through the preview backend, with native
/// bytes read from private storage. It never submits or reads on a caller's behalf.
/// </summary>
public sealed class PreviewRunExecutor : IExecutionAdmission
{
    private readonly IPreviewRunStore _store;
    private readonly IPrivateObjectStore _objects;
    private readonly IMappingPreviewBackend _backend;
    private readonly TimeProvider _time;
    private readonly PreviewRunOptions _options;

    public bool CanExecute => _backend is not IExecutionAdmission { CanExecute: false };

    public PreviewRunExecutor(IPreviewRunStore store, IPrivateObjectStore objects, IMappingPreviewBackend backend,
        TimeProvider time, PreviewRunOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = Validate(options ?? new());
    }

    internal static PreviewRunOptions Validate(PreviewRunOptions options)
    {
        if (options.MaxAttempts is < 1 or > 8 || options.Lease < TimeSpan.FromSeconds(35) || options.Lease > TimeSpan.FromMinutes(10)
            || options.Retention < TimeSpan.FromMinutes(1) || options.Retention > TimeSpan.FromDays(7)
            || options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromHours(1)
            || options.CancellationPollInterval <= TimeSpan.Zero || options.CancellationPollInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options), "Preview run options are outside their admitted bounds.");
        return options;
    }

    /// <summary>
    /// Claims and executes one attempt. A caller token canceled by host shutdown releases the run
    /// for another worker; a cancellation requested through the store completes it as canceled.
    /// </summary>
    public async Task<PreviewExecutionOutcome> ExecuteAsync(Guid organizationId, Guid runId, Guid workerInstance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanExecute) return PreviewExecutionOutcome.Busy;
        var claimed = await _store.ClaimAsync(organizationId, runId, workerInstance, _options.Lease, cancellationToken).ConfigureAwait(false);
        switch (claimed.Outcome)
        {
            case PreviewClaimOutcome.Busy: return PreviewExecutionOutcome.Busy;
            case PreviewClaimOutcome.Finished: return PreviewExecutionOutcome.Finished;
            case PreviewClaimOutcome.Missing: return PreviewExecutionOutcome.Missing;
        }
        var claim = claimed.Claim ?? throw new PreviewRunException("A claimed preview run has no claim.");

        PreviewRunCompletion completion;
        try
        {
            if (await PrepareAsync(claim, cancellationToken).ConfigureAwait(false) is not { } request)
                return await ReleaseAsync(claim, _options.RetryDelay).ConfigureAwait(false);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var watching = new CancellationTokenSource();
            var watcher = WatchAsync(claim, execution, watching.Token);
            MappingPreviewResult result;
            try
            {
                result = await _backend.PreviewAsync(request, execution.Token).ConfigureAwait(false);
                var expectedProfile = request.Profile == PreviewExecutionProfile.Document ? DocumentPreviewPolicy.Profile : PreviewPolicy.Profile;
                if (result.Profile != expectedProfile || result.Artifacts.Any(output =>
                    request.Profile == PreviewExecutionProfile.Document
                        ? !DocumentPreviewPolicy.AdmitsMediaType(output.MediaType)
                        : output.MediaType != PreviewPolicy.MediaType))
                    throw new PreviewRunException("The preview backend did not preserve the requested admission profile.");
            }
            finally
            {
                await watching.CancelAsync().ConfigureAwait(false);
            }
            var observed = await watcher.ConfigureAwait(false);
            if (observed == Watch.ClaimLost)
                return PreviewExecutionOutcome.ClaimLost;
            if (observed == Watch.CancelRequested)
                completion = Canceled();
            else if (cancellationToken.IsCancellationRequested)
                return await ReleaseAsync(claim, TimeSpan.Zero).ConfigureAwait(false);
            else if (result.Status is MappingPreviewStatus.BackendUnavailable or MappingPreviewStatus.IsolationFailure)
                return await ReleaseAsync(claim, _options.RetryDelay).ConfigureAwait(false);
            else
                completion = Completion(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await ReleaseAsync(claim, TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (PreviewRunException error)
        {
            completion = new(PreviewRunOutcome.ProtocolFailure,
                new("preview.storedRequest", error.Message, false, null, false), null, 0, []);
        }
        return await _store.CompleteAsync(claim, completion, CancellationToken.None).ConfigureAwait(false)
            ? PreviewExecutionOutcome.Completed : PreviewExecutionOutcome.ClaimLost;
    }

    /// <summary>Returns null when the native object cannot currently be read and the attempt should retry.</summary>
    private async Task<MappingPreviewRequest?> PrepareAsync(PreviewRunClaim claim, CancellationToken cancellationToken)
    {
        var description = PreviewRequestDocument.Decode(claim.RequestDocument);
        if (description.Revision != claim.Revision || claim.NativeObject.Id.Scope != claim.Revision)
            throw new PreviewRunException("The stored preview request does not match its run.");
        var inputs = await _store.ReadInputsAsync(claim, cancellationToken).ConfigureAwait(false);
        if (inputs.Count != description.Inputs.Count)
            throw new PreviewRunException("The stored preview inputs do not match their declarations.");
        for (var index = 0; index < inputs.Count; index++)
        {
            var (stored, declared) = (inputs[index], description.Inputs[index]);
            if (stored.Ordinal != index || stored.Name != declared.Name || stored.Content.LongLength != declared.ByteLength
                || stored.Digest != declared.Digest || Sha256Digest.FromBytes(stored.Content) != declared.Digest)
                throw new PreviewRunException("The stored preview inputs do not match their declarations.");
        }

        byte[] native;
        try
        {
            native = await _objects.ReadVerifiedAsync(claim.Revision, claim.NativeObject, cancellationToken).ConfigureAwait(false);
        }
        catch (PrivateObjectStorageException)
        {
            return null;
        }
        var artifact = NativeArtifact.Admit(claim.Revision, native, PreviewPolicy.MaximumNativeBytes);
        if (artifact.Digest != claim.NativeObject.Digest || artifact.ByteCount != claim.NativeObject.ByteCount)
            throw new PreviewRunException("The native object does not match the run's revision.");
        return new MappingPreviewRequest(artifact, description.Target, new PreviewInputDocument(null, inputs[0].Content),
            inputs.Skip(1).Select(input => new PreviewInputDocument(input.Name, input.Content)).ToArray(),
            description.Parameters, claim.CurrentDateTime, description.Limits)
        { Profile = description.Profile };
    }

    private enum Watch { Running, CancelRequested, ClaimLost }

    private async Task<Watch> WatchAsync(PreviewRunClaim claim, CancellationTokenSource execution, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CancellationPollInterval, _time, stop).ConfigureAwait(false);
                var observation = await _store.ObserveAsync(claim, stop).ConfigureAwait(false);
                if (!observation.ClaimCurrent || observation.CancelRequested)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    return observation.ClaimCurrent ? Watch.CancelRequested : Watch.ClaimLost;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed observation leaves the attempt running; its fenced completion decides.
            }
        }
        return Watch.Running;
    }

    private async Task<PreviewExecutionOutcome> ReleaseAsync(PreviewRunClaim claim, TimeSpan delay) =>
        await _store.ReleaseAsync(claim, delay, CancellationToken.None).ConfigureAwait(false)
            ? PreviewExecutionOutcome.Released : PreviewExecutionOutcome.ClaimLost;

    private static PreviewRunCompletion Canceled() =>
        new(PreviewRunOutcome.Canceled, new("preview.canceled", "Preview was canceled.", false, null, false), null, 0, []);

    private static PreviewRunCompletion Completion(MappingPreviewResult result)
    {
        var outcome = Enum.Parse<PreviewRunOutcome>(result.Status.ToString());
        var provenance = new PreviewRunProvenance(result.Provenance.Confirmed, result.Provenance.EngineRevision,
            result.Provenance.BackendBuild, result.Provenance.AdapterBuild, result.Profile);
        return outcome == PreviewRunOutcome.Succeeded
            ? new(outcome, null, provenance, result.RecordsWritten, result.Artifacts)
            : new(outcome, result.Failure, provenance, 0, []);
    }
}
