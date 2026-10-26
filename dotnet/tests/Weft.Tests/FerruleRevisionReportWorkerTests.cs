using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Real pinned worker runs with synthetic metadata identities; no durable report publication.</summary>
public sealed class FerruleRevisionReportWorkerTests
{
    private static readonly RevisionScope SyntheticScope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualDefaultProjectionRetainsConfirmedContextExactNumbersAndOriginalNativeBytes()
    {
        var artifact = Artifact("versioned-exact");
        var original = artifact.CopyBytes();
        var runner = new DockerInspectionWorkerRunner(Options());
        var profile = Profile(runner);
        var key = ProjectionKey(artifact, profile);
        var result = await new FerruleRevisionReportBackend(runner, profile).DeriveAsync(key, artifact);
        var report = SuccessfulProjection(result, key);
        var document = Assert.IsType<ProjectionDocumentV1>(report.Document);

        Assert.Equal("weft.mapping-projection.v1", document.SchemaVersion);
        Assert.Equal(FerruleInspectionBackend.ProjectionVersion, document.Provenance.ProjectionVersion);
        Assert.NotEmpty(document.Fields);
        Assert.Equal(2, document.Boundaries.Count);
        var literals = document.Rules.SelectMany(rule => rule.Dependencies).OfType<ConstantDependency>()
            .Select(value => value.Value).OfType<ExactNumberLiteralValue>().ToArray();
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.Integer && value.Value == "9007199254740993");
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.BinaryFloat && value.Value == "3ff0000000000000");
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.BinaryFloat && value.Value == "8000000000000000");
        Assert.Equal(original, artifact.CopyBytes());
        Assert.Equal(Sha256Digest.FromBytes(original), artifact.Digest);
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualValidationDistinguishesValidAndInvalidAndKeepsExactNativeFunctionIdentity()
    {
        var runner = new DockerInspectionWorkerRunner(Options());
        var profile = Profile(runner);
        var backend = new FerruleRevisionReportBackend(runner, profile);

        var valid = Artifact("valid-basic");
        var validKey = ValidationKey(valid, profile);
        var validResult = await backend.DeriveAsync(validKey, valid);
        var validReport = SuccessfulValidation(validResult, validKey);
        Assert.Equal(OperationStatusV1.Succeeded, validReport.Status);
        Assert.True(validReport.IsValid);
        Assert.Equal(0, validReport.TotalIssueCount);
        Assert.Equal(0, validReport.EmittedIssueCount);
        Assert.Empty(validReport.Diagnostics);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, validResult.Observation!.Status);

        var invalid = Artifact("invalid-function");
        var invalidKey = ValidationKey(invalid, profile);
        var invalidResult = await backend.DeriveAsync(invalidKey, invalid);
        var invalidReport = SuccessfulValidation(invalidResult, invalidKey);
        Assert.Equal(OperationStatusV1.InvalidMapping, invalidReport.Status);
        Assert.False(invalidReport.IsValid);
        Assert.True(invalidReport.TotalIssueCount > 0);
        Assert.Equal(invalidReport.Diagnostics.Count, invalidReport.EmittedIssueCount);
        Assert.Equal(RevisionReportBackendStatus.InvalidMapping, invalidResult.Observation!.Status);
        Assert.False(invalidResult.Observation.Validation!.IsValid);
        Assert.Contains(invalidReport.Diagnostics, diagnostic => diagnostic.Owner is GraphNodeNativeOwnerV1
        { FunctionId: "18446744073709551615", NodeId: "7" });
        Assert.Contains(invalidReport.Diagnostics, diagnostic => diagnostic.NativeLocator is GraphNodeNativeLocator
        { FunctionId: "18446744073709551615", NodeId: "7" });
        Assert.All(invalidReport.Diagnostics, diagnostic => Assert.Null(diagnostic.ProjectionLocator));
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualProjectionAndValidationSucceedThroughTheRegistryWithSensibleReducedPolicy()
    {
        var artifact = Artifact("valid-basic");
        var runner = new DockerInspectionWorkerRunner(Options());
        var profile = Profile(runner);
        var policy = new RevisionReportPolicy(nativeByteLimit: 65536, workerResponseByteLimit: 262144,
            itemLimit: 256, responseItemLimit: 4096, jsonDepthLimit: 32, detailDepthLimit: 8,
            diagnosticLimit: 16, validationIssueLimit: 256, textByteLimit: 512,
            headerByteLimit: 8192, storedObjectByteLimit: 1048576, responseByteLimit: 1048576);
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(profile, policy, () => runner);
        var projection = Assert.IsType<RevisionReportRegistration>(registry.FindCurrent("ferrule", RevisionReportOperation.Projection));
        var validation = Assert.IsType<RevisionReportRegistration>(registry.FindCurrent("ferrule", RevisionReportOperation.Validation));
        var projectionKey = ProjectionKey(artifact, profile, policy);
        var validationKey = ValidationKey(artifact, profile, policy);

        var projected = await projection.GetBackend().DeriveAsync(projectionKey, artifact);
        var report = SuccessfulProjection(projected, projectionKey);
        Assert.NotEmpty(report.Document!.Fields);
        Assert.True(projected.Observation!.Projection!.DocumentItemCount <= policy.ItemLimit);
        var validated = SuccessfulValidation(await validation.GetBackend().DeriveAsync(validationKey, artifact), validationKey);
        Assert.True(validated.IsValid);
        Assert.Equal(0, validated.TotalIssueCount);
        Assert.Equal(policy, projection.Policy);
        Assert.Equal(policy, validation.Policy);
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualLoweredTextRefusesTheWholeProjectionAndSameRunnerAcceptsTheExactLiteralAtDefaultPolicy()
    {
        // Author a new native input. Admitted artifact bytes are never rewritten by the adapter.
        var native = JsonNode.Parse(Artifact("valid-basic").CopyBytes())!;
        var literal = new string('é', 65); // 130 UTF8 bytes: above 128, below the unchanged hard ceiling.
        native["graph"]!["nodes"]!["1"] = new JsonObject { ["kind"] = "const", ["value"] = literal };
        var artifact = NativeArtifact.Admit(SyntheticScope, JsonSerializer.SerializeToUtf8Bytes(native), InspectionPolicy.MaximumNativeBytes);
        var original = artifact.CopyBytes();
        var runner = new DockerInspectionWorkerRunner(Options());
        var profile = Profile(runner);
        var backend = new FerruleRevisionReportBackend(runner, profile);
        var limitedKey = ProjectionKey(artifact, profile, new(textByteLimit: 128));

        var refused = await backend.DeriveAsync(limitedKey, artifact);
        AssertResourceRefusal(refused, limitedKey);
        var defaultKey = ProjectionKey(artifact, profile);
        var report = SuccessfulProjection(await backend.DeriveAsync(defaultKey, artifact), defaultKey);
        Assert.Contains(report.Document!.Rules.SelectMany(rule => rule.Dependencies), dependency =>
            dependency is ConstantDependency { Value: StringLiteralValue value } && value.Value == literal);
        Assert.Equal(original, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualLoweredItemsRefusesTheWholeProjectionAndSameRunnerAdmitsTheNextDefaultRequest()
    {
        var artifact = Artifact("valid-basic");
        var runner = new DockerInspectionWorkerRunner(Options());
        var profile = Profile(runner);
        var backend = new FerruleRevisionReportBackend(runner, profile);
        var limitedKey = ProjectionKey(artifact, profile, new(itemLimit: 1));

        AssertResourceRefusal(await backend.DeriveAsync(limitedKey, artifact), limitedKey);
        var key = ProjectionKey(artifact, profile);
        var report = SuccessfulProjection(await backend.DeriveAsync(key, artifact), key);
        Assert.Equal(2, report.Document!.Boundaries.Count);
        Assert.NotEmpty(report.Document.Rules);
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualInFlightCancellationAutomaticallyRemovesWorkerAndSameRunnerAdmitsTheNextValidation()
    {
        var launcher = new HoldingInputLauncher();
        var runner = new DockerInspectionWorkerRunner(Options(), launcher);
        var profile = Profile(runner);
        var backend = new FerruleRevisionReportBackend(runner, profile);
        var artifact = Artifact("valid-basic");
        var key = ProjectionKey(artifact, profile);
        using var cancellation = new CancellationTokenSource();
        var operation = backend.DeriveAsync(key, artifact, cancellation.Token);
        try
        {
            await launcher.InputWritten.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var name = Assert.IsType<string>(launcher.ContainerName);
            await WaitForRunningContainerAsync(name);
            cancellation.Cancel();
            var canceled = await operation.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.False(canceled.IsUsable);
            Assert.Equal(RevisionReportFailureCategory.Canceled, canceled.Failure);
            Assert.Null(canceled.ProjectionReport);
            Assert.Null(canceled.ValidationReport);
            var observation = Assert.IsType<RevisionReportObservation>(canceled.Observation);
            Assert.Equal(RevisionReportBackendStatus.Canceled, observation.Status);
            Assert.False(Assert.IsType<RevisionReportProvenance>(observation.Provenance).Confirmed);

            var removed = await InspectContainerAsync(name);
            Assert.NotEqual(0, removed.ExitCode);
            Assert.Contains("no such container", removed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(name, removed.Error, StringComparison.Ordinal);
            var validationKey = ValidationKey(artifact, profile);
            Assert.True(SuccessfulValidation(await backend.DeriveAsync(validationKey, artifact), validationKey).IsValid);
        }
        finally
        {
            cancellation.Cancel();
            // The production runner must settle and remove its own container. This test never removes it.
            await operation.WaitAsync(TimeSpan.FromSeconds(12));
        }
    }

    private static ProjectionReportV1 SuccessfulProjection(RevisionReportBackendResult result, ProjectionCacheKey key)
    {
        Assert.True(result.IsUsable);
        Assert.Equal(key, result.Key);
        Assert.Null(result.Failure);
        Assert.Null(result.ValidationReport);
        var report = Assert.IsType<ProjectionReportV1>(result.ProjectionReport);
        Assert.Equal(OperationStatusV1.Succeeded, report.Status);
        Assert.IsType<ProjectionDocumentV1>(report.Document);
        AssertOperationProvenance(report.Provenance, key);
        AssertConfirmed(result, key);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, result.Observation!.Status);
        Assert.NotNull(result.Observation.Projection!.Coverage);
        return report;
    }

    private static ValidationReportV1 SuccessfulValidation(RevisionReportBackendResult result, ValidationCacheKey key)
    {
        Assert.True(result.IsUsable);
        Assert.Equal(key, result.Key);
        Assert.Null(result.Failure);
        Assert.Null(result.ProjectionReport);
        var report = Assert.IsType<ValidationReportV1>(result.ValidationReport);
        Assert.NotNull(report.IsValid);
        AssertOperationProvenance(report.Provenance, key);
        AssertConfirmed(result, key);
        Assert.Equal(report.IsValid, result.Observation!.Validation!.IsValid);
        Assert.Equal(report.TotalIssueCount, result.Observation.Validation.TotalIssueCount);
        Assert.Equal(report.EmittedIssueCount, result.Observation.Validation.EmittedIssueCount);
        return report;
    }

    private static void AssertResourceRefusal(RevisionReportBackendResult result, ProjectionCacheKey key)
    {
        Assert.False(result.IsUsable);
        Assert.Equal(RevisionReportFailureCategory.ResourceLimit, result.Failure);
        Assert.Null(result.Key);
        Assert.Null(result.ProjectionReport);
        Assert.Null(result.ValidationReport);
        AssertConfirmed(result, key);
        Assert.Equal(RevisionReportBackendStatus.ResourceLimit, result.Observation!.Status);
    }

    private static void AssertConfirmed(RevisionReportBackendResult result, RevisionReportKey key)
    {
        var observation = Assert.IsType<RevisionReportObservation>(result.Observation);
        var provenance = Assert.IsType<RevisionReportProvenance>(observation.Provenance);
        Assert.True(provenance.Confirmed);
        Assert.Equal(key.Operation, observation.Operation);
        Assert.Equal(key.Scope, provenance.Scope);
        Assert.Equal(key.NativeDigest, provenance.NativeDigest);
        Assert.Equal(key.Profile, provenance.Profile);
    }

    private static void AssertOperationProvenance(OperationProvenanceV1? provenance, RevisionReportKey key)
    {
        var actual = Assert.IsType<OperationProvenanceV1>(provenance);
        Assert.True(actual.Confirmed);
        Assert.Equal(key.Scope.OrganizationId.ToString("D"), actual.Revision.OrganizationId);
        Assert.Equal(key.Scope.ProjectId.ToString("D"), actual.Revision.ProjectId);
        Assert.Equal(key.Scope.MappingId.ToString("D"), actual.Revision.MappingId);
        Assert.Equal(key.Scope.RevisionId.ToString("D"), actual.Revision.RevisionId);
        Assert.Equal(key.NativeDigest.Value, actual.NativeArtifactDigest.Value);
        Assert.Equal(key.BackendId, actual.Backend.Id);
        Assert.Equal(key.BackendRevision, actual.Backend.Revision);
        Assert.Equal(key.BackendBuild, actual.Backend.BuildId);
        Assert.Equal(key.AdapterBuild, actual.AdapterVersion);
    }

    private static NativeArtifact Artifact(string fixture) => NativeArtifact.Admit(SyntheticScope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")), InspectionPolicy.MaximumNativeBytes);

    private static DockerInspectionWorkerOptions Options() => new("/usr/bin/docker",
        Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!);

    private static RevisionReportBackendProfile Profile(IInspectionWorkerRunner runner)
    {
        var profile = FerruleRevisionReportBackendFactory.CreateProfile(runner.BuildIdentity);
        Assert.Equal("576012aa17c403d9c3fcb8659ed4d3a47ba06864", profile.BackendRevision);
        Assert.Equal(runner.BuildIdentity, profile.BackendBuild);
        return profile;
    }

    private static RevisionReportSource Source(NativeArtifact artifact) => new(artifact.Scope,
        new(PrivateObjectId.Restore(artifact.Scope, Guid.Parse("55555555-5555-5555-5555-555555555555"), PrivateObjectClass.NativeArtifact),
            artifact.Digest, artifact.ByteCount), InspectionPolicy.DocumentId, IngestionIntent.EmptyDependencyIdentity, "ferrule");

    private static ProjectionCacheKey ProjectionKey(NativeArtifact artifact, RevisionReportBackendProfile profile, RevisionReportPolicy? policy = null) =>
        new(Source(artifact), profile, "weft.mapping-projection.v1", "weft.ferrule-projection.v1", "weft.revision-report-codec.v1", policy ?? new());

    private static ValidationCacheKey ValidationKey(NativeArtifact artifact, RevisionReportBackendProfile profile, RevisionReportPolicy? policy = null) =>
        new(Source(artifact), profile, "weft.mapping-validation.v1", "weft.ferrule-validation.v1", "weft.revision-report-codec.v1", policy ?? new());

    private static async Task WaitForRunningContainerAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = await InspectContainerAsync(name, timeout.Token);
            if (state.ExitCode == 0)
            {
                using var parsed = JsonDocument.Parse(state.Output);
                if (parsed.RootElement.GetProperty("Running").GetBoolean())
                    return;
            }
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException("The owned test worker did not reach its running state.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> InspectContainerAsync(string name, CancellationToken cancellationToken = default)
    {
        Assert.StartsWith("weft-inspect-", name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(name["weft-inspect-".Length..], "N", out _));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (var argument in new[] { "--host", "unix:///var/run/docker.sock", "container", "inspect", "--format", "{{json .State}}", name })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(DockerInspectionWorkerOptions.KillTimeout);
            }
        }
    }

    // Withhold EOF once at the real process port. No response, native bytes or daemon operation is substituted.
    private sealed class HoldingInputLauncher : IWorkerProcessLauncher
    {
        private int held;
        internal TaskCompletionSource InputWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? ContainerName { get; private set; }

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var name = Array.IndexOf(arguments, "--name");
            if (name >= 0)
                ContainerName = arguments[name + 1];
            var hold = arguments.Contains("--attach", StringComparer.Ordinal) && Interlocked.Exchange(ref held, 1) == 0;
            var process = new Process { StartInfo = startInfo };
            process.Start();
            return new HeldProcess(process, hold ? new HeldInput(process.StandardInput.BaseStream, InputWritten) : process.StandardInput.BaseStream, hold);
        }
    }

    private sealed class HeldProcess(Process process, Stream input, bool holdInputOpen) : IWorkerProcess
    {
        public Stream StandardInput => input;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput() { if (!holdInputOpen) process.StandardInput.Dispose(); }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose() { process.StandardInput.Dispose(); process.Dispose(); }
    }

    private sealed class HeldInput(Stream inner, TaskCompletionSource written) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken) { await inner.FlushAsync(cancellationToken); written.TrySetResult(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
