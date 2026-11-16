using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Tests;

public sealed class RegressionCaseExecutionTests
{
    [Theory]
    [InlineData("application/edi-x12", false)]
    [InlineData("application/edi-x12", true)]
    [InlineData("application/edifact", false)]
    [InlineData("application/edifact", true)]
    public async Task New_revision_document_evaluation_retains_profile_comparison_options_and_original_evidence(string media, bool ignoreDates)
    {
        var test = new Harness { ReturnedNative = [8, 7, 6] };
        var old = test.Value;
        var runtime = new RegressionRuntimeIdentity(old.Runtime.BackendId, old.Runtime.EngineRevision, old.Runtime.BackendBuild,
            "document-adapter", DocumentPreviewPolicy.Profile);
        var text = media == "application/edi-x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact;
        var expected = System.Text.Encoding.UTF8.GetBytes(text);
        var actual = System.Text.Encoding.UTF8.GetBytes(text.Replace("261024", "261025", StringComparison.Ordinal));
        test.Value = new(old.CaseId, old.Version, old.Owner, old.Name, old.Revision, old.NativeDigest, runtime, old.Replay,
            RegressionComparisonService.EdiSegmentsPolicy, RegressionOutputSet.Capture([
                RegressionOutput.Capture(RegressionOutputKey.Primary, expected, mediaType: media)]), new(false, true, ignoreDates));
        var original = test.Reference;
        var originalBytes = new RegressionCaseEnvelopeCodec().Encode(test.Value).CopyBytes();
        var revision = new RevisionScope(old.Revision.OrganizationId, old.Revision.ProjectId, old.Revision.MappingId, Guid.NewGuid());
        var digest = Sha256Digest.FromBytes(test.ReturnedNative);
        test.Source = new(revision, new(PrivateObjectId.Create(revision, PrivateObjectClass.NativeArtifact), digest, test.ReturnedNative.Length),
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, "ferrule");
        test.Transform = result => result with
        {
            Profile = runtime.Profile,
            Artifacts = [Output("primary-output", "", actual, media: media)],
            Provenance = result.Provenance with { AdapterBuild = runtime.AdapterBuild }
        };
        var service = new RegressionCaseExecutionService(test, test, test, test, test, test.Runtime, runtime);
        var result = await service.EvaluateAsync(test.Actor, original, new(revision, digest));
        Assert.Equal(ignoreDates ? RegressionComparisonOutcome.Passed : RegressionComparisonOutcome.Failed, result.Comparison!.Outcome);
        Assert.Equal(media, Assert.Single(result.Outputs!.Outputs).MediaType);
        var request = Assert.Single(test.Requests);
        Assert.Equal(PreviewExecutionProfile.Document, request.Profile);
        Assert.Equal(revision, request.Artifact.Scope);
        Assert.Equal(digest, request.Artifact.Digest);
        Assert.Equal(original, test.Reference);
        Assert.Equal(originalBytes, new RegressionCaseEnvelopeCodec().Encode(test.Value).CopyBytes());
        test.Transform = response => response with
        {
            Profile = runtime.Profile,
            Artifacts = [Output("primary-output", "", actual, media: media)],
            Provenance = response.Provenance with { Scope = old.Revision, NativeDigest = old.NativeDigest, AdapterBuild = runtime.AdapterBuild }
        };
        Refused(await service.EvaluateAsync(test.Actor, original, new(revision, digest)), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Theory]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    public async Task Document_replay_requires_the_separately_pinned_runtime_and_keeps_media(string media)
    {
        var test = new Harness(); var old = test.Value;
        var runtime = new RegressionRuntimeIdentity(old.Runtime.BackendId, old.Runtime.EngineRevision, old.Runtime.BackendBuild,
            "document-adapter", DocumentPreviewPolicy.Profile);
        var bytes = System.Text.Encoding.UTF8.GetBytes(media == "application/edi-x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact);
        test.Value = new(old.CaseId, old.Version, old.Owner, old.Name, old.Revision, old.NativeDigest, runtime, old.Replay,
            RegressionComparisonService.EdiSegmentsPolicy, RegressionOutputSet.Capture([
                RegressionOutput.Capture(RegressionOutputKey.Primary, bytes, mediaType: media)]), new(false, true, true));
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.RuntimeMismatch);
        Assert.Empty(test.Requests);
        test.Transform = result => result with
        {
            Profile = runtime.Profile,
            Artifacts = [Output("primary-output", "", bytes, media: media)],
            Provenance = result.Provenance with { AdapterBuild = runtime.AdapterBuild }
        };
        var service = new RegressionCaseExecutionService(test, test, test, test, test, test.Runtime, runtime);
        var result = await service.ExecuteAsync(test.Actor, test.Reference);
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome);
        Assert.Equal(media, Assert.Single(result.Outputs!.Outputs).MediaType);
        Assert.Equal(PreviewExecutionProfile.Document, Assert.Single(test.Requests).Profile);
    }

