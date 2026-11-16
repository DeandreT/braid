namespace Braid.Contracts.Catalog;

/// <summary>Safe original-actor receipt facts. Future scopes do not establish that a revision was committed.</summary>
public sealed record CatalogRevisionIngestionReceiptV1
{
    public CatalogRevisionIngestionReceiptV1(string operationId, CatalogProjectScopeV1 project,
        CatalogMappingScopeV1 futureMapping, CatalogRevisionScopeV1 futureRevision,
        CatalogRevisionIngestionPhaseV1 phase, int claimCount, DateTimeOffset reservedAtUtc,
        DateTimeOffset? completedAtUtc, string originalFilename, string? committedSequence,
        CatalogAdmissionV1? committedAdmission, CatalogRevisionIngestionFailureV1? latestFailure)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(futureMapping);
        ArgumentNullException.ThrowIfNull(futureRevision);
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), "The ingestion phase must be known.");
        if (claimCount is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(claimCount), "Claim count must fit the ingestion claim budget.");
        if (latestFailure is { } failure && !Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(latestFailure), "The ingestion failure must be known.");
        if (project.OrganizationId != futureMapping.OrganizationId || project.ProjectId != futureMapping.ProjectId ||
            futureMapping.OrganizationId != futureRevision.OrganizationId || futureMapping.ProjectId != futureRevision.ProjectId ||
            futureMapping.MappingId != futureRevision.MappingId)
            throw new ArgumentException("Receipt scopes must preserve the complete project and mapping ancestry.");

        var terminal = phase is CatalogRevisionIngestionPhaseV1.Committed or CatalogRevisionIngestionPhaseV1.Rejected or
            CatalogRevisionIngestionPhaseV1.HeadConflict or CatalogRevisionIngestionPhaseV1.AttemptExhausted;
        if (terminal != (completedAtUtc is not null) ||
            (completedAtUtc is { } completed && completed < reservedAtUtc))
            throw new ArgumentException("Receipt completion must agree with its phase and reservation time.", nameof(completedAtUtc));
        var committed = phase == CatalogRevisionIngestionPhaseV1.Committed;
        if ((committedSequence is not null) != committed || (committedAdmission is not null) != committed)
            throw new ArgumentException("Only a committed receipt has its sequence and admission together.");
        if (claimCount == 0 && (phase != CatalogRevisionIngestionPhaseV1.Reserved || latestFailure is not null))
            throw new ArgumentException("Only an unclaimed reserved receipt can have no attempts.", nameof(claimCount));
        if (phase == CatalogRevisionIngestionPhaseV1.AttemptExhausted && claimCount != 8)
            throw new ArgumentException("Attempt exhaustion requires the complete claim budget.", nameof(claimCount));
        if ((phase is CatalogRevisionIngestionPhaseV1.Committed or CatalogRevisionIngestionPhaseV1.HeadConflict) && latestFailure is not null)
            throw new ArgumentException("An eligible terminal receipt cannot report an operational failure.", nameof(latestFailure));
        if ((phase == CatalogRevisionIngestionPhaseV1.Rejected && latestFailure is not null && latestFailure != CatalogRevisionIngestionFailureV1.EvidenceLimit) ||
            (latestFailure == CatalogRevisionIngestionFailureV1.EvidenceLimit && phase != CatalogRevisionIngestionPhaseV1.Rejected))
            throw new ArgumentException("An evidence limit belongs to the actual rejected receipt.", nameof(latestFailure));
        if (committedAdmission is { } admission)
            CatalogValueV1.RequireAdmission(admission);

        OperationId = CatalogValueV1.RequireId(operationId, nameof(operationId));
        Project = project;
        FutureMapping = futureMapping;
        FutureRevision = futureRevision;
        Phase = phase;
        ClaimCount = claimCount;
        ReservedAtUtc = reservedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc?.ToUniversalTime();
        OriginalFilename = CatalogValueV1.RequireFilename(originalFilename);
        CommittedSequence = committedSequence is { } sequence ? CatalogValueV1.RequirePositiveDecimal(sequence, nameof(committedSequence)) : null;
        CommittedAdmission = committedAdmission;
        LatestFailure = latestFailure;
    }

    public string OperationId { get; }
    public CatalogProjectScopeV1 Project { get; }
    public CatalogMappingScopeV1 FutureMapping { get; }
    public CatalogRevisionScopeV1 FutureRevision { get; }
    public CatalogRevisionIngestionPhaseV1 Phase { get; }
    public int ClaimCount { get; }
    public DateTimeOffset ReservedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string OriginalFilename { get; }
    public string? CommittedSequence { get; }
    public CatalogAdmissionV1? CommittedAdmission { get; }
    public CatalogRevisionIngestionFailureV1? LatestFailure { get; }
}
