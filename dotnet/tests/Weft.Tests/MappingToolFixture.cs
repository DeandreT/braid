using Weft.Ai;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// Independent, manually authored neutral reports and a finite scripted authority model.
/// These fixtures test contract composition, not PostgreSQL ACLs, native execution or stored-byte integrity.
/// No unconditional allow-all substitute is used: actor, complete scope, current role/admission and selectors must match.
/// </summary>
internal sealed class MappingToolFixture : IRevisionReportReader, IRevisionReportMetadataReader
{
    internal static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"), Guid.Parse("44444444-4444-4444-8444-444444444444"));
    internal static readonly ActorContext Actor = new(new(Guid.Parse("55555555-5555-4555-8555-555555555555")));
    internal static readonly PrincipalId OriginalDeriver = new(Guid.Parse("56565656-5656-4565-8565-565656565656"));
    internal static readonly RevisionReportBackendProfile Profile = new("neutral", "engine-1", "build-1", "adapter-1");
    internal static readonly DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    internal const string PrivateText = "private-neutral-literal";
    internal RevisionReportSource Source { get; } = new(Scope,
        new(PrivateObjectId.Restore(Scope, Guid.Parse("66666666-6666-4666-8666-666666666666"), PrivateObjectClass.NativeArtifact),
            Sha256Digest.FromBytes([1, 2, 3]), 3), IngestionIntent.EntryDocument, "neutral-dependencies", Profile.BackendId);
    internal VerifiedProjectionReport Projection { get; }
    internal VerifiedValidationReport Validation { get; }
    internal ChatConversationIntent Conversation { get; }
    internal bool Active { get; set; } = true;
    internal bool Admitted { get; set; } = true;
    internal ProjectRole? Role { get; set; } = ProjectRole.Viewer;
    internal bool Missing { get; set; }
    internal RevisionReportReadOutcome? ReadFailure { get; set; }
    internal bool WrongActor { get; set; }
    internal RevisionReportResult? SubstitutedProjectionMetadata { get; set; }
    internal Action<int>? OnAuthority { get; set; }
    internal Action<CancellationToken>? OnRead { get; set; }
    internal int AuthorityCalls { get; private set; }
    internal List<(RevisionReportOperation Operation, Guid Id, Guid? ProjectionId)> Reads { get; } = [];

