namespace Weft.Contracts.Creation;

public sealed record CreationProposalLinkV1(string ProposalId, string Fingerprint, string ReviewPath);
public sealed record CreationProposalPageV1(CreationProposalLinkV1[] Proposals, string? AfterProposal);

public sealed record CreationErrorV1(string Code);
public sealed record CreationApprovalV1(string Fingerprint, IReadOnlyList<string> AcceptedCriticalAssumptions);
public sealed record CreationRecoveryV1(string Fingerprint);
public sealed record CreationPreviewRequestV1(string Fingerprint, string SampleBase64, string SampleDigest);
public sealed record CreationAssumptionV1(string Id, string Text, bool Critical);
public sealed record CreationQuestionV1(string Text, bool Critical);
public sealed record CreationCitationV1(string BundleId, string SourceId, long SourceVersion, int ChunkOrdinal);
public sealed record CreationReceiptV1(string OperationId, string Phase, string? MappingId, string? RevisionId);
public sealed record CreationProposalV1(string ProjectId, string ProposalId, string Fingerprint, string SnapshotDigest,
    string PlanDigest, string NativeDigest, string Name, string? Description, string PlanJson, bool Approved,
    IReadOnlyList<CreationCitationV1> Evidence, IReadOnlyList<CreationAssumptionV1> Assumptions,
    IReadOnlyList<CreationQuestionV1> Questions, CreationReceiptV1? Receipt);
public sealed record CreationPreviewV1(string ProposalId, string Fingerprint, string NativeDigest, string SampleDigest,
    string Outcome, string? Format, string? MediaType, string? OutputDigest, string? OutputBase64);
