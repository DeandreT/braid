using Weft.Domain;

namespace Weft.Application.Regression;

public enum RegressionCaseExecutionOutcome
{
    Completed,
    NotAuthorizedOrNotFound,
    CaseChanged,
    RuntimeMismatch,
    SourceMismatch,
    ResultRejected,
    InfrastructureFailure
}

/// <summary>One case's private result, not an authorization grant or a durable job receipt.</summary>
public sealed class RegressionCaseExecution
{
    private RegressionCaseExecution(RegressionCaseExecutionOutcome outcome, MappingPreviewStatus? previewStatus,
        RegressionOutputSet? outputs, RegressionComparisonResult? comparison)
    {
        Outcome = outcome;
        PreviewStatus = previewStatus;
        Outputs = outputs;
        Comparison = comparison;
    }

    public RegressionCaseExecutionOutcome Outcome { get; }
    public MappingPreviewStatus? PreviewStatus { get; }
    public RegressionOutputSet? Outputs { get; }
    public RegressionComparisonResult? Comparison { get; }

    internal static RegressionCaseExecution Complete(MappingPreviewStatus status, RegressionOutputSet outputs,
        RegressionComparisonResult comparison) => new(RegressionCaseExecutionOutcome.Completed, status, outputs, comparison);

    internal static RegressionCaseExecution Refused(RegressionCaseExecutionOutcome outcome) => new(outcome, null, null, null);

    public override string ToString() => $"{nameof(RegressionCaseExecution)}:{Outcome}";
}

/// <summary>
/// Replays one pinned private case through the existing preview boundary. Each invocation is an
/// independent failure boundary; no batch state, automatic retry, queue or history is implied.
/// </summary>
public interface IRegressionCaseExecutor
{
    Task<RegressionCaseExecution> ExecuteAsync(ActorContext actor, RegressionCaseReference expected, CancellationToken cancellationToken = default);
}

public sealed class RegressionCaseExecutionService : IRegressionCaseExecutor, IRegressionEvaluationExecutor, IExecutionAdmission
{
    private readonly IRegressionCaseReader cases;
    private readonly IProjectReader projects;
    private readonly IRevisionReportMetadataReader sources;
    private readonly IPrivateObjectStore objects;
    private readonly IMappingPreviewBackend backend;
    private readonly RegressionRuntimeIdentity runtime;

    public bool CanExecute => backend is not IExecutionAdmission { CanExecute: false };

    public RegressionCaseExecutionService(IRegressionCaseReader cases, IProjectReader projects,
        IRevisionReportMetadataReader sources, IPrivateObjectStore objects, IMappingPreviewBackend backend,
        RegressionRuntimeIdentity runtime)
    {
        this.cases = cases ?? throw new ArgumentNullException(nameof(cases));
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.objects = objects ?? throw new ArgumentNullException(nameof(objects));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (runtime.BackendId != "ferrule" || runtime.Profile != PreviewPolicy.Profile)
            throw new ArgumentException("Case execution requires the supported preview output identity profile.", nameof(runtime));
    }

    public async Task<RegressionCaseExecution> ExecuteAsync(ActorContext actor, RegressionCaseReference expected,
        CancellationToken cancellationToken = default)
        => await EvaluateAsync(actor, expected, new(expected.Revision, expected.NativeDigest), cancellationToken).ConfigureAwait(false);