    internal MappingToolFixture(bool partial = true, string? largeSummary = null, bool deepDetails = false)
    {
        var key = new ProjectionCacheKey(Source, Profile, "weft.mapping-projection.v1", "neutral.project.1", "neutral.codec.1", new());
        var contractScope = new ScopedRevisionId(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"),
            Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D"));
        var digest = new ArtifactDigest("sha256", Source.NativeObject.Digest.Value);
        var backend = new BackendIdentity(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild);
        var provenance = new OperationProvenanceV1(contractScope, digest, backend, Profile.AdapterBuild, true);
        var type = new FieldDataType(FieldDataTypeKind.Scalar, R(ScalarDataType.String));
        var input = new ProjectionField("field.in", "in", R<PathSegment>(new FieldPathSegment("a.b", "urn:source"),
            new AttributePathSegment("id", "urn:attribute"), new IndexPathSegment("9007199254740993")),
            "Incoming", type, new(CardinalityKind.Unknown), FieldRequirement.Unknown, FieldNullability.Unknown, Origin("input"));
        var output = new ProjectionField("field.out", "out", R<PathSegment>(new FieldPathSegment("total")),
            "Total", type, new(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, Origin("output"));
        var downstream = output with { Id = "field.downstream", Name = "Downstream", NativeLocator = Origin("downstream") };
        var dependencies = R<RuleDependency>(new FieldDependency(input.Id),
            new ParameterDependency("parameter", DependencyRole.Default),
            new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "-0.000E+07"), DependencyRole.Default));
        var details = R<DetailNode>(new LiteralDetail(new StringLiteralValue(PrivateText)),
            new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993")),
            new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000")),
            new LiteralDetail(new NullLiteralValue(NullLiteralKind.Absent)),
            new LiteralDetail(new NullLiteralValue(NullLiteralKind.ExplicitNull)),
            new LiteralDetail(new NullLiteralValue(NullLiteralKind.XmlNil)),
            new UnsupportedDetail("extension", "Not projected", Origin("extension")));
        if (deepDetails)
        {
            DetailNode nested = new LiteralDetail(new StringLiteralValue(PrivateText));
            for (var index = 0; index < 8; index++) nested = new GroupDetail("nested", R(nested));
            details = R(nested);
        }
        var rule = new ProjectionRule("rule.first", output.Id, largeSummary ?? "Choose incoming value", dependencies, details, Origin("first"));
        var second = new ProjectionRule("rule.second", downstream.Id, "Read total", R<RuleDependency>(new FieldDependency(output.Id)),
            R<DetailNode>(), Origin("second"));
        var control = new ProjectionControl("control.filter", ControlKind.Filter, "Only accepted records",
            R<ProjectionNodeReference>(new RuleNodeReference(rule.Id)),
            R<RuleDependency>(new FieldDependency(input.Id, DependencyRole.Condition)), R<DetailNode>(), Origin("filter"));
        var gaps = partial ? R(new CoverageGap("unsupported", "Unknown extension behavior", Origin(deepDetails ? "first" : "extension"), new RuleNodeReference(rule.Id))) : R<CoverageGap>();
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, provenance,
            new(new(contractScope, digest, backend, Profile.AdapterBuild, key.AlgorithmVersion),
                new(partial ? CoverageStatus.Partial : CoverageStatus.Complete, gaps),
                R(new ProjectionBoundary("in", "Input", BoundaryRole.PrimaryInput, "neutral", BoundaryShape.Rows, null, Origin("in")),
                    new ProjectionBoundary("out", "Output", BoundaryRole.PrimaryOutput, "neutral", BoundaryShape.Object, null, Origin("out"))),
                R(input, output, downstream),
                R(new ProjectionParameter("parameter", "Fallback", type, FieldRequirement.Optional,
                    new StringLiteralValue("exact fallback"), Origin("parameter"))), R(rule, second), R(control),
                R(new ProjectionRelationship("member.in", RelationshipKind.BoundaryMember, new FieldNodeReference(input.Id), new BoundaryNodeReference("in")),
                    new ProjectionRelationship("member.out", RelationshipKind.BoundaryMember, new FieldNodeReference(output.Id), new BoundaryNodeReference("out")),
                    new ProjectionRelationship("member.downstream", RelationshipKind.BoundaryMember, new FieldNodeReference(downstream.Id), new BoundaryNodeReference("out")),
                    new ProjectionRelationship("write.first", RelationshipKind.WritesTo, new RuleNodeReference(rule.Id), new FieldNodeReference(output.Id)),
                    new ProjectionRelationship("write.second", RelationshipKind.WritesTo, new RuleNodeReference(second.Id), new FieldNodeReference(downstream.Id)),
                    new ProjectionRelationship("read.first", RelationshipKind.ReadsFrom, new RuleNodeReference(rule.Id), new FieldNodeReference(input.Id)),
                    new ProjectionRelationship("read.second", RelationshipKind.ReadsFrom, new RuleNodeReference(second.Id), new FieldNodeReference(output.Id)),
                    new ProjectionRelationship("read.control", RelationshipKind.ReadsFrom, new ControlNodeReference(control.Id), new FieldNodeReference(input.Id)),
                    new ProjectionRelationship("default.parameter", RelationshipKind.DependsOn, new RuleNodeReference(rule.Id), new ParameterNodeReference("parameter")),
                    new ProjectionRelationship("govern.first", RelationshipKind.GovernedBy, new RuleNodeReference(rule.Id), new ControlNodeReference(control.Id))), R<ProjectionDiagnostic>(),
                R(new OperationCapability(MappingOperation.Project, CapabilityStatus.Conditional, "neutral",
                    R("in", "out"), R(new CapabilityCondition("metadataOnly", "Explanation, not execution"))))), R<OperationDiagnosticV1>());
        var observation = RevisionReportProjectionValidation.Validate(key, report, new());
        var metadata = Metadata(key, report, observation, Guid.Parse("77777777-7777-4777-8777-777777777777"),
            Guid.Parse("99999999-9999-4999-8999-999999999999"));
        Projection = VerifiedProjectionReport.Create(metadata, report, new());

