using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Deployment;

public sealed record BuildEnvironment(BuildEnvironmentReference Reference, BuildRuntimeProfile Profile, bool Active);
public sealed record BuildApproval(PrincipalId Actor, BuildIntent Intent, PrivateObjectReference Native, Sha256Digest EvidenceDigest);
public enum BuildControlOutcome { Created, Replayed, Conflict, Refused }

public interface IBuildControlStore : IBuildEnvironmentCommands, IBuildApprovalRevocation
{
    Task<BuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default);
    Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildApproval approval, CancellationToken token = default);
    Task<BuildApproval?> ReadApprovalAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default);
}

public interface IBuildApprovalWriter
{
    Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default);
}

/// <summary>Combines fresh persisted controls, current native identity and recomputed saved-case evidence.</summary>
public sealed class BuildControlService(IBuildControlStore controls, IProjectReader projects,
    IRevisionReportMetadataReader sources, RegressionRunReadService runs) : IBuildAdmissionReader, IBuildApprovalWriter
{
    public async Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
    {
        var evidence = await EvidenceAsync(actor, intent, token).ConfigureAwait(false);
        if (evidence is null) return BuildControlOutcome.Refused;
        var approval = new BuildApproval(actor.PrincipalId, intent, evidence.Value.Native, evidence.Value.Digest);
        return await controls.ApproveAsync(actor, approval, token).ConfigureAwait(false);
    }

    public async Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
    {
        var approval = await controls.ReadApprovalAsync(actor, intent, token).ConfigureAwait(false);
        if (approval is null || approval.Actor != actor.PrincipalId || approval.Intent.Fingerprint != intent.Fingerprint) return null;
        var evidence = await EvidenceAsync(actor, intent, token).ConfigureAwait(false);
        if (evidence is null || evidence.Value.Native != approval.Native || evidence.Value.Digest != approval.EvidenceDigest) return null;
        var fresh = await controls.ReadApprovalAsync(actor, intent, token).ConfigureAwait(false);
        return fresh is not null && fresh.Actor == approval.Actor && fresh.Intent.Fingerprint == approval.Intent.Fingerprint &&
            fresh.Native == approval.Native && fresh.EvidenceDigest == approval.EvidenceDigest
            ? new(actor.PrincipalId, intent.Fingerprint, approval.Native) : null;
    }

    private async Task<(PrivateObjectReference Native, Sha256Digest Digest)?> EvidenceAsync(ActorContext actor, BuildIntent intent, CancellationToken token)
    {
        var access = await projects.GetAccessAsync(actor, intent.Environment.Project, token).ConfigureAwait(false);
        if (access is null || access.PrincipalId != actor.PrincipalId || access.Scope != intent.Environment.Project || !access.Can(ProjectAction.UploadRevision)) return null;
        var environment = await controls.ReadEnvironmentAsync(actor, intent.Environment, token).ConfigureAwait(false);
        if (environment is null || !environment.Active || environment.Reference != intent.Environment || environment.Profile != intent.Profile) return null;
        var source = await sources.GetSourceAsync(actor, intent.Revision, token).ConfigureAwait(false);
        if (source is null || source.Source.Scope != intent.Revision || source.Source.NativeObject.Digest != intent.NativeDigest ||
            source.Source.SourceBackendId != intent.Backend.BackendId) return null;
        var run = await runs.ReadAsync(actor, intent.Revision.OrganizationId, intent.RegressionEvidenceId, token).ConfigureAwait(false);
        if (run?.Result?.Comparison?.Outcome != RegressionComparisonOutcome.Passed || run.Run.Case.Revision != intent.Revision ||
            run.Run.Case.NativeDigest != intent.NativeDigest || run.Run.CompletionDigest is null || run.Runtime is null ||
            run.Runtime.BackendId != intent.Backend.BackendId || run.Runtime.EngineRevision != intent.Backend.EngineRevision ||
            run.Runtime.BackendBuild != intent.Backend.EngineBuild || run.Runtime.AdapterBuild != intent.Backend.AdapterVersion) return null;
        token.ThrowIfCancellationRequested();
        return (source.Source.NativeObject, run.Run.CompletionDigest);
    }
}