    [Fact]
    public async Task Evaluates_the_new_revision_without_rewriting_original_case_provenance()
    {
        var test = new Harness { ReturnedNative = [8, 7, 6] };
        var original = test.Reference;
        var originalBytes = new RegressionCaseEnvelopeCodec().Encode(test.Value).CopyBytes();
        var revision = new RevisionScope(original.Revision.OrganizationId, original.Revision.ProjectId, original.Revision.MappingId, Guid.NewGuid());
        var digest = Sha256Digest.FromBytes(test.ReturnedNative);
        test.Source = new(revision, new(PrivateObjectId.Create(revision, PrivateObjectClass.NativeArtifact), digest, 3),
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, "ferrule");
        var result = await test.Service.EvaluateAsync(test.Actor, original, new(revision, digest));
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome);
        Assert.Equal(revision, Assert.Single(test.Requests).Artifact.Scope);
        Assert.Equal(digest, test.Requests[0].Artifact.Digest);
        Assert.Equal(original, test.Reference);
        Assert.Equal(originalBytes, new RegressionCaseEnvelopeCodec().Encode(test.Value).CopyBytes());
    }

    [Fact]
    public async Task New_revision_evaluation_refuses_a_backend_replaying_the_original_revision()
    {
        var test = new Harness();
        var original = test.Reference;
        var revision = new RevisionScope(original.Revision.OrganizationId, original.Revision.ProjectId, original.Revision.MappingId, Guid.NewGuid());
        test.Source = new(revision, new(PrivateObjectId.Create(revision, PrivateObjectClass.NativeArtifact), original.NativeDigest, test.Native.Length),
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, "ferrule");
        test.Transform = result => result with { Provenance = result.Provenance with { Scope = original.Revision } };
        Refused(await test.Service.EvaluateAsync(test.Actor, original, new(revision, original.NativeDigest)), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Fact]
    public void Local_admission_tracks_the_same_backend_without_reading_case_or_payload_data()
    {
        var test = new Harness();
        Assert.True(test.Service.CanExecute);
        test.CanExecute = false;
        Assert.False(test.Service.CanExecute);
        test.CanExecute = true;
        Assert.True(test.Service.CanExecute);
        Assert.Equal(0, test.SourceReads);
        Assert.Equal(0, test.StorageReads);
        Assert.Empty(test.Requests);
    }

    [Fact]
    public async Task Replays_the_exact_saved_revision_clock_parameters_and_bytes()
    {
        var test = new Harness();
        var result = await test.ExecuteAsync();
        Assert.Equal(RegressionCaseExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome);
        var request = Assert.Single(test.Requests);
        Assert.Equal(test.Value.Revision, request.Artifact.Scope);
        Assert.Equal(test.Value.NativeDigest, request.Artifact.Digest);
        Assert.Equal(test.Native, request.Artifact.CopyBytes());
        Assert.Equal(test.Value.Replay.CurrentDateTime, request.CurrentDateTime);
        Assert.Equal(test.Value.Replay.Primary.CopyBytes(), request.Primary.CopyBytes());
        Assert.Equal(test.Value.Replay.Parameters, request.Parameters);
        Assert.Equal(test.Value.Replay.Limits, request.Limits);
        Assert.Equal(test.Value.Replay.NamedInputs[0].CopyBytes(), request.NamedInputs[0].CopyBytes());
        Assert.DoesNotContain("private", result.ToString());
    }

    [Theory]
    [InlineData("different", RegressionComparisonOutcome.Failed)]
    [InlineData("missing-baseline", RegressionComparisonOutcome.NotEvaluated)]
    public async Task A_successful_execution_still_requires_the_explicit_exact_baseline(string mode, RegressionComparisonOutcome expected)
    {
        var test = new Harness();
        if (mode == "different") test.Transform = result => result with { Artifacts = [Output("primary-output", "", "different"u8)] };
        else test.Value = test.MakeCase(baseline: false);
        Assert.Equal(expected, (await test.ExecuteAsync()).Comparison!.Outcome);
    }

    public static TheoryData<MappingPreviewStatus> UnsuccessfulStatuses => new(Enum.GetValues<MappingPreviewStatus>()
        .Where(status => status != MappingPreviewStatus.Succeeded));

    [Theory]
    [MemberData(nameof(UnsuccessfulStatuses))]
    public async Task A_failed_execution_never_passes(MappingPreviewStatus status)
    {
        var test = new Harness();
        test.Transform = result => result with
        {
            Status = status,
            Artifacts = [],
            RecordsWritten = 0,
            Failure = new("private-code", "private-payload-text", false, null, false),
            Provenance = result.Provenance with { Confirmed = false }
        };
        var result = await test.ExecuteAsync();
        Assert.Equal(RegressionCaseExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(status, result.PreviewStatus);
        Assert.Equal(RegressionNotEvaluatedReason.ExecutionUnsuccessful, result.Comparison!.NotEvaluatedReason);
        Assert.Empty(result.Outputs!.Outputs);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("backend")]
    [InlineData("engine")]
    [InlineData("build")]
    [InlineData("adapter")]
    [InlineData("profile")]
    [InlineData("unconfirmed")]
    public async Task Mismatched_successful_provenance_cannot_publish_outputs(string field)
    {
        var test = new Harness();
        test.Transform = result => field switch
        {
            "scope" => result with { Provenance = result.Provenance with { Scope = Harness.OtherScope } },
            "digest" => result with { Provenance = result.Provenance with { NativeDigest = Sha256Digest.FromBytes([9]) } },
            "backend" => result with { Provenance = result.Provenance with { BackendId = "other" } },
            "engine" => result with { Provenance = result.Provenance with { EngineRevision = "other" } },
            "build" => result with { Provenance = result.Provenance with { BackendBuild = "other" } },
            "adapter" => result with { Provenance = result.Provenance with { AdapterBuild = "other" } },
            "profile" => result with { Profile = "other" },
            _ => result with { Provenance = result.Provenance with { Confirmed = false } }
        };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("version")]
    [InlineData("owner")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("input")]
    [InlineData("expected")]
    public async Task A_different_loaded_case_cannot_execute_the_queued_reference(string field)
    {
        var test = new Harness();
        var expected = test.Reference;
        test.Value = test.MakeCase(caseId: field == "case" ? Guid.NewGuid() : null,
            version: field == "version" ? 2 : 1,
            owner: field == "owner" ? new(Guid.NewGuid()) : null,
            revision: field == "revision" ? Harness.OtherScope : null,
            name: field == "name" ? "other" : "private-case",
            input: field == "input" ? [7] : null,
            output: field == "expected" ? [8] : null);
        Refused(await test.Service.ExecuteAsync(test.Actor, expected), RegressionCaseExecutionOutcome.CaseChanged);
        Assert.Empty(test.Requests);
        Assert.Equal(0, test.StorageReads);
    }

    [Fact]
    public async Task A_changed_runtime_is_refused_before_source_or_private_storage()
    {
        var test = new Harness();
        var service = test.CreateService(new("ferrule", "changed", "build", "adapter", PreviewPolicy.Profile));
        Refused(await service.ExecuteAsync(test.Actor, test.Reference), RegressionCaseExecutionOutcome.RuntimeMismatch);
        Assert.Empty(test.Requests);
        Assert.Equal(0, test.StorageReads);
        Assert.Equal(0, test.SourceReads);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("actor")]
    [InlineData("digest")]
    [InlineData("backend")]
    [InlineData("document")]
    [InlineData("dependencies")]
    public async Task Source_substitution_is_refused_before_storage(string field)
    {
        var test = new Harness();
        var source = test.Source;
        var scope = field == "scope" ? Harness.OtherScope : source.Scope;
        test.Source = new(scope, new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact),
            field == "digest" ? Sha256Digest.FromBytes([9]) : source.NativeObject.Digest, source.NativeObject.ByteCount),
            field == "document" ? "different" : source.LogicalDocument,
            field == "dependencies" ? "different" : source.DependencyIdentity,
            field == "backend" ? "different" : source.SourceBackendId);
        if (field == "actor") test.SourceActor = new(Guid.NewGuid());
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.SourceMismatch);
        Assert.Empty(test.Requests);
        Assert.Equal(0, test.StorageReads);
    }

    [Fact]
    public async Task Storage_that_returns_the_wrong_bytes_never_reaches_the_backend()
    {
        var test = new Harness { ReturnedNative = [9, 8, 7] };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.SourceMismatch);
        Assert.Empty(test.Requests);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("source")]
    [InlineData("storage")]
    [InlineData("backend")]
    public async Task Revocation_drops_private_results_at_each_async_boundary(string boundary)
    {
        var test = new Harness();
        if (boundary == "before") test.Role = ProjectRole.Viewer;
        if (boundary == "source") test.OnSource = () => test.Role = ProjectRole.Viewer;
        if (boundary == "storage") test.OnStorage = () => test.Role = ProjectRole.Viewer;
        if (boundary == "backend") test.OnBackend = () => test.Role = ProjectRole.Viewer;
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.NotAuthorizedOrNotFound);
        Assert.Equal(boundary == "backend" ? 1 : 0, test.Requests.Count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("source")]
    [InlineData("storage")]
    [InlineData("backend")]
    public async Task Requested_cancellation_propagates_even_when_a_dependency_returns_normally(string boundary)
    {
        var test = new Harness();
        using var cancellation = new CancellationTokenSource();
        if (boundary == "before") cancellation.Cancel();
        if (boundary == "source") test.OnSource = cancellation.Cancel;
        if (boundary == "storage") test.OnStorage = cancellation.Cancel;
        if (boundary == "backend") test.OnBackend = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.ExecuteAsync(cancellation.Token));
        Assert.Equal(boundary == "backend" ? 1 : 0, test.Requests.Count);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("boundary")]
    [InlineData("noncanonical")]
    [InlineData("media")]
    [InlineData("missing-primary")]
    [InlineData("empty")]
    [InlineData("failure-on-success")]
    [InlineData("unknown-status")]
    public async Task Ambiguous_or_malformed_outputs_cannot_be_compared(string mode)
    {
        var test = new Harness();
        test.Transform = result => mode switch
        {
            "truncated" => result with { Artifacts = [result.Artifacts[0], Output("named-output:0", "private", [1], truncated: true)] },
            "duplicate" => result with { Artifacts = [result.Artifacts[0], Output("named-output:0", "same", [1]), Output("named-output:1", "same", [1])] },
            "boundary" => result with { Artifacts = [Output("unknown", "", [1])] },
            "noncanonical" => result with { Artifacts = [result.Artifacts[0], Output("named-output:00", "other", [1])] },
            "media" => result with { Artifacts = [Output("primary-output", "", [1], media: "text/plain")] },
            "missing-primary" => result with { Artifacts = [Output("named-output:0", "private", [1])] },
            "empty" => result with { Artifacts = [] },
            "failure-on-success" => result with { Failure = new("private", "private", false, null, false) },
            _ => result with { Status = (MappingPreviewStatus)999 }
        };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Fact]
    public async Task Named_outputs_use_exact_complete_names_not_declaration_order()
    {
        var test = new Harness();
        test.Value = test.MakeCase(expected: RegressionOutputSet.Capture([
            RegressionOutput.Capture(RegressionOutputKey.Primary, "expected"u8),
            RegressionOutput.Capture(RegressionOutputKey.Named("Lookup"), [1]),
            RegressionOutput.Capture(RegressionOutputKey.Named("lookup"), [2])]));
        test.Transform = result => result with { Artifacts = [result.Artifacts[0], Output("named-output:1", "lookup", [2]), Output("named-output:0", "Lookup", [1])] };
        Assert.Equal(RegressionComparisonOutcome.Passed, (await test.ExecuteAsync()).Comparison!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_primary_and_named_selections_only_accept_the_requested_output(bool named)
    {
        var test = new Harness();
        var key = named ? RegressionOutputKey.Named("exact-name") : RegressionOutputKey.Primary;
        test.Value = test.MakeCase(target: named ? PreviewTargetSelection.Named("exact-name") : PreviewTargetSelection.Primary,
            expected: RegressionOutputSet.Capture([RegressionOutput.Capture(key, "expected"u8)]));
        test.Transform = result => result with { Artifacts = [Output(named ? "named-output:4" : "primary-output", "exact-name", "expected"u8)] };
        Assert.Equal(RegressionComparisonOutcome.Passed, (await test.ExecuteAsync()).Comparison!.Outcome);
        test.Transform = result => result with { Artifacts = [Output(named ? "primary-output" : "named-output:4", "exact-name", "expected"u8)] };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Fact]
    public async Task Combined_output_bytes_and_count_cannot_exceed_the_saved_limits()
    {
        var test = new Harness();
        test.Value = test.MakeCase(limits: new(PreviewPolicy.MaximumInputBytes, 16, 3, PreviewPolicy.MaximumTextBytes));
        test.Transform = result => result with { Artifacts = [Output("primary-output", "", new byte[8]), Output("named-output:0", "other", new byte[8])] };
        Assert.Equal(RegressionCaseExecutionOutcome.Completed, (await test.ExecuteAsync()).Outcome);
        test.Transform = result => result with { Artifacts = [Output("primary-output", "", new byte[8]), Output("named-output:0", "other", new byte[9])] };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
        test.Transform = result => result with
        {
            Artifacts = [Output("primary-output", "", []), Output("named-output:0", "one", []),
            Output("named-output:1", "two", []), Output("named-output:2", "three", [])]
        };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Fact]
    public async Task A_maximum_size_output_can_be_verified_and_compared_without_truncation()
    {
        var test = new Harness();
        var output = new byte[PreviewPolicy.MaximumOutputBytes];
        Array.Fill(output, (byte)255);
        test.Value = test.MakeCase(output: output);
        test.Transform = result => result with { Artifacts = [Output("primary-output", "", output)] };
        var result = await test.ExecuteAsync();
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome);
        Assert.Equal(output.Length, result.Outputs!.ByteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_the_native_object_with_the_same_digest_is_still_a_source_change(bool afterDispatch)
    {
        var test = new Harness();
        void Replace() => test.Source = new(test.Source.Scope,
            new(PrivateObjectId.Create(test.Source.Scope, PrivateObjectClass.NativeArtifact), test.Source.NativeObject.Digest, test.Source.NativeObject.ByteCount),
            test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId);
        if (afterDispatch) test.OnBackend = Replace;
        else test.OnStorage = Replace;
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.SourceMismatch);
        Assert.Equal(afterDispatch ? 1 : 0, test.Requests.Count);
    }

    [Fact]
    public async Task A_failed_preview_with_output_is_a_protocol_refusal_not_a_comparison()
    {
        var test = new Harness();
        test.Transform = result => result with
        {
            Status = MappingPreviewStatus.ExecutionFailed,
            Failure = new("failed", "private", false, null, false)
        };
        Refused(await test.ExecuteAsync(), RegressionCaseExecutionOutcome.ResultRejected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task One_case_failure_does_not_block_unrelated_invocations_and_only_failed_work_needs_retry(int failureIndex)
    {
        var test = new Harness();
        var cases = Enumerable.Range(0, 3).Select(_ => test.MakeCase(caseId: Guid.NewGuid())).ToArray();
        var outcomes = new List<RegressionCaseExecution>();
        for (var index = 0; index < cases.Length; index++)
        {
            test.Value = cases[index];
            test.ThrowOnBackend = index == failureIndex;
            outcomes.Add(await test.ExecuteAsync());
        }
        Assert.Equal(RegressionCaseExecutionOutcome.InfrastructureFailure, outcomes[failureIndex].Outcome);
        Assert.All(outcomes.Where((_, index) => index != failureIndex), result => Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome));
        Assert.Equal(3, test.Requests.Count);
        test.Value = cases[failureIndex];
        test.ThrowOnBackend = false;
        Assert.Equal(RegressionComparisonOutcome.Passed, (await test.ExecuteAsync()).Comparison!.Outcome);
        Assert.Equal(4, test.Requests.Count);
    }

    [Fact]
    public async Task Infrastructure_details_are_not_exposed_and_the_next_case_still_runs()
    {
        var test = new Harness { OnStorage = () => throw new InvalidOperationException("private payload and database details") };
        var failure = await test.ExecuteAsync();
        Refused(failure, RegressionCaseExecutionOutcome.InfrastructureFailure);
        Assert.DoesNotContain("private", failure.ToString());
        test.OnStorage = null;
        Assert.Equal(RegressionComparisonOutcome.Passed, (await test.ExecuteAsync()).Comparison!.Outcome);
    }

    private static void Refused(RegressionCaseExecution result, RegressionCaseExecutionOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Outputs);
        Assert.Null(result.Comparison);
        Assert.Null(result.PreviewStatus);
    }

    private static PreviewOutputArtifact Output(string boundary, string name, ReadOnlySpan<byte> bytes,
        bool truncated = false, string media = PreviewPolicy.MediaType) => new(boundary, name, truncated, media, 1, bytes);

    private sealed class Harness : IRegressionCaseReader, IProjectReader, IRevisionReportMetadataReader, IPrivateObjectStore, IMappingPreviewBackend, IExecutionAdmission
    {
        public bool CanExecute { get; set; } = true;
        internal static readonly RevisionScope OtherScope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        private readonly RevisionScope scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        private readonly Guid caseId = Guid.NewGuid();
        internal byte[] Native { get; } = [1, 2, 3];
        internal byte[]? ReturnedNative { get; init; }
        internal ActorContext Actor { get; } = new(new(Guid.NewGuid()));
        internal RegressionRuntimeIdentity Runtime { get; } = new("ferrule", "engine", "build", "adapter", PreviewPolicy.Profile);
        internal RegressionCaseVersion Value { get; set; }
        internal RegressionCaseReference Reference => new RegressionCaseEnvelopeCodec().Encode(Value).Reference;
        internal RevisionReportSource Source { get; set; }
        internal PrincipalId SourceActor { get; set; }
        internal ProjectRole Role { get; set; } = ProjectRole.Runner;
        internal RegressionCaseExecutionService Service { get; }
        internal List<MappingPreviewRequest> Requests { get; } = [];
        internal int StorageReads { get; private set; }
        internal int SourceReads { get; private set; }
        internal Action? OnSource { get; set; }
        internal Action? OnStorage { get; set; }
        internal Action? OnBackend { get; set; }
        internal bool ThrowOnBackend { get; set; }
        internal Func<MappingPreviewResult, MappingPreviewResult>? Transform { get; set; }

        internal Harness()
        {
            Value = MakeCase();
            Source = new(scope, new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), Value.NativeDigest, Native.Length),
                IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, Runtime.BackendId);
            SourceActor = Actor.PrincipalId;
            Service = CreateService(Runtime);
        }

        internal RegressionCaseExecutionService CreateService(RegressionRuntimeIdentity runtime) => new(this, this, this, this, this, runtime);
        internal Task<RegressionCaseExecution> ExecuteAsync(CancellationToken token = default) => Service.ExecuteAsync(Actor, Reference, token);
        internal RegressionCaseVersion MakeCase(Guid? caseId = null, long version = 1, PrincipalId? owner = null,
            RevisionScope? revision = null, string name = "private-case", byte[]? input = null, byte[]? output = null,
            bool baseline = true, RegressionOutputSet? expected = null, PreviewTargetSelection? target = null, PreviewLimits? limits = null) =>
            new(caseId ?? this.caseId, version, owner ?? Actor.PrincipalId, name, revision ?? scope, Sha256Digest.FromBytes(Native), Runtime,
                new(target ?? PreviewTargetSelection.All, "2026-10-20T12:34:56.123456789-07:00", limits ?? PreviewLimits.Default,
                    new(null, input ?? [255, 0, 128]), [new("lookup", [4, 5, 6])],
                    [new("int", PreviewParameterKind.Integer, "9007199254740993"), new("float", PreviewParameterKind.Float, "-0.0")]),
                RegressionComparisonService.ExactBytesPolicy, expected ?? RegressionOutputSet.Capture(baseline
                    ? [RegressionOutput.Capture(RegressionOutputKey.Primary, output ?? "expected"u8.ToArray())] : []));

        public Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version, CancellationToken cancellationToken = default) =>
            Task.FromResult<RegressionCaseVersion?>(Value);
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, Role));
        public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
        {
            SourceReads++;
            OnSource?.Invoke();
            return Task.FromResult<AuthorizedRevisionReportSource?>(new(Source, SourceActor, DateTimeOffset.UtcNow));
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
        {
            StorageReads++;
            OnStorage?.Invoke();
            return Task.FromResult(ReturnedNative ?? Native.ToArray());
        }
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            OnBackend?.Invoke();
            if (ThrowOnBackend) throw new InvalidOperationException("private failure");
            var result = new MappingPreviewResult(MappingPreviewStatus.Succeeded,
                new(request.Artifact.Scope, request.Artifact.Digest, Runtime.BackendId, Runtime.EngineRevision, Runtime.BackendBuild, Runtime.AdapterBuild, true),
                Runtime.Profile, 1, [Output("primary-output", "", "expected"u8)], null);
            return Task.FromResult(Transform?.Invoke(result) ?? result);
        }
        public Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterCaseId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
