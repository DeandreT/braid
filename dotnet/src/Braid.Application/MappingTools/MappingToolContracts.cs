using System.Text.Json.Serialization;
using Braid.Ai;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application.MappingTools;

public enum MappingToolOutcome
{
    Available, Unknown, InvalidCall, NotAuthorizedOrNotFound, ReportUnavailable,
    ResourceLimit, Busy, TimedOut, DependencyFailure
}

public sealed class MappingToolLimits
{
    public MappingToolLimits(int workUnits = 100_000, int outputBytes = 24_576, int resultItems = 20,
        int lineageNodes = 64, int citationCount = 64, TimeSpan? duration = null)
    {
        var effectiveDuration = duration ?? TimeSpan.FromSeconds(10);
        if (workUnits is < 1 or > 1_000_000 || outputBytes is < 1_024 or > 32_768 ||
            resultItems is < 1 or > 20 || lineageNodes is < 1 or > 64 || citationCount is < 1 or > 64 ||
            effectiveDuration < TimeSpan.FromMilliseconds(1) || effectiveDuration > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid mapping tool limits.");
        WorkUnits = workUnits;
        OutputBytes = outputBytes;
        ResultItems = resultItems;
        LineageNodes = lineageNodes;
        CitationCount = citationCount;
        Duration = effectiveDuration;
    }

    public int WorkUnits { get; }
    public int OutputBytes { get; }
    public int ResultItems { get; }
    public int LineageNodes { get; }
    public int CitationCount { get; }
    public TimeSpan Duration { get; }
    public override string ToString() => nameof(MappingToolLimits);
}

/// <summary>Result data is explanation evidence only, never execution or preview evidence.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MappingSummaryData), "mappingSummary")]
[JsonDerivedType(typeof(MappingSearchData), "search")]
[JsonDerivedType(typeof(MappingFieldLineageData), "fieldLineage")]
[JsonDerivedType(typeof(MappingRuleData), "ruleDetails")]
[JsonDerivedType(typeof(MappingValidationData), "validationIssues")]
public abstract record MappingToolData
{
    public sealed override string ToString() => nameof(MappingToolData);
}

public sealed record MappingSummaryData(
    IReadOnlyList<ProjectionBoundary> Boundaries,
    IReadOnlyList<ProjectionControl> Controls,
    IReadOnlyList<OperationCapability> Capabilities,
    IReadOnlyList<ProjectionDiagnostic> Diagnostics,
    IReadOnlyList<OperationDiagnosticV1> OperationDiagnostics,
    int FieldCount, int RuleCount, int ParameterCount) : MappingToolData;

public sealed record MappingSearchHit(ProjectionNodeReference Node, string Label,
    IReadOnlyList<PathSegment>? Path, NativeLocator NativeLocator)
{
    public override string ToString() => nameof(MappingSearchHit);
}

public sealed record MappingSearchData(IReadOnlyList<MappingSearchHit> Hits, bool Truncated) : MappingToolData;

public enum MappingLineageEdgeKind { ProducedBy, RuleDependency, ControlDependency, ParameterDefault, GovernedBy, Relationship }

/// <summary>Edges point from a dependent to its dependency; constants have no node target.</summary>
public sealed record MappingLineageEdge(ProjectionNodeReference From, ProjectionNodeReference? To,
    MappingLineageEdgeKind Kind, RuleDependency? Dependency = null, ProjectionRelationship? Relationship = null)
{
    public override string ToString() => nameof(MappingLineageEdge);
}

public sealed record MappingFieldLineageData(ProjectionField Field,
    IReadOnlyList<ProjectionNodeReference> UpstreamNodes, IReadOnlyList<MappingLineageEdge> UpstreamEdges,
    IReadOnlyList<ProjectionNodeReference> DownstreamNodes, IReadOnlyList<MappingLineageEdge> DownstreamEdges,
    IReadOnlyList<FieldNodeReference> AffectedOutputs, bool Truncated) : MappingToolData;

public sealed record MappingRuleData(ProjectionRule Rule, IReadOnlyList<ProjectionControl> Controls) : MappingToolData;

public sealed record MappingValidationIssue(int Ordinal, ValidationDiagnosticV1 Diagnostic)
{
    public override string ToString() => nameof(MappingValidationIssue);
}

public sealed record MappingValidationData(OperationStatusV1 Status, bool? IsValid, int TotalIssueCount,
    int EmittedIssueCount, bool ReportTruncated, int Offset, bool WindowTruncated,
    IReadOnlyList<MappingValidationIssue> Issues) : MappingToolData;

/// <summary>Typed view of one tool result. Only <see cref="Result"/> crosses into provider history.</summary>
public sealed class MappingToolReply
{
    internal MappingToolReply(MappingToolOutcome outcome, ProjectionCoverage? coverage, MappingToolData? data,
        IReadOnlyList<ChatCitationTarget> citations, AiJsonObject result, int retrievedNodes)
    {
        Outcome = outcome; Coverage = coverage; Data = data; Citations = citations;
        Result = result; RetrievedNodes = retrievedNodes;
    }

    public MappingToolOutcome Outcome { get; }
    /// <summary>Null means unknown, not complete.</summary>
    public ProjectionCoverage? Coverage { get; }
    public MappingToolData? Data { get; }
    /// <summary>Unverified citation candidates; they become citations only through ValidateCitationsAsync.</summary>
    public IReadOnlyList<ChatCitationTarget> Citations { get; }
    /// <summary>The complete bounded envelope. It is never cropped to fit a budget.</summary>
    public AiJsonObject Result { get; }
    public int RetrievedNodes { get; }
    public override string ToString() => $"{nameof(MappingToolReply)}:{Outcome}";
}

public enum MappingCitationOutcome { Valid, Invalid, NotAuthorizedOrNotFound, ReportUnavailable, ResourceLimit, Busy, TimedOut, DependencyFailure }

/// <summary>Only the owned citations on a Valid check may be persisted or rendered.</summary>
public sealed class MappingCitationCheck
{
    internal MappingCitationCheck(MappingCitationOutcome outcome, IReadOnlyList<ChatValidatedCitation>? citations = null)
    { Outcome = outcome; Citations = citations ?? Array.Empty<ChatValidatedCitation>(); }
    public MappingCitationOutcome Outcome { get; }
    public IReadOnlyList<ChatValidatedCitation> Citations { get; }
    public override string ToString() => $"{nameof(MappingCitationCheck)}:{Outcome}";
}

/// <summary>
/// One revision and the exact retained reports the five tools read: a revision conversation's pins, or the
/// current retained reports a workspace tool observed. A null validation pin makes only the validation tool unavailable.
/// </summary>
public sealed record MappingReportContext
{
    public MappingReportContext(RevisionScope revision, ChatReportPin projection, ChatReportPin? validation)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(projection);
        if (validation is not null && validation.DerivationId == projection.DerivationId) throw ChatBounds.Invalid();
        Revision = revision; Projection = projection; Validation = validation;
    }
    public RevisionScope Revision { get; }
    public ChatReportPin Projection { get; }
    public ChatReportPin? Validation { get; }
    public static MappingReportContext Of(ChatConversationIntent conversation) =>
        new(conversation.Key.PinnedRevision, conversation.PinnedProjection, conversation.PinnedValidation);
    public override string ToString() => nameof(MappingReportContext);
}
