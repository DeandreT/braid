using System.Text;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class FerruleBuildCompilerTests
{
    [Fact]
    public async Task Exact_native_source_and_generated_assembly_are_bound_to_the_complete_intent()
    {
        var test = new Context();
        var capability = await test.Compiler.InspectAsync(test.Intent);
        Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(test.Intent, capability).Outcome);
        var artifact = await test.Compiler.BuildAsync(test.Intent, new(test.Native, test.Native.Length));
        Assert.Equal(test.Assembly, artifact.Copy());
        Assert.Equal(new[] { "inspect", "compile" }, test.Runner.Requests.Select(r => r.Operation));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("revision")]
    [InlineData("native")]
    [InlineData("intent")]
    [InlineData("engine")]
    [InlineData("profile")]
    public async Task A_substituted_reply_cannot_become_capability_evidence(string field)
    {
        var test = new Context();
        test.Runner.Change = response => field switch
        {
            "request" => response with { RequestId = Guid.NewGuid().ToString("D") },
            "revision" => response with { Revision = new(response.Revision!.OrganizationId, response.Revision.ProjectId, response.Revision.MappingId, Guid.NewGuid()) },
            "native" => response with { NativeDigest = new string('b', 64) },
            "intent" => response with { IntentDigest = new string('b', 64) },
            "engine" => response with { EngineRevision = "different" },
            _ => response with { Profile = "different" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Compiler.InspectAsync(test.Intent));
    }

    [Fact]
    public async Task Generation_refusal_remains_unsupported_not_a_build_success()
    {
        var test = new Context();
        test.Runner.Change = response => response with { Status = "unsupported", Boundaries = [], Files = [] };
        Assert.Equal(CapabilityStatus.Unsupported, (await test.Compiler.InspectAsync(test.Intent)).Status);
    }

    [Fact]
    public async Task Boundary_inventory_mismatch_refuses_pure_preflight()
    {
        var test = new Context();
        test.Runner.Change = response => response with { Boundaries = [.. response.Boundaries, new("namedOutput", "unrequested", "json")] };
        var capability = await test.Compiler.InspectAsync(test.Intent);
        Assert.Equal(BuildPreflightOutcome.MismatchedEvidence, new BuildPreflightService().Evaluate(test.Intent, capability).Outcome);
    }

    [Fact]
    public async Task Unverified_source_and_revoked_admission_do_not_reach_worker_transport()
    {
        var test = new Context(); test.Allowed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Compiler.InspectAsync(test.Intent));
        Assert.Empty(test.Runner.Requests);
        test.Allowed = true; test.Corrupt = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Compiler.InspectAsync(test.Intent));
        Assert.Empty(test.Runner.Requests);
    }

    [Fact]
    public async Task Compile_refuses_nonassembly_or_tampered_frame_content()
    {
        var test = new Context(); test.Runner.Body = Encoding.UTF8.GetBytes("not an assembly");
        await Assert.ThrowsAsync<BadImageFormatException>(() => test.Compiler.BuildAsync(test.Intent, new(test.Native, test.Native.Length)));
        test.Runner.Tamper = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Compiler.BuildAsync(test.Intent, new(test.Native, test.Native.Length)));
    }

    [Fact]
    public async Task Requested_cancellation_propagates_instead_of_becoming_a_capability()
    {
        var test = new Context(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Compiler.InspectAsync(test.Intent, canceled.Token));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../GeneratedMapping.cs")]
    [InlineData("Runtime/../unsafe.cs")]
    [InlineData("Runtime\\unsafe.cs")]
    [InlineData("Runtime/x.cs:alternate")]
    [InlineData("./x.cs")]
    public void Source_paths_are_portable_relative_identities_not_arbitrary_paths(string path) => Assert.False(BuildWorkerProtocol.SafePath(path));

    [Fact]
    public void Extra_frames_or_duplicate_JSON_fields_are_not_admitted()
    {
        var test = new Context(); var request = BuildWorkerProtocol.For(test.Intent, "inspect");
        var encoded = BuildWorkerProtocol.EncodeRequest(request, test.Native);
        Assert.Throws<InvalidOperationException>(() => BuildWorkerProtocol.DecodeRequest([.. encoded, 1]));
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(0, 4));
        var json = Encoding.UTF8.GetString(encoded, 4, length).Replace("{", "{\"profile\":\"duplicate\",", StringComparison.Ordinal);
        using var bytes = new MemoryStream();
        var newHeader = Encoding.UTF8.GetBytes(json);
        Span<byte> prefix = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, newHeader.Length);
        bytes.Write(prefix); bytes.Write(newHeader); bytes.Write(encoded.AsSpan(4 + length));
        Assert.Throws<InvalidOperationException>(() => BuildWorkerProtocol.DecodeRequest(bytes.ToArray()));
    }

    private sealed class Context : IBuildAdmissionReader, IPrivateObjectStore
    {
        internal readonly byte[] Native = Encoding.UTF8.GetBytes("synthetic-native");
        internal readonly byte[] Assembly = System.IO.File.ReadAllBytes(typeof(BuildIntent).Assembly.Location);
        internal readonly ActorContext Actor = new(new(Guid.NewGuid()));
        internal readonly Runner Runner;
        internal readonly FerruleBuildCompiler Compiler;
        internal readonly BuildIntent Intent;
        private readonly PrivateObjectReference reference;
        internal bool Allowed = true;
        internal bool Corrupt;
        internal Context()
        {
            Runner = new() { Body = Assembly };
            Compiler = new(Runner, new(Actor, this, this));
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            reference = new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(Native), Native.Length);
            Intent = new(Guid.NewGuid(), scope, reference.Digest, Compiler.Backend, FerruleBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        }
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<BuildAdmission?>(Allowed ? new(Actor.PrincipalId, intent.Fingerprint, reference) : null); }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference expected, CancellationToken token = default) => Task.FromResult(Corrupt ? new byte[] { 1 } : Native.ToArray());
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference expected, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Runner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        internal readonly List<BuildWorkerProtocol.Request> Requests = [];
        internal byte[] Body = [];
        internal bool Tamper;
        internal Func<BuildWorkerProtocol.Response, BuildWorkerProtocol.Response> Change = v => v;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var request = BuildWorkerProtocol.DecodeRequest(frame.ToArray()).Header; Requests.Add(request);
            var compile = request.Operation == "compile";
            var response = Change(new(BuildWorkerProtocol.Protocol, request.RequestId, request.Revision, request.NativeDigest, request.IntentDigest,
                BuildWorkerProtocol.Engine, BuildWorkerProtocol.Profile, "supported", [new("primaryInput", null, "json"), new("primaryOutput", null, "json")],
                compile ? [new("Ferrule.Generated.dll", Body.Length, Sha256Digest.FromBytes(Body).Value)] : []));
            var bytes = BuildWorkerProtocol.EncodeReply(response, compile ? [Body] : []);
            if (Tamper) bytes[^1] ^= 1;
            return Task.FromResult(new WorkerProcessResult(bytes, WorkerProcessFailure.None));
        }
    }
}
