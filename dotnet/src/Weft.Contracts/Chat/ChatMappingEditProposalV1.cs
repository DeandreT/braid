using Weft.Contracts.Catalog;

namespace Weft.Contracts.Chat;

/// <summary>Approval and recovery bind one immutable proposal, never a turn-wide permission.</summary>
public sealed record ChatMappingEditApprovalV1(string ExpectedFingerprint);

/// <summary>Bounded review metadata. Exact operations are available only from the separately authorized operations route.</summary>
public sealed record ChatMappingEditProposalV1(string ProposalId, string Fingerprint, string Status,
    CatalogRevisionScopeV1 BaseRevision, long BaseSequence, int OperationCount, string OperationsDigest,
    IReadOnlyList<ChatMappingChangeV1> Changes, bool ChangesTruncated, string? RevisionId, long? RevisionSequence);

/// <summary>The entire canonical script, at most 1 MiB UTF-8 before JSON string escaping. Never native checkpoint bytes.</summary>
public sealed record ChatMappingEditOperationsV1(string ProposalId, string Fingerprint, string OperationsDigest,
    int OperationCount, string CanonicalScript)
{
    public override string ToString() => nameof(ChatMappingEditOperationsV1);
}

public sealed record ChatMappingEditProposalPageV1(IReadOnlyList<ChatMappingEditProposalV1> Proposals);
