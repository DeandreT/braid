using System.Text;
using Weft.Contracts.Projections;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class BuildPreflightTests
{
    private static readonly Guid Build = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Organization = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Project = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid Mapping = Guid.Parse("40000000-0000-4000-8000-000000000001");
    private static readonly Guid Revision = Guid.Parse("50000000-0000-4000-8000-000000000001");
    private static readonly Guid EnvironmentId = Guid.Parse("60000000-0000-4000-8000-000000000001");
    private static readonly Guid Approval = Guid.Parse("70000000-0000-4000-8000-000000000001");
    private static readonly Guid Regression = Guid.Parse("80000000-0000-4000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("90000000-0000-4000-8000-000000000001");
    private readonly BuildPreflightService service = new();

    [Fact]
    public void Exact_supported_capability_matches_without_executing_anything()
    {
        var intent = Intent();
        var result = service.Evaluate(intent, Capability(intent));
        Assert.Equal(BuildPreflightOutcome.Supported, result.Outcome);
        Assert.Equal(BuildPreflightReason.None, result.Reason);
        Assert.Equal("BuildPreflightResult:Supported:None", result.ToString());
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported, BuildPreflightOutcome.Unsupported)]
    [InlineData(CapabilityStatus.NotQualified, BuildPreflightOutcome.NotQualified)]
    [InlineData(CapabilityStatus.Conditional, BuildPreflightOutcome.ConditionsRequired)]
    public void Unsupported_unqualified_and_conditional_capabilities_cannot_be_promoted(CapabilityStatus status, BuildPreflightOutcome expected)
    {
        var intent = Intent();
        var capability = Capability(intent, status);
        Assert.Equal(expected, service.Evaluate(intent, capability).Outcome);
        AssertFailure(BuildContractFailure.CapabilityUnavailable, () =>
            BuildArtifactReference.Create(intent, capability, Digest(), 1));
    }

    [Fact]
    public void Every_revision_scope_component_and_native_digest_must_match()
    {
        var intent = Intent();
        foreach (var revision in new[]
        {
            new RevisionScope(Other, Project, Mapping, Revision),
            new RevisionScope(Organization, Other, Mapping, Revision),
            new RevisionScope(Organization, Project, Other, Revision),
            new RevisionScope(Organization, Project, Mapping, Other)
        })
        {
            var capability = new BuildCapabilitySnapshot(revision, intent.NativeDigest, intent.Backend,
                intent.Profile, intent.Boundaries, CapabilityStatus.Supported);
            Assert.Equal(BuildPreflightReason.RevisionMismatch, service.Evaluate(intent, capability).Reason);
        }
        var changedDigest = new BuildCapabilitySnapshot(intent.Revision, Sha256Digest.FromBytes([2]), intent.Backend,
            intent.Profile, intent.Boundaries, CapabilityStatus.Supported);
        Assert.Equal(BuildPreflightReason.NativeDigestMismatch, service.Evaluate(intent, changedDigest).Reason);
    }

    [Fact]
    public void Backend_engine_build_and_adapter_identity_are_all_exact()
    {
        var intent = Intent();
        foreach (var backend in new[]
        {
            new BuildBackendIdentity("other", "revision", "engine-build", "adapter"),
            new BuildBackendIdentity("fixture", "other", "engine-build", "adapter"),
            new BuildBackendIdentity("fixture", "revision", "other", "adapter"),
            new BuildBackendIdentity("fixture", "revision", "engine-build", "other")
        })
        {
            var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, backend,
                intent.Profile, intent.Boundaries, CapabilityStatus.Supported);
            Assert.Equal(BuildPreflightReason.BackendMismatch, service.Evaluate(intent, capability).Reason);
        }
    }

    [Fact]
    public void Every_runtime_profile_field_is_qualified_explicitly()
    {
        var intent = Intent();
        foreach (var profile in ChangedProfiles())
        {
            var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend,
                profile, intent.Boundaries, CapabilityStatus.Supported);
            Assert.Equal(BuildPreflightReason.ProfileMismatch, service.Evaluate(intent, capability).Reason);
        }
    }

    [Theory]
    [InlineData("lookup", "Lookup")]
    [InlineData("lookup", " lookup")]
    [InlineData("a/b", "a\\b")]
    [InlineData("é", "e\u0301")]
    public void Logical_boundary_names_are_not_normalized(string left, string right)
    {
        var intent = Intent(boundaries: [new(BuildBoundaryKind.NamedInput, left, "json")]);
        var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend,
            intent.Profile, [new(BuildBoundaryKind.NamedInput, right, "json")], CapabilityStatus.Supported);
        Assert.Equal(BuildPreflightReason.BoundaryMismatch, service.Evaluate(intent, capability).Reason);
    }

    [Fact]
    public void Boundary_roles_formats_and_complete_sets_are_exact()
    {
        var intent = Intent(boundaries: [new(BuildBoundaryKind.PrimaryInput, null, "json")]);
        foreach (var boundaries in new BuildBoundary[][]
        {
            [new(BuildBoundaryKind.PrimaryOutput, null, "json")],
            [new(BuildBoundaryKind.NamedInput, "primary", "json")],
            [new(BuildBoundaryKind.PrimaryInput, null, "JSON")],
            [new(BuildBoundaryKind.PrimaryInput, null, "xml")],
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")]
        })
        {
            var capability = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend,
                intent.Profile, boundaries, CapabilityStatus.Supported);
            Assert.Equal(BuildPreflightReason.BoundaryMismatch, service.Evaluate(intent, capability).Reason);
        }
    }

    [Fact]
    public void Boundary_input_order_does_not_change_preflight_or_intent_fingerprint()
    {
        var intent = Intent();
        var reversed = Intent(boundaries: intent.Boundaries.Reverse().ToArray());
        Assert.Equal(intent.Fingerprint, reversed.Fingerprint);
        Assert.Equal(BuildPreflightOutcome.Supported, service.Evaluate(intent, Capability(reversed)).Outcome);
        Assert.Equal(new[] { BuildBoundaryKind.PrimaryInput, BuildBoundaryKind.NamedInput,
            BuildBoundaryKind.PrimaryOutput, BuildBoundaryKind.NamedOutput }, intent.Boundaries.Select(item => item.Kind));
    }

    [Fact]
    public void Intent_fingerprint_has_a_fixed_versioned_byte_grammar()
    {
        var canonical = $$"""{"schema":"weft.build-intent.v1","buildId":"{{Build:D}}","organizationId":"{{Organization:D}}","projectId":"{{Project:D}}","mappingId":"{{Mapping:D}}","revisionId":"{{Revision:D}}","nativeDigest":"{{Digest().Value}}","backendId":"fixture","engineRevision":"revision","engineBuild":"engine-build","adapterVersion":"adapter","profileId":"fixture-profile","profileVersion":1,"runtime":"fixture-runtime","runtimeVersion":"v1","artifactFormat":"bundle","maximumArtifactBytes":1048576,"environmentId":"{{EnvironmentId:D}}","environmentVersion":1,"approvalId":"{{Approval:D}}","regressionEvidenceId":"{{Regression:D}}","boundaries":[{"kind":"primaryInput","name":null,"format":"json"},{"kind":"namedInput","name":"lookup","format":"json"},{"kind":"primaryOutput","name":null,"format":"json"},{"kind":"namedOutput","name":"audit","format":"json"}]}""";
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(canonical)), Intent().Fingerprint);
    }

    [Fact]
    public void Artifact_provenance_rejects_every_build_affecting_substitution()
    {
        var intent = Intent();
        var artifact = BuildArtifactReference.Create(intent, Capability(intent), Digest(), 1024);
        var changes = new List<BuildIntent>
        {
            Intent(buildId: Other),
            Intent(revision: new(Other, Project, Mapping, Revision)),
            Intent(revision: new(Organization, Other, Mapping, Revision)),
            Intent(revision: new(Organization, Project, Other, Revision)),
            Intent(revision: new(Organization, Project, Mapping, Other)),
            Intent(nativeDigest: Sha256Digest.FromBytes([2])),
            Intent(environmentId: Other),
            Intent(environment: new(new(Organization, Project), EnvironmentId, 2)),
            Intent(approvalId: Other),
            Intent(regressionId: Other),
            Intent(backend: new("changed", "revision", "engine-build", "adapter")),
            Intent(backend: new("fixture", "changed", "engine-build", "adapter")),
            Intent(backend: new("fixture", "revision", "changed", "adapter")),
            Intent(backend: new("fixture", "revision", "engine-build", "changed")),
            Intent(boundaries: [new(BuildBoundaryKind.PrimaryInput, null, "xml")])
        };
        changes.AddRange(ChangedProfiles().Select(profile => Intent(profile: profile)));
        var changedFormat = intent.Boundaries.ToArray();
        changedFormat[0] = new(BuildBoundaryKind.PrimaryInput, null, "xml");
        changes.Add(Intent(boundaries: changedFormat));
        var changedName = intent.Boundaries.ToArray();
        changedName[1] = new(BuildBoundaryKind.NamedInput, "Lookup", "json");
        changes.Add(Intent(boundaries: changedName));
        var changedRole = intent.Boundaries.ToArray();
        changedRole[1] = new(BuildBoundaryKind.NamedOutput, "lookup", "json");
        changes.Add(Intent(boundaries: changedRole));
        Assert.True(artifact.MatchesIntent(Intent()));
        foreach (var changed in changes)
        {
            Assert.NotEqual(intent.Fingerprint, changed.Fingerprint);
            Assert.False(artifact.MatchesIntent(changed));
        }
        // Compatibility itself is not authority: changing declared evidence still needs a fresh application check.
        Assert.Equal(BuildPreflightOutcome.Supported, service.Evaluate(Intent(approvalId: Other), Capability(intent)).Outcome);
    }

    [Fact]
    public void Artifact_reference_size_is_bounded_without_allocating_the_declared_bytes()
    {
        var intent = Intent();
        var capability = Capability(intent);
        var minimum = BuildArtifactReference.Create(intent, capability, Digest(), 1);
        var maximum = BuildArtifactReference.Create(intent, capability, Digest(), intent.Profile.MaximumArtifactBytes);
        Assert.Equal(1, minimum.ByteCount);
        Assert.Equal(intent.Profile.MaximumArtifactBytes, maximum.ByteCount);
        Assert.Equal(Digest(), maximum.ContentDigest);
        AssertFailure(BuildContractFailure.ResourceLimit, () => BuildArtifactReference.Create(intent, capability, Digest(), 0));
        AssertFailure(BuildContractFailure.ResourceLimit, () => BuildArtifactReference.Create(intent, capability, Digest(), intent.Profile.MaximumArtifactBytes + 1));
    }

    [Fact]
    public void Mutable_lists_do_not_change_intents_or_capability_snapshots()
    {
        var boundaries = new List<BuildBoundary> { new(BuildBoundaryKind.PrimaryInput, null, "json") };
        var intent = Intent(boundaries: boundaries);
        var fingerprint = intent.Fingerprint;
        var capability = Capability(intent);
        boundaries[0] = new(BuildBoundaryKind.PrimaryInput, null, "xml");
        Assert.Equal(fingerprint, intent.Fingerprint);
        Assert.Equal("json", intent.Boundaries[0].Format);
        Assert.Equal(BuildPreflightOutcome.Supported, service.Evaluate(intent, capability).Outcome);
        Assert.Throws<NotSupportedException>(() => ((IList<BuildBoundary>)intent.Boundaries).Clear());
        var conditions = new List<string> { "external-connection" };
        var conditional = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend,
            intent.Profile, intent.Boundaries, CapabilityStatus.Conditional, conditions);
        conditions.Clear();
        Assert.Single(conditional.Conditions);
        Assert.Equal(BuildPreflightOutcome.ConditionsRequired, service.Evaluate(intent, conditional).Outcome);
    }

    [Fact]
    public void Invalid_names_duplicate_boundaries_and_wrong_environment_scope_fail_closed()
    {
        foreach (var text in new[] { "", " ", "control\0", "\ud800", new string('x', 129) })
            AssertFailure(BuildContractFailure.InvalidInput, () => new BuildRuntimeProfile(text, 1, "runtime", "v1", "bundle", 1));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildBoundary(BuildBoundaryKind.PrimaryInput, "name", "json"));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildBoundary(BuildBoundaryKind.NamedInput, null, "json"));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(boundaries:
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryInput, null, "xml")]));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(environment:
            new(new ProjectScope(Other, Project), EnvironmentId, 1)));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(environment:
            new(new ProjectScope(Organization, Other), EnvironmentId, 1)));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(approvalId: Guid.Empty));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(regressionId: Guid.Empty));
        AssertFailure(BuildContractFailure.InvalidInput, () => Intent(buildId: Guid.Empty));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildEnvironmentReference(new(Organization, Project), EnvironmentId, 0));
        AssertFailure(BuildContractFailure.ResourceLimit, () => Intent(boundaries: []));
        AssertFailure(BuildContractFailure.ResourceLimit, () => Intent(boundaries:
            Enumerable.Range(0, 129).Select(index => new BuildBoundary(BuildBoundaryKind.NamedInput, index.ToString(), "json")).ToArray()));
    }

    [Fact]
    public void Capability_status_and_condition_shapes_are_closed()
    {
        var intent = Intent();
        AssertFailure(BuildContractFailure.InvalidInput, () => Capability(intent, (CapabilityStatus)(-1)));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildCapabilitySnapshot(intent.Revision,
            intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries, CapabilityStatus.Supported, ["condition"]));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildCapabilitySnapshot(intent.Revision,
            intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries, CapabilityStatus.Conditional));
    }

    [Fact]
    public void Declared_sizes_and_boundary_counts_have_finite_exact_limits()
    {
        var maximumProfile = new BuildRuntimeProfile("maximum", 1, "runtime", "v1", "bundle",
            BuildRuntimeProfile.MaximumAllowedArtifactBytes);
        var intent = Intent(profile: maximumProfile);
        Assert.Equal(BuildRuntimeProfile.MaximumAllowedArtifactBytes,
            BuildArtifactReference.Create(intent, Capability(intent), Digest(), maximumProfile.MaximumArtifactBytes).ByteCount);
        foreach (var bytes in new[] { 0L, -1L, BuildRuntimeProfile.MaximumAllowedArtifactBytes + 1 })
            AssertFailure(BuildContractFailure.InvalidInput, () => new BuildRuntimeProfile("id", 1, "runtime", "v1", "bundle", bytes));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildRuntimeProfile("id", 0, "runtime", "v1", "bundle", 1));
        AssertFailure(BuildContractFailure.InvalidInput, () => new BuildRuntimeProfile(new string('é', 65), 1, "runtime", "v1", "bundle", 1));
        Assert.Equal(128, Intent(boundaries: Enumerable.Range(0, 128)
            .Select(index => new BuildBoundary(BuildBoundaryKind.NamedInput, index.ToString(), "json")).ToArray()).Boundaries.Count);
    }

    [Fact]
    public void Cancellation_precedes_preflight_and_artifact_reference_creation()
    {
        var intent = Intent();
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Evaluate(intent, Capability(intent), source.Token));
        Assert.Throws<OperationCanceledException>(() => BuildArtifactReference.Create(intent, Capability(intent), Digest(), 1, source.Token));
        Assert.Equal(BuildPreflightOutcome.Supported, service.Evaluate(intent, Capability(intent)).Outcome);
    }

    [Fact]
    public void Diagnostic_strings_do_not_include_private_identifiers_or_configuration()
    {
        var profile = new BuildRuntimeProfile("private-profile", 1, "private-runtime", "private-version", "private-format", 1);
        var intent = Intent(profile: profile);
        foreach (var text in new[] { profile.ToString(), intent.ToString(), intent.Environment.ToString(), intent.Backend.ToString(),
            intent.Boundaries[0].ToString(), Capability(intent).ToString(), BuildArtifactReference.Create(intent, Capability(intent), Digest(), 1).ToString() })
            Assert.DoesNotContain("private", text, StringComparison.Ordinal);
    }

    private static BuildIntent Intent(Guid? buildId = null, RevisionScope? revision = null, Sha256Digest? nativeDigest = null,
        BuildBackendIdentity? backend = null, BuildRuntimeProfile? profile = null, IReadOnlyList<BuildBoundary>? boundaries = null,
        Guid? environmentId = null, Guid? approvalId = null, Guid? regressionId = null, BuildEnvironmentReference? environment = null)
    {
        var scope = revision ?? new(Organization, Project, Mapping, Revision);
        return new(buildId ?? Build, scope, nativeDigest ?? Digest(), backend ?? new("fixture", "revision", "engine-build", "adapter"),
            profile ?? Profile(), boundaries ?? [new(BuildBoundaryKind.PrimaryInput, null, "json"),
                new(BuildBoundaryKind.NamedInput, "lookup", "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json"),
                new(BuildBoundaryKind.NamedOutput, "audit", "json")],
            environment ?? new(new(scope.OrganizationId, scope.ProjectId), environmentId ?? EnvironmentId, 1),
            approvalId ?? Approval, regressionId ?? Regression);
    }

    private static BuildRuntimeProfile Profile() => new("fixture-profile", 1, "fixture-runtime", "v1", "bundle", 1024 * 1024);
    private static BuildCapabilitySnapshot Capability(BuildIntent intent, CapabilityStatus status = CapabilityStatus.Supported) =>
        new(intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries, status,
            status == CapabilityStatus.Conditional ? new[] { "external-connection" } : null);
    private static Sha256Digest Digest() => Sha256Digest.FromBytes([1]);
    private static IEnumerable<BuildRuntimeProfile> ChangedProfiles() =>
    [
        new("other", 1, "fixture-runtime", "v1", "bundle", 1024 * 1024),
        new("fixture-profile", 2, "fixture-runtime", "v1", "bundle", 1024 * 1024),
        new("fixture-profile", 1, "other", "v1", "bundle", 1024 * 1024),
        new("fixture-profile", 1, "fixture-runtime", "other", "bundle", 1024 * 1024),
        new("fixture-profile", 1, "fixture-runtime", "v1", "other", 1024 * 1024),
        new("fixture-profile", 1, "fixture-runtime", "v1", "bundle", 1024 * 1024 + 1)
    ];
    private static void AssertFailure(BuildContractFailure expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<BuildContractException>(action).Failure);
}
