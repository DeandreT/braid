using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class MappingCreationWorkflowTests
{
    [Fact]
    public async Task Generation_retention_discovery_and_unapproved_recovery_never_ingest()
    {
        var f = new Fixture(); var p = await f.Generate();
        await f.Service.RetainAsync(f.Actor, p);
        Assert.NotNull(await f.Service.ReadAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        await f.Service.RecoverAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        var preview = await new MappingCreationPreviewService(f, f).PreviewAsync(f.Actor, p, "sample"u8.ToArray());
        Assert.NotNull(preview);
        Assert.Equal(p.Compilation.NativeDigest, preview.Subject.Native);
        Assert.Equal(CreationPreviewOutcome.Succeeded, preview.Outcome);
        Assert.Equal(0, f.Creates); Assert.Equal(0, f.Resumes); Assert.Equal(0, f.Acquires);
        Assert.Equal("braid.creation-plan.v1", System.Text.Json.JsonDocument.Parse(p.Plan.CopyBytes()).RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public async Task Explicit_approval_retains_bytes_key_and_metadata_across_lost_ack_and_replay()
    {
        var f = new Fixture(); var p = await f.Generate(); await f.Service.RetainAsync(f.Actor, p);
        f.LoseAck = true;
        var first = await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.NotNull(first!.Receipt);
        var operation = first.Receipt.OperationId;
        var second = await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.Equal(operation, second!.Receipt!.OperationId);
        Assert.Equal(1, f.Reservations);
        Assert.All(f.Keys, key => Assert.Equal(p.RequestKey, key));
        Assert.All(f.Bytes, bytes => Assert.Equal(p.Compilation.CopyNative(), bytes));
        Assert.All(f.Requests, request => Assert.Equal(p.Request, request));
        var recovered = await f.Service.RecoverAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.Equal(operation, recovered!.Receipt!.OperationId);
        Assert.Equal(2, f.Creates); Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task Preview_checks_exact_executed_native_and_current_authority_without_reserving()
    {
        var f = new Fixture(); var p = await f.Generate(); var preview = new MappingCreationPreviewService(f, f);
        f.WrongPreviewNative = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.PreviewAsync(f.Actor, p, "sample"u8.ToArray()));
        f.WrongPreviewNative = false; f.AfterPreview = () => f.Authority = CreationAuthority.Revoked;
        Assert.Null(await preview.PreviewAsync(f.Actor, p, "sample"u8.ToArray()));
        Assert.Equal(0, f.Creates); Assert.Equal(0, f.Reservations); Assert.Null(f.State);
    }

    [Fact]
    public async Task Approved_but_unreserved_recovery_does_not_bridge_to_create()
    {
        var f = new Fixture(); var p = await f.Generate(); f.State = new(p, new(p.Fingerprint, []));
        await f.Service.RecoverAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.Equal(0, f.Creates); Assert.Equal(0, f.Acquires);
        await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.Equal(1, f.Creates);
    }

    [Theory]
    [InlineData(CreationAuthority.Stale)]
    [InlineData(CreationAuthority.Revoked)]
    public async Task Current_authority_required_after_generation_and_before_approval(CreationAuthority status)
    {
        var f = new Fixture(); f.AfterCompile = () => f.Authority = status;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate());
        f.AfterCompile = null; f.Authority = CreationAuthority.Current;
        var p = await f.Generate(); await f.Service.RetainAsync(f.Actor, p); f.Authority = status;
        Assert.Null(await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task Critical_questions_are_reviewable_but_never_approved()
    {
        var f = new Fixture(); var p = await f.Generate(critical: true); await f.Service.RetainAsync(f.Actor, p);
        var result = await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.False(result!.State.Approved); Assert.Equal(0, f.Acquires); Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task Critical_assumptions_require_exact_explicit_authenticated_approval_decisions()
    {
        var f = new Fixture(); var p = await f.Generate(criticalAssumption: true); await f.Service.RetainAsync(f.Actor, p);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint, new(p.Fingerprint, ["other"])));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint, new(Sha256Digest.FromBytes("other"u8), ["a1"])));
        Assert.Equal(0, f.Acquires); Assert.Equal(0, f.Creates);
        var saved = await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint, new(p.Fingerprint, ["a1"]));
        Assert.Equal(new[] { "a1" }, saved!.State.Approval!.AcceptedCriticalAssumptions);
        Assert.Equal(1, f.Creates);
        await f.Service.RecoverAsync(f.Actor, p.Project, p.Id, p.Fingerprint);
        Assert.Equal(1, f.Creates); Assert.Equal(1, f.Resumes);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("project")]
    [InlineData("fingerprint")]
    [InlineData("id")]
    public async Task Substituted_store_identity_is_rejected_before_ingestion(string substitution)
    {
        var f = new Fixture(); var p = await f.Generate(); f.State = new(p);
        var actor = substitution == "author" ? new ActorContext(new(Guid.NewGuid())) : f.Actor;
        var project = substitution == "project" ? new ProjectScope(Guid.NewGuid(), Guid.NewGuid()) : p.Project;
        var digest = substitution == "fingerprint" ? Sha256Digest.FromBytes("other"u8) : p.Fingerprint;
        var id = substitution == "id" ? Guid.NewGuid() : p.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApproveAsync(actor, project, id, digest));
        Assert.Equal(0, f.Acquires); Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task Native_source_plan_and_metadata_are_immutable_and_fingerprint_bound()
    {
        var f = new Fixture(); var p = await f.Generate(); var same = await f.Generate();
        Assert.Equal(p.Fingerprint, same.Fingerprint);
        p.Compilation.CopyNative()[0] ^= 1; p.Plan.CopyBytes()[0] ^= 1; p.Specification.Sources[0].CopyBytes()[0] ^= 1;
        Assert.Equal(p.Fingerprint, (await f.Generate()).Fingerprint);
        Assert.NotEqual(p.Fingerprint, (await f.Generate(name: "other")).Fingerprint);
        f.Native = "different native"u8.ToArray(); Assert.NotEqual(p.Fingerprint, (await f.Generate()).Fingerprint);
        f.SubstituteCompiler = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate());
    }

    [Fact]
    public async Task Source_and_evidence_substitutions_fail_before_compiler_dispatch()
    {
        var f = new Fixture();
        var other = new SpecCapturedSource(f.Source.Identity, SpecSourceFormat.Text, "other bytes"u8);
        Assert.Throws<ArgumentException>(() => new CreationSpecification(f.Bundle, [other], [SpecSourceExtractor.Extract(f.Source)]));
        var notes = new CreationGenerationNotes([new(new(f.Bundle, Guid.NewGuid(), 1), 0)], [], []);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Generation.GenerateAsync(f.Actor, f.Id, f.Key, f.Specification, f.Plan, notes, new("name", "description")));
        Assert.Equal(0, f.Compiles);
    }

    [Fact]
    public void Typed_plan_rejects_unknown_types_unbound_required_targets_and_native_expressions()
    {
        Assert.Throws<ArgumentException>(() => new MappingCreationPlan([new("a", (CreationScalarType)99, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]));
        Assert.Throws<ArgumentException>(() => new MappingCreationPlan([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], []));
        Assert.Throws<ArgumentException>(() => new MappingCreationPlan([new("eval(x)", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("eval(x)", "b")]));
    }

    [Fact]
    public void Model_bytes_admit_only_the_bounded_typed_schema()
    {
        var f = new Fixture(); var bytes = f.Plan.CopyBytes();
        Assert.Equal(f.Plan.Digest, MappingCreationPlan.Parse(bytes).Digest);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var bad in new[] { json.Replace("\"schema\":", "\"native\":{},\"schema\":"),
            json.Replace("\"format\":", "\"format\":\"json-record\",\"format\":"),
            json.Replace("braid.creation-plan.v1", "braid.creation-plan.v2") })
            Assert.Throws<ArgumentException>(() => MappingCreationPlan.Parse(System.Text.Encoding.UTF8.GetBytes(bad)));
        Assert.Throws<ArgumentException>(() => MappingCreationPlan.Parse(new byte[MappingCreationPlan.MaximumBytes + 1]));
    }

    [Fact]
    public void Compiler_plan_uses_actual_authoring_contract_and_native_evidence_cannot_be_publicly_constructed()
    {
        var plan = new Fixture().Plan;
        using var doc = System.Text.Json.JsonDocument.Parse(plan.CopyCompilerPlanBytes());
        var root = doc.RootElement;
        Assert.Equal("braid.authoring.v1", root.GetProperty("format").GetString());
        Assert.Equal("json", root.GetProperty("source").GetProperty("options").GetProperty("kind").GetString());
        Assert.Equal("string", root.GetProperty("source").GetProperty("schema").GetProperty("shape").GetProperty("children")[0].GetProperty("shape").GetProperty("scalar").GetString());
        Assert.Equal("ferrule.edit.v1", root.GetProperty("edit").GetProperty("format").GetString());
        Assert.Equal("input", root.GetProperty("edit").GetProperty("operations")[0].GetProperty("expression").GetProperty("path")[0].GetString());
        Assert.Equal(plan.CompilerDigest, Sha256Digest.FromBytes(plan.CopyCompilerPlanBytes()));
        Assert.Empty(typeof(CreationCompilation).GetConstructors());
        Assert.Empty(typeof(MappingCreationProposal).GetConstructors());
        Assert.Empty(typeof(CreationPreviewEvidence).GetConstructors());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("authoring")]
    [InlineData("snapshot")]
    [InlineData("failure")]
    [InlineData("inconsistent")]
    public async Task Compiler_checkpoint_identity_and_failure_cannot_mint_a_proposal(string fault)
    {
        var f = new Fixture { CompilerFault = fault };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate());
        Assert.Null(f.State); Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task Retention_replays_exact_intent_and_conflicts_on_changed_metadata()
    {
        var f = new Fixture(); var p = await f.Generate();
        var first = await f.Service.RetainAsync(f.Actor, p);
        Assert.Same(first, await f.Service.RetainAsync(f.Actor, await f.Generate()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await f.Service.RetainAsync(f.Actor, await f.Generate(name: "changed")));
        Assert.Equal(0, f.Creates);
    }

    [Fact]
    public async Task Receipt_for_another_creation_cannot_be_resumed_or_reported()
    {
        var first = new Fixture(); var p = await first.Generate(); await first.Service.RetainAsync(first.Actor, p);
        await first.Service.ApproveAsync(first.Actor, p.Project, p.Id, p.Fingerprint);
        var other = new Fixture(); var q = await other.Generate(); other.State = new(q, new(q.Fingerprint, [])); other.Durable = first.Durable;
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Service.RecoverAsync(other.Actor, q.Project, q.Id, q.Fingerprint));
        Assert.Equal(0, other.Creates); Assert.Equal(0, other.Resumes);
    }

    [Fact]
    public async Task Revocation_after_an_ambiguous_create_hides_retained_content()
    {
        var f = new Fixture(); var p = await f.Generate(); await f.Service.RetainAsync(f.Actor, p);
        f.AfterCreate = () => f.Authority = CreationAuthority.Revoked; f.LoseAck = true;
        Assert.Null(await f.Service.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        Assert.Equal(1, f.Reservations);
        Assert.Null(await f.Service.RecoverAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        Assert.Equal(0, f.Resumes);
    }

    private sealed class Fixture : IMappingCreationCompiler, IMappingCreationAuthority, IMappingCreationProposalStore,
        IMappingCreationApprovalStore, IRevisionIngestionService, IIngestionWorkflowReader, IMappingCreationPreview
    {
        internal readonly ActorContext Actor = new(new(Guid.NewGuid()));
        internal readonly Guid Id = Guid.NewGuid(), Key = Guid.NewGuid();
        internal readonly SpecBundleScope Bundle = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        internal readonly MappingCreationPlan Plan = new([new("input", CreationScalarType.Text, true)], [new("output", CreationScalarType.Text, true)], [new("input", "output")]);
        internal readonly SpecCapturedSource Source;
        internal readonly CreationSpecification Specification;
        internal MappingCreationState? State;
        internal IngestionReceipt? Durable;
        internal CreationAuthority Authority = CreationAuthority.Current;
        internal Action? AfterCompile, AfterCreate, AfterPreview;
        internal bool WrongPreviewNative;
        internal bool LoseAck, SubstituteCompiler;
        internal string? CompilerFault;
        internal int Creates, Resumes, Reservations, Acquires, Compiles;
        internal byte[] Native = "compiled native fixture"u8.ToArray();
        internal readonly List<Guid> Keys = [];
        internal readonly List<byte[]> Bytes = [];
        internal readonly List<CreateRevisionRequest> Requests = [];
        internal MappingCreationApprovalService Service => new(this, this, this, this);
        internal MappingCreationGenerationService Generation => new(this, this);
        internal Fixture()
        {
            Source = new(new(Bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Map input to output"u8);
            Specification = new(Bundle, [Source], [SpecSourceExtractor.Extract(Source)]);
        }
        internal Task<MappingCreationProposal> Generate(string name = "Mapping", bool critical = false, bool criticalAssumption = false) => Generation.GenerateAsync(Actor, Id, Key,
            Specification, Plan, new([new(Source.Identity, 0)], [new("a1", "input is text", criticalAssumption)], critical ? [new("What is the output?", true)] : []), new(name, "Description"));
        public Task<CreationCompilationResult> CompileAsync(ProjectScope project, Guid authoringId, MappingCreationPlan plan, SpecBundleSnapshot snapshot, CancellationToken ct)
        {
            Compiles++; AfterCompile?.Invoke();
            if (CompilerFault == "inconsistent") return Task.FromResult(new CreationCompilationResult(null, null));
            if (CompilerFault == "failure") return Task.FromResult(new CreationCompilationResult(null, new("invalidAuthoring", "fixture", false, null, IngestionBackendStatus.InvalidMapping)));
            return Task.FromResult(new CreationCompilationResult(new CreationCompilation(
                CompilerFault == "project" ? new(Guid.NewGuid(), Guid.NewGuid()) : project,
                CompilerFault == "authoring" ? Guid.NewGuid() : authoringId,
                SubstituteCompiler ? Sha256Digest.FromBytes("wrong"u8) : plan.CompilerDigest,
                CompilerFault == "snapshot" ? Sha256Digest.FromBytes("wrong"u8) : snapshot.Digest,
                "fixture-engine", "fixture-worker", Native, "{}"u8, "{}"u8), null));
        }
        public Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct) => Task.FromResult(Authority);
        public Task<CreationPreviewEvidence> RunAsync(MappingCreationProposal proposal, ReadOnlyMemory<byte> sample, CancellationToken ct)
        {
            AfterPreview?.Invoke();
            var subject = new CreationPreviewSubject(proposal.Project, proposal.Id, proposal.Fingerprint,
                WrongPreviewNative ? Sha256Digest.FromBytes("wrong"u8) : proposal.Compilation.NativeDigest, Sha256Digest.FromBytes(sample.Span));
            return Task.FromResult(new CreationPreviewEvidence(subject, CreationPreviewOutcome.Succeeded, "preview only"u8, "csv", "text/csv"));
        }
        public Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct)
        {
            if (State is not null && State.Proposal.Fingerprint != proposal.Fingerprint) throw new InvalidOperationException("conflict");
            State ??= new(proposal); return Task.FromResult<MappingCreationState?>(State);
        }
        public Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope project, Guid proposalId, CancellationToken ct) =>
            Task.FromResult(Authority == CreationAuthority.Revoked ? null : State);
        public Task<IMappingCreationApprovalLease?> AcquireAsync(ActorContext actor, ProjectScope project, Guid proposalId, Sha256Digest fingerprint, CreationApprovalDecision? decision, CancellationToken ct)
        {
            Acquires++;
            if (Authority != CreationAuthority.Current || State is null || decision is null && !State.Approved) return Task.FromResult<IMappingCreationApprovalLease?>(null);
            State = State with { Approval = decision ?? State.Approval }; return Task.FromResult<IMappingCreationApprovalLease?>(new Lease(State));
        }
        private sealed class Lease(MappingCreationState state) : IMappingCreationApprovalLease
        {
            public MappingCreationState State => state;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        public async Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default)
        {
            Creates++; Keys.Add(idempotencyKey); Requests.Add(request);
            using var buffer = new MemoryStream(); await nativeSource.CopyToAsync(buffer, cancellationToken); Bytes.Add(buffer.ToArray());
            var intent = IngestionIntent.ForCreate(request.Metadata, request.BackendId, request.OriginalFilename, Sha256Digest.FromBytes(buffer.ToArray()), buffer.Length);
            if (Durable is null)
            {
                Reservations++;
                var scope = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
                var reference = new PrivateObjectReference(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), intent.NativeDigest, intent.NativeByteCount);
                var facts = new ReservedIngestionFacts(project, Guid.NewGuid(), actor.PrincipalId, idempotencyKey,
                    CanonicalFingerprint.Create(actor.PrincipalId, project, intent), intent, scope, reference,
                    new("ferrule", "test", "test", "test"), new(IngestionAdmissionPolicy.InitialIdentity, 4 * 1024 * 1024), DateTimeOffset.UtcNow);
                Durable = new(facts, IngestionPhase.Reserved, 0, 0, null, null, null, []);
            }
            AfterCreate?.Invoke();
            if (LoseAck) throw new IOException("ack lost");
            return RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, RevisionIngestionReceipt.FromMetadata(Durable));
        }
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Durable!.Facts.OperationId, operationId); Resumes++;
            return Task.FromResult(RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, RevisionIngestionReceipt.FromMetadata(Durable)));
        }
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey, AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Creation must not append.");
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project, IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken = default)
        {
            Assert.Equal(IngestionOperationKind.Create, kind); Assert.Null(target); return Task.FromResult(Durable);
        }
        public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
