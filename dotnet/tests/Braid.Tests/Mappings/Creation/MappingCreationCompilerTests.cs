using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class MappingCreationCompilerTests
{
    [MappingAuthoringBackendTests.NativeAuthoringFact]
    public Task Actual_compiler_accepts_multiple_distinct_source_bindings() => CompileMultiple(false);

    [MappingAuthoringBackendTests.NativeAuthoringFact]
    public Task Actual_compiler_accepts_same_source_bound_to_multiple_targets() => CompileMultiple(true);

    private static async Task CompileMultiple(bool sharedSource)
    {
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var bundle = new SpecBundleScope(project, Guid.NewGuid());
        var source = new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Bind each output explicitly"u8);
        var extraction = SpecSourceExtractor.Extract(source);
        var specification = new CreationSpecification(bundle, [source], [extraction]);
        var plan = new MappingCreationPlan(
            sharedSource ? [new("a", CreationScalarType.Text, true)] : [new("a", CreationScalarType.Text, true), new("b", CreationScalarType.Text, true)],
            [new("x", CreationScalarType.Text, true), new("y", CreationScalarType.Text, true)],
            [new("a", "x"), new(sharedSource ? "a" : "b", "y")]);
        var authoring = Guid.NewGuid();
        var backend = new FerruleInspectionBackend(new MappingAuthoringBackendTests.NativeRunner());

        // Demonstrate that the prior emitter really fails in pinned Ferrule, not just in a contract mock.
        var broken = JsonNode.Parse(plan.CopyCompilerPlanBytes())!;
        broken["edit"]!["operations"]![1]!["expression"]!["id"] = "1";
        var rejected = await backend.AuthorAsync(project, authoring, JsonSerializer.SerializeToUtf8Bytes(broken));
        Assert.Null(rejected.Checkpoint);
        Assert.Equal("operationRejected", rejected.Failure!.Code);
        Assert.Equal("1", rejected.Failure.OperationIndex);

        var compiler = new FerruleMappingCreationCompiler(backend);
        var compiled = await compiler.CompileAsync(project, authoring, plan, specification.Snapshot, default);
        Assert.Null(compiled.Failure);
        var checkpoint = Assert.IsType<CreationCompilation>(compiled.Checkpoint);
        Assert.Equal(project, checkpoint.Project); Assert.Equal(authoring, checkpoint.AuthoringId);
        Assert.Equal(plan.CompilerDigest, checkpoint.Plan); Assert.Equal(specification.Snapshot.Digest, checkpoint.Snapshot);
        Assert.Equal(Sha256Digest.FromBytes(checkpoint.CopyNative()), checkpoint.NativeDigest);
        var validation = JsonSerializer.Deserialize<MappingAuthoringValidation>(checkpoint.CopyValidation())!;
        var projection = JsonSerializer.Deserialize<MappingAuthoringProjection>(checkpoint.CopyProjection())!;
        Assert.True(validation.IsValid);
        Assert.Equal(2, projection.Rules.Count);
        Assert.Equal(2, projection.Rules.Select(r => r.OutputFieldId).Distinct().Count());

        // The real adapter is also used by generation, preserving source attribution and exact compiled native.
        var actor = new ActorContext(new(Guid.NewGuid()));
        var generated = await new MappingCreationGenerationService(compiler, new Current()).GenerateAsync(actor, authoring, Guid.NewGuid(),
            specification, plan, new([new(source.Identity, 0)], [], []), new("Generated mapping", "Two reviewed bindings"));
        Assert.Equal(checkpoint.CopyNative(), generated.Compilation.CopyNative());
        Assert.Equal(checkpoint.ValidationDigest, generated.Compilation.ValidationDigest);
        Assert.Equal(checkpoint.ProjectionDigest, generated.Compilation.ProjectionDigest);
        var replay = await compiler.CompileAsync(project, authoring, plan, specification.Snapshot, default);
        Assert.Null(replay.Failure);
        Assert.Equal(checkpoint.CopyNative(), replay.Checkpoint!.CopyNative());
    }

    [Fact]
    public async Task Foreign_snapshot_is_refused_before_worker_dispatch()
    {
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var source = new SpecCapturedSource(new(new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1), SpecSourceFormat.Text, "evidence"u8);
        var snapshot = new SpecBundleSnapshot(source.Identity.Bundle, [SpecSourceExtractor.Extract(source).Identity]);
        var compiler = new FerruleMappingCreationCompiler(new FerruleInspectionBackend(new NeverRun()));
        await Assert.ThrowsAsync<ArgumentException>(() => compiler.CompileAsync(project, Guid.NewGuid(), Plan(), snapshot, default));
    }

    [Fact]
    public async Task Backend_failure_remains_failure_without_native_evidence()
    {
        var source = new SpecCapturedSource(new(new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1), SpecSourceFormat.Text, "evidence"u8);
        var snapshot = new SpecBundleSnapshot(source.Identity.Bundle, [SpecSourceExtractor.Extract(source).Identity]);
        var compiler = new FerruleMappingCreationCompiler(new FerruleInspectionBackend(new NeverRun()));
        var result = await compiler.CompileAsync(snapshot.Bundle.Project, Guid.NewGuid(), Plan(), snapshot, new CancellationToken(true));
        Assert.Null(result.Checkpoint); Assert.Equal(IngestionBackendStatus.Canceled, result.Failure!.Status);
    }

    private static MappingCreationPlan Plan() => new([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]);
    private sealed class Current : IMappingCreationAuthority
    {
        public Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct) => Task.FromResult(CreationAuthority.Current);
    }
    private sealed class NeverRun : IInspectionWorkerRunner
    {
        public string BuildIdentity => "must-not-dispatch";
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => throw new InvalidOperationException("Unexpected dispatch.");
    }
}