    public async Task<RegressionCaseExecution> EvaluateAsync(ActorContext actor, RegressionCaseReference expected,
        RegressionEvaluationTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Revision.OrganizationId != expected.Revision.OrganizationId ||
            target.Revision.ProjectId != expected.Revision.ProjectId || target.Revision.MappingId != expected.Revision.MappingId)
            throw new ArgumentException("An evaluation target must belong to the original case's mapping.", nameof(target));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var mapping = new MappingScope(expected.Revision.OrganizationId, expected.Revision.ProjectId, expected.Revision.MappingId);
            var value = await cases.ReadAsync(actor, mapping, expected.CaseId, expected.Version, cancellationToken).ConfigureAwait(false);
            if (value is null) return Refused(RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
            if (new RegressionCaseEnvelopeCodec().Encode(value, cancellationToken).Reference != expected)
                return Refused(RegressionCaseExecutionOutcome.CaseChanged);
            if (value.Runtime != runtime) return Refused(RegressionCaseExecutionOutcome.RuntimeMismatch);

            var source = await AdmitAsync(actor, target, cancellationToken).ConfigureAwait(false);
            var bytes = await objects.ReadVerifiedAsync(target.Revision, source.NativeObject, cancellationToken).ConfigureAwait(false);
            var native = NativeArtifact.Admit(target.Revision, bytes, PreviewPolicy.MaximumNativeBytes);
            if (native.Digest != target.NativeDigest || native.ByteCount != source.NativeObject.ByteCount)
                return Refused(RegressionCaseExecutionOutcome.SourceMismatch);
            // Do not clone/rewrite the case: its envelope, original revision and baseline remain unchanged.
            // Only the native artifact dispatched through the existing preview boundary changes.
            var request = new MappingPreviewRequest(native, value.Replay.Target, value.Replay.Primary,
                value.Replay.NamedInputs, value.Replay.Parameters, value.Replay.CurrentDateTime, value.Replay.Limits);

            // Storage can be slow. Never reuse the admission from before private-object I/O for dispatch.
            if (await AdmitAsync(actor, target, cancellationToken).ConfigureAwait(false) != source)
                return Refused(RegressionCaseExecutionOutcome.SourceMismatch);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await backend.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RegressionOutputSet outputs;
            try { outputs = RegressionPreviewOutputs.Read(value, target, result, cancellationToken); }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or RegressionCaseException or RegressionComparisonException)
            {
                return Refused(RegressionCaseExecutionOutcome.ResultRejected);
            }

            var status = result.Status == MappingPreviewStatus.Succeeded
                ? Weft.Contracts.Operations.OperationStatusV1.Succeeded : Weft.Contracts.Operations.OperationStatusV1.BackendFailure;
            var comparison = new RegressionComparisonService().Compare(value.Expected, new(status, outputs),
                value.ComparisonPolicy, cancellationToken: cancellationToken);

            // Neither output bytes nor comparison facts escape after authority/source has changed.
            if (await AdmitAsync(actor, target, cancellationToken).ConfigureAwait(false) != source)
                return Refused(RegressionCaseExecutionOutcome.SourceMismatch);
            cancellationToken.ThrowIfCancellationRequested();
            return RegressionCaseExecution.Complete(result.Status, outputs, comparison);
        }
        catch (ExecutionRefusal refusal) { return Refused(refusal.Outcome); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No raw storage, worker or payload-derived exception text crosses the case boundary.
            return Refused(RegressionCaseExecutionOutcome.InfrastructureFailure);
        }

        RegressionCaseExecution Refused(RegressionCaseExecutionOutcome outcome)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RegressionCaseExecution.Refused(outcome);
        }
    }

    private async Task<RevisionReportSource> AdmitAsync(ActorContext actor, RegressionEvaluationTarget value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = new ProjectScope(value.Revision.OrganizationId, value.Revision.ProjectId);
        var access = await projects.GetAccessAsync(actor, scope, token).ConfigureAwait(false);
        if (access is null) throw new ExecutionRefusal(RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
        if (access.Scope != scope || access.PrincipalId != actor.PrincipalId)
            throw new ExecutionRefusal(RegressionCaseExecutionOutcome.SourceMismatch);
        if (!access.Can(ProjectAction.SubmitPreview))
            throw new ExecutionRefusal(RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
        var observation = await sources.GetSourceAsync(actor, value.Revision, token).ConfigureAwait(false);
        if (observation is null) throw new ExecutionRefusal(RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
        if (observation.Actor != actor.PrincipalId || observation.Source.Scope != value.Revision ||
            observation.Source.NativeObject.Digest != value.NativeDigest || observation.Source.SourceBackendId != runtime.BackendId ||
            observation.Source.LogicalDocument != IngestionIntent.EntryDocument || observation.Source.DependencyIdentity != IngestionIntent.EmptyDependencyIdentity)
            throw new ExecutionRefusal(RegressionCaseExecutionOutcome.SourceMismatch);
        // The source read is a separate asynchronous operation: refresh the role after it too.
        access = await projects.GetAccessAsync(actor, scope, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (access is null || !access.Can(ProjectAction.SubmitPreview))
            throw new ExecutionRefusal(RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
        if (access.Scope != scope || access.PrincipalId != actor.PrincipalId)
            throw new ExecutionRefusal(RegressionCaseExecutionOutcome.SourceMismatch);
        return observation.Source;
    }

    private sealed class ExecutionRefusal(RegressionCaseExecutionOutcome outcome) : Exception
    {
        internal RegressionCaseExecutionOutcome Outcome { get; } = outcome;
    }
}