        var validationKey = new ValidationCacheKey(Source, Profile, "weft.mapping-validation.v1", "neutral.validate.1", "neutral.codec.1", new());
        var validation = new ValidationReportV1(OperationStatusV1.InvalidMapping, provenance, false, 3, 2, true,
            R(new ValidationDiagnosticV1("invalid.first", DiagnosticSeverity.Error, PrivateText, false, Origin("first")),
                new ValidationDiagnosticV1("invalid.second", DiagnosticSeverity.Warning, "Second issue", false, Origin("second"))));
        var validationObservation = RevisionReportValidationValidation.Validate(validationKey, validation, new());
        Validation = VerifiedValidationReport.Create(Metadata(validationKey, validation, validationObservation,
            Guid.Parse("88888888-8888-4888-8888-888888888888"), Guid.Parse("9999aaaa-9999-4999-8999-999999999999")), validation, new());
        Conversation = ConversationWith();
    }

    internal ChatConversationIntent ConversationWith(RevisionScope? scope = null, PrincipalId? creator = null,
        ChatReportPin? projection = null, ChatReportPin? validation = null) =>
        new(new(scope ?? Scope, creator ?? Actor.PrincipalId, Guid.Parse("abababab-abab-4bab-8bab-abababababab")),
            projection ?? MappingToolService.Pin(Projection.Metadata), validation ?? MappingToolService.Pin(Validation.Metadata),
            Now.AddDays(30));

    /// <summary>A real turn whose grounding policy pins the exact mapping tool manifest.</summary>
    internal static ChatTurnIntent Turn(int maxNodes = 1_024, int maxResultBytes = 32_768)
    {
        var budget = new ChatProviderBudget(600_000_000, 4, 1_048_576, 4_194_304, 131_072, 8, 512);
        var grounding = ChatAiBridge.PinGroundingPolicy("mapping-tools", 1, "Explain the pinned mapping only.",
            MappingToolRegistry.Definitions, maxNodes, maxResultBytes, 3);
        var digest = Sha256Digest.FromBytes([4, 5, 6]);
        return new(Guid.Parse("cdcdcdcd-cdcd-4dcd-8dcd-cdcdcdcdcdcd"), new(Guid.Parse("efefefef-efef-4fef-8fef-efefefefefef"),
            "Explain the mapping", [], budget, grounding), new("fixture", 1, "fixture", "fixture-model", "mapping-metadata", 1, digest),
            new(256, 262_144, 4), Now.AddMinutes(1));
    }

    internal ChatReadOnlyToolContext Context(ActorContext? actor = null, ChatConversationIntent? conversation = null,
        int remainingBytes = 32_768, int remainingNodes = 1_024) =>
        new(actor ?? Actor, conversation ?? Conversation, Turn(), Guid.Parse("12121212-1212-4212-8212-121212121212"), 1,
            remainingBytes, remainingNodes);

    internal static AiToolIntent Call(string name, string arguments) =>
        AiToolIntent.Create("call-1", name, AiJsonObject.CopyFrom(arguments));

    internal MappingToolService Service(MappingToolLimits? limits = null) => new(this, this, limits);

    internal Task<MappingToolReply> InvokeAsync(MappingToolService service, string name, string arguments,
        ChatReadOnlyToolContext? context = null, CancellationToken cancellationToken = default) =>
        service.InvokeAsync(context ?? Context(), Call(name, arguments), cancellationToken);

    internal Task<MappingToolReply> InvokeAsync(string name, string arguments) => InvokeAsync(Service(), name, arguments);

    internal static NativeLocator Origin(string node) => new GraphNodeNativeLocator("neutral.mapping", node, "function.one");
    internal static IReadOnlyList<T> R<T>(params T[] items) => Array.AsReadOnly(items);

    private static RevisionReportResult Metadata<T>(RevisionReportKey key, T report, RevisionReportObservation observation,
        Guid derivationId, Guid cacheEntryId) where T : notnull
    {
        // Synthetic report bytes define fixture metadata only. They are not an on-disk envelope or production receipt.
        var bytes = RevisionReportResponseSerializer.Serialize(report, 1_048_576);
        return new(derivationId, cacheEntryId, key, 1, OriginalDeriver,
            Now, Now.AddSeconds(1), new(PrivateObjectId.Restore(Scope, derivationId, PrivateObjectClass.DerivedArtifact),
                Sha256Digest.FromBytes(bytes), bytes.Length), observation);
    }

    private bool Allowed(ActorContext actor, RevisionScope scope) => actor == Actor && scope == Scope && Active && Admitted &&
        Role is { } role && ProjectPermissions.Can(role, ProjectAction.ReadMetadata);

    public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Count before the optional hook: a null-conditional call would skip evaluating its argument.
        AuthorityCalls++;
        OnAuthority?.Invoke(AuthorityCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<AuthorizedRevisionReportSource?>(Allowed(actor, scope)
            ? new AuthorizedRevisionReportSource(Source, actor.PrincipalId, Now) : null);
    }

    public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add((operation, derivationId, projectionDerivationId));
        OnRead?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Allowed(actor, scope) || Missing) return Task.FromResult(RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.NotAuthorizedOrNotFound));
        var observedActor = WrongActor ? new PrincipalId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")) : actor.PrincipalId;
        var source = new AuthorizedRevisionReportSource(Source, observedActor, Now);
        VerifiedRevisionReport selected;
        if (operation == RevisionReportOperation.Projection && derivationId == Projection.Metadata.DerivationId && projectionDerivationId is null)
        {
            selected = SubstitutedProjectionMetadata is { } replacement
                ? VerifiedProjectionReport.Create(replacement, Projection.Report, new()) : Projection;
        }
        else if (operation == RevisionReportOperation.Validation && derivationId == Validation.Metadata.DerivationId &&
            (projectionDerivationId is null || projectionDerivationId == Projection.Metadata.DerivationId))
        {
            var value = Validation.Report;
            if (projectionDerivationId is not null)
            {
                var links = ProjectionOriginLinks.FromVerifiedReport(Projection, new());
                value = value with
                {
                    Diagnostics = Array.AsReadOnly(value.Diagnostics.Select(item =>
                    item with { ProjectionLocator = links.Find(item.NativeLocator) }).ToArray())
                };
            }
            selected = VerifiedValidationReport.Create(Validation.Metadata, value, new(), projectionDerivationId);
        }
        else return Task.FromResult(RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.NotAuthorizedOrNotFound));
        return Task.FromResult(ReadFailure is { } failure
            ? RevisionReportReadResult.ForReadFailure(source, selected.Metadata, failure)
            : RevisionReportReadResult.ForAvailable(source, selected));
    }

    public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Current report selection is forbidden in pinned tools.");
    public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Current report metadata is forbidden in pinned tools.");
    public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid derivationId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Only fresh source authority is requested through this port.");
}
