using Weft.Application;
using Weft.Domain;
using static Weft.Tests.PrivateObjectMaintenanceContractFacts;

namespace Weft.Tests;

/// <summary>Authored forwarding/fault ports only; no database, filesystem operation or expiry is qualified here.</summary>
internal sealed class PrivateObjectMaintenanceServiceTestPorts : IPrivateObjectMaintenanceMetadata,
    IPrivateObjectMaintenanceStatusReader, IPrivateObjectPublicationObservationReader,
    IPrivateObjectStorageObserver, IPrivateObjectRetirementStore
{
    internal sealed record FinishCall(ObjectCleanupClaim Claim, ObjectCleanupDisposition Disposition,
        CancellationToken Work, CancellationToken Settlement);
    internal sealed record GetCall(ProjectScope Project, Guid Retirement, long Generation, CancellationToken Token);
    internal sealed record RetireCall(ObjectCleanupClaim Claim, CancellationToken Token);

    internal List<string> Calls { get; } = [];
    internal List<FinishCall> Finishes { get; } = [];
    internal List<GetCall> Gets { get; } = [];
    internal List<RetireCall> Retirements { get; } = [];
    internal ObjectCleanupStatusPage Statuses { get; set; } = new(Project, RetiredAt, []);
    internal PrivateStorageObservationPage Storage { get; set; } = new(Project, Id(10), Id(11), [], true, true, false, 0);
    internal ObjectCleanupClaimBatch Batch { get; set; } = new(Project, [], 0, 0, 0);
    internal Func<ProjectScope, ObjectCleanupStatusCursor?, CancellationToken, Task<ObjectCleanupStatusPage>>? OnStatus { get; set; }
    internal Func<ProjectScope, CancellationToken, Task<PrivateStorageObservationPage>>? OnObserve { get; set; }
    internal Func<PublicationObservationRequest, CancellationToken, Task<PrivatePublicationObservationBatch>>? OnMatch { get; set; }
    internal Func<ProjectScope, Guid, ObjectCleanupScanCursor?, CancellationToken, Task<ObjectCleanupClaimBatch>>? OnClaim { get; set; }
    internal Func<ObjectCleanupClaim, CancellationToken, Task<PrivateObjectRetirementOutcome>>? OnRetire { get; set; }
    internal Func<FinishCall, Task<ObjectCleanupFinishResult>>? OnFinish { get; set; }
    internal Func<GetCall, Task<ObjectCleanupAttempt?>>? OnGet { get; set; }

    internal PrivateObjectMaintenanceService Service(TimeProvider? time = null, Guid? instance = null) =>
        new(this, this, this, this, this, instance ?? Id(20), time ?? new MaintenanceServiceTestTimeProvider());

    internal void SetClaims(params ObjectCleanupClaim[] claims) => Batch = new(Project, claims, claims.Length, 0, 0);

    public Task<ObjectCleanupStatusPage> GetStatusesAsync(ProjectScope project, ObjectCleanupStatusCursor? after = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("status");
        return OnStatus?.Invoke(project, after, cancellationToken) ?? Task.FromResult(Statuses);
    }

    public Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project, CancellationToken cancellationToken = default)
    {
        Calls.Add("storage");
        return OnObserve?.Invoke(project, cancellationToken) ?? Task.FromResult(Storage);
    }

    public Task<PrivatePublicationObservationBatch> MatchAsync(PublicationObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("match");
        return OnMatch?.Invoke(request, cancellationToken) ?? Task.FromResult(new PrivatePublicationObservationBatch(request,
            RetiredAt, request.Slots.Select(slot => new PublicationSlotObservation(slot.Ordinal, PublicationObservationOutcome.NoMatch, []))));
    }

    public Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid serverInstanceId,
        ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("claim");
        return OnClaim?.Invoke(project, serverInstanceId, after, cancellationToken) ?? Task.FromResult(Batch);
    }

    public Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim, CancellationToken cancellationToken = default)
    {
        Calls.Add("retire");
        Retirements.Add(new(claim, cancellationToken));
        return OnRetire?.Invoke(claim, cancellationToken) ?? Task.FromResult(PrivateObjectRetirementOutcome.Deleted);
    }

    public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("The legacy overload must not be used by the command.");

    public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken, CancellationToken settlementCancellationToken)
    {
        Calls.Add("finish");
        var call = new FinishCall(claim, disposition, cancellationToken, settlementCancellationToken);
        Finishes.Add(call);
        return OnFinish?.Invoke(call) ?? Task.FromResult(ObjectCleanupFinishResult.ForAttempt(ObjectCleanupFinishOutcome.Applied,
            new ObjectCleanupAttempt(claim, claim.StartedAtUtc, disposition)));
    }

    public Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("get");
        var call = new GetCall(project, retirementId, generation, cancellationToken);
        Gets.Add(call);
        return OnGet?.Invoke(call) ?? Task.FromResult<ObjectCleanupAttempt?>(null);
    }
}
