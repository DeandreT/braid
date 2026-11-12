using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Real PostgreSQL schema/authority tests. Payload and evidence bytes are deliberately
// synthetic trusted-writer inputs; canonical structural restoration is qualified separately.
[Collection(PostgresCollection.Name)]
public sealed class DocumentEvidencePersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Actual_mixed_office_pdf_evidence_roundtrips_typed_proposal_through_restricted_registry_and_approval()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)", test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var bundle = new SpecBundleScope(test.Project, Guid.NewGuid());
        var (template, _, _) = CreationEvidenceGenerationTests.Mixed();
        var sources = template.Sources.Select(s => new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), s.Format, s.CopyBytes())).ToList();
        sources.Add(new(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Pdf, SpecPdfProcessTests.Pdf(["Readable", "", "Last page"])));
        CreationEvidenceDocument[] documents = [CreationEvidenceDocument.From(SpecSourceExtractor.Extract(sources[0])),
            CreationEvidenceDocument.From(SpecOfficeParser.Extract(sources[1], new(maximumChunks: 123))),
            CreationEvidenceDocument.From(SpecOfficeParser.Extract(sources[2], new(maximumEvidenceBytes: 1024 * 1024))),
            CreationEvidenceDocument.From(await CreationEvidenceGenerationTests.ExtractPdf(sources[3], 1024 * 1024))];
        Assert.All(documents.Skip(1), d => Assert.Equal(SpecExtractionOutcome.Partial, d.Outcome));
        var specification = new CreationSpecification(bundle, sources, documents);
        var storeId = Guid.NewGuid();
        var binding = new SpecStoreBinding(storeId, SpecStoreKind.FileSystem, Sha256Digest.FromBytes(storeId.ToByteArray()));
        await test.Case.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,true)", storeId, binding.LocationDigest.Value);
        await using var specDatabase = new PostgresSpecBundleDatabase(test.Case.RegressionConnectionString("weft_spec_writer"), SpecDatabaseProfile.Writer);
        var registry = specDatabase.ForActor(test.Actor, binding);
        var version = (await registry.CreateAsync(bundle, Guid.NewGuid(), default)).Version;
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i]; var document = documents[i];
            foreach (var kind in new[] { SpecObjectKind.Original, SpecObjectKind.Extraction })
            {
                var reference = new SpecObjectReference(new(source.Identity, kind, Guid.NewGuid()),
                    kind == SpecObjectKind.Original ? source.Digest : document.Identity.EvidenceDigest,
                    kind == SpecObjectKind.Original ? source.ByteCount : document.CopyEvidenceBytes().Length);
                var reservation = await registry.ReserveAsync(reference, source.Format, Guid.NewGuid(), version,
                    kind == SpecObjectKind.Original ? null : document.Identity, default);
                version = reservation.BundleVersion;
                // Real registry transaction; physical storage is an explicitly synthetic callback.
                await registry.ExecutePublicationAsync(reservation.Claim, _ => Task.FromResult(SpecPublishResult.Created), default);
            }
        }
        CreationBoundary Boundary(string name) => new(new(name, null, [new("value", CreationValueType.String)]), new CreationJsonOptions());
        var plan = new MappingCreationPlan(new CreationPlanV2(Boundary("Input"), Boundary("Output"), new(null, [new("value", new CreationSourceValue(["value"]))])));
        var id = Guid.NewGuid();
        var notes = CreationEvidenceCoverage.Include(specification, new([new(sources[0].Identity, 0)], [], []));
        var compilation = new CreationCompilation(test.Project, id, plan.CompilerDigest, specification.Snapshot.Digest,
            "synthetic-engine", "synthetic-worker", PostgresIngestionTestSupport.SyntheticBody, "{}"u8, "{}"u8);
        var proposal = new MappingCreationProposal(test.Actor.PrincipalId, id, Guid.NewGuid(), specification, plan, notes, compilation, new(new("Mixed", "Actual parsed evidence; synthetic compilation"), "ferrule"));
        await using var store = new PostgresMappingCreationStore(test.Case.RegressionConnectionString("weft_creation_reader"), test.Case.RegressionConnectionString("weft_creation_writer"), test.Case.RegressionConnectionString("weft_creation_approver"));
        Assert.NotNull(await store.RetainAsync(test.Actor, proposal, default));
        await using (var lease = await store.AcquireAsync(test.Actor, test.Project, id, proposal.Fingerprint,
            new(proposal.Fingerprint, notes.Assumptions.Where(a => a.Critical).Select(a => a.Id)), default))
        {
            Assert.NotNull(lease);
            var restored = (await store.ReadAsync(test.Actor, test.Project, id, default))!.Proposal;
            Assert.Equal(MappingCreationStorageCodec.Encode(proposal), MappingCreationStorageCodec.Encode(restored));
            Assert.Equal(plan.CopyBytes(), restored.Plan.CopyBytes());
            for (var i = 0; i < documents.Length; i++)
            {
                Assert.Equal(documents[i].Identity, restored.Specification.Extractions[i].Identity);
                Assert.Equal(documents[i].CopyEvidenceBytes(), restored.Specification.Extractions[i].CopyEvidenceBytes());
            }
        }
        await test.Case.ExecuteAsync("SELECT * FROM weft.spec_tombstone($1,$2,$3,$4,NULL,NULL,$5,$6)", test.Actor.PrincipalId.Value, test.Project.OrganizationId, test.Project.ProjectId, bundle.BundleId, version, Guid.NewGuid());
        Assert.NotNull(await store.ReadAsync(test.Actor, test.Project, id, default));
        Assert.Null(await store.AcquireAsync(test.Actor, test.Project, id, proposal.Fingerprint, null, default));
    }

    [PostgresFact]
    public async Task Runtime_discovery_contract_enforces_creator_content_store_liveness_and_uuid_keysets()
    {
        await using var test = await fixture.CreateCaseAsync();
        var f = await Seed.Create(test, 6, 4, 5);
        await f.Retain();
        var second = f.ForkProposal();
        await second.Retain();
        const string proposals = "SELECT array_agg(proposal_id ORDER BY proposal_id) FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,20)";
        var expected = await test.ScalarAsync<Guid[]>("SELECT array_agg(id ORDER BY id) FROM weft.creation_proposals");
        Assert.Equal(expected, await f.Scalar<Guid[]>("weft_spec_reader", proposals, f.Actor, f.Org, f.Project));
        Assert.Equal(expected[0], await f.Scalar<Guid>("weft_spec_reader", "SELECT proposal_id FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,1)", f.Actor, f.Org, f.Project));
        Assert.Equal(expected[1], await f.Scalar<Guid>("weft_spec_reader", "SELECT proposal_id FROM weft.creation_proposals_discover_v1($1,$2,$3,$4,1)", f.Actor, f.Org, f.Project, expected[0]));
        foreach (var limit in new[] { 0, 21 })
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", "SELECT * FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,$4)", f.Actor, f.Org, f.Project, limit))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", "SELECT * FROM weft.creation_proposals_discover_v1($1,$2,$3,$4,20)", f.Actor, f.Org, f.Project, Guid.Empty))).SqlState);
        var other = new PostgresSeedData().Admitted;
        await test.ExecuteAsync("INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active) VALUES($1,$2,$3,'viewer',true)", f.Org, f.Project, other);
        await test.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)", f.Org, f.Project, other);
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", "SELECT count(*) FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,20)", other, f.Org, f.Project));
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", f.Actor);
        Assert.Equal(expected, await f.Scalar<Guid[]>("weft_spec_reader", proposals, f.Actor, f.Org, f.Project));

        const string selections = "SELECT count(*) FROM weft.spec_selection_discover_v1($1,$2,$3,$4,NULL,NULL,20)";
        Assert.Equal(1L, await f.Scalar<long>("weft_spec_reader", selections, f.Actor, f.Org, f.Project, f.Store));
        var extraction = await test.ScalarAsync<Guid>("SELECT id FROM weft.spec_objects WHERE kind=2");
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", "SELECT count(*) FROM weft.spec_selection_discover_v1($1,$2,$3,$4,$5,$6,20)", f.Actor, f.Org, f.Project, f.Store, f.Bundle, extraction));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", "SELECT * FROM weft.spec_selection_discover_v1($1,$2,$3,$4,$5,NULL,20)", f.Actor, f.Org, f.Project, f.Store, f.Bundle))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", selections, f.Actor, f.Org, f.Project, Guid.Empty))).SqlState);
        await test.ExecuteAsync("UPDATE weft.spec_objects SET state='pending' WHERE kind=1");
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", selections, f.Actor, f.Org, f.Project, f.Store));
        await test.ExecuteAsync("UPDATE weft.spec_objects SET state='published' WHERE kind=1");
        var foreignStore = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,true)", foreignStore, Sha256Digest.FromBytes(foreignStore.ToByteArray()).Value);
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", selections, f.Actor, f.Org, f.Project, foreignStore));
        await test.ExecuteAsync("UPDATE weft.spec_objects SET store_id=$1 WHERE kind=1", foreignStore);
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", selections, f.Actor, f.Org, f.Project, f.Store));
        await test.ExecuteAsync("UPDATE weft.spec_objects SET store_id=$1 WHERE kind=1", f.Store);
        await test.ExecuteAsync("UPDATE weft.spec_sources SET tombstoned=true");
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", selections, f.Actor, f.Org, f.Project, f.Store));
        Assert.Equal(expected, await f.Scalar<Guid[]>("weft_spec_reader", proposals, f.Actor, f.Org, f.Project));
        await test.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE principal_id=$1", f.Actor);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", selections, f.Actor, f.Org, f.Project, f.Store))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", proposals, f.Actor, f.Org, f.Project))).SqlState);
    }

    [PostgresFact]
    public async Task Typed_creation_citation_disclosure_and_five_rounds_roundtrip_and_replay_with_authority()
    {
        await using var chat = await PostgresChatTestSupport.CreateAsync(fixture);
        var f = await Seed.Create(chat.Case, 4, 3, 5, existing: chat.Seed);
        await f.Retain();
        var key = ChatConversationKey.ForWorkspace(f.Org, chat.Actor.PrincipalId, Guid.NewGuid());
        var now = new DateTimeOffset(await chat.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var conversation = ChatConversationIntent.ForWorkspace(key, null, now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await chat.Writer.CreateAsync(chat.Actor, conversation));
        var id = Guid.NewGuid();
        var intent = new ChatTurnIntent(id, new ChatOriginalEnvelope(id, "synthetic creation", [], new(600_000_000, 5, 1048576, 4194304, 131072, 8, 2048), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
            ChatTurnPolicyTests.Profile(), new(32, 65536, 5), now.AddMinutes(5));
        var started = await chat.Writer.StartAsync(chat.Actor, key, intent);
        Assert.Equal(ChatStoreOutcome.Applied, started.Outcome);
        var facts = started.Facts!;
        var citation = ChatWorkspaceCitationFact.CreationProposal(new(f.Org, f.Project), f.Id, Sha256Digest.Parse(f.Digest));
        var validated = ChatValidatedCitation.ForReadResource(conversation, citation);
        var disclosure = new ChatPayloadDisclosureFact(f.Project, ChatPayloadSource.Specification, f.Bundle, null, 1);
        var instanceId = Guid.NewGuid();
        for (var ordinal = 1; ordinal <= 5; ordinal++)
        {
            var round = PostgresChatTestSupport.Round(instanceId);
            var reserved = await chat.Writer.ReserveRoundAsync(chat.Actor, key, id, facts.Version, round);
            Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
            Assert.Equal(ordinal, reserved.Round!.Ordinal);
            facts = reserved.Turn!;
            if (ordinal == 4)
            {
                var citationIntent = new ChatCitationEventData(Guid.NewGuid(), validated);
                var written = await chat.Writer.AppendCitationAsync(chat.Actor, key, id, facts.Version, round.RoundId, round.Fence, citationIntent);
                Assert.Equal(ChatStoreOutcome.Applied, written.Outcome);
                Assert.Equal(ChatStoreOutcome.Replayed, (await chat.Writer.AppendCitationAsync(chat.Actor, key, id, facts.Version, round.RoundId, round.Fence, citationIntent)).Outcome);
                facts = written.Facts!;
                var data = ChatEventData.ForDataShared(Guid.NewGuid(), disclosure);
                var appended = await chat.Writer.AppendAsync(chat.Actor, key, id, facts.Version, round.RoundId, round.Fence, data);
                Assert.Equal(ChatStoreOutcome.Applied, appended.Outcome);
                Assert.Equal(ChatStoreOutcome.Replayed, (await chat.Writer.AppendAsync(chat.Actor, key, id, facts.Version, round.RoundId, round.Fence, data)).Outcome);
                facts = appended.Facts!;
            }
            var completed = await chat.Writer.CompleteRoundAsync(chat.Actor, key, id, facts.Version, round.RoundId, round.Fence,
                ordinal < 5 ? ChatRoundOutcome.ToolCalls : ChatRoundOutcome.Stopped);
            Assert.Equal(ChatStoreOutcome.Applied, completed.Outcome);
            facts = completed.Facts!;
        }
        var events = await chat.Reader.ReadEventsAsync(chat.Actor, key, id, 0, 32);
        Assert.Equal(citation, Assert.Single(events, e => e.Kind == ChatEventKind.WorkspaceCitation).WorkspaceCitation);
        Assert.Equal(disclosure, Assert.Single(events, e => e.Kind == ChatEventKind.DataShared).DataShared);
        Assert.Equal(5, await chat.Case.ScalarAsync<int>("SELECT max(ordinal) FROM weft.chat_rounds WHERE turn_id=$1", id));
        Assert.Equal(1L, await chat.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_payload_audit WHERE source_kind=5 AND turn_id=$1", id));
        var other = new ActorContext(new PrincipalId(Guid.NewGuid()));
        Assert.Empty(await chat.Reader.ReadEventsAsync(other, key, id, 0, 32));
    }

    [PostgresFact]
    public async Task Creation_citation_metadata_has_closed_canonical_identity_and_fingerprint()
    {
        await using var chat = await PostgresChatTestSupport.CreateAsync(fixture);
        var f = await Seed.Create(chat.Case, 4, 3, 5, existing: chat.Seed);
        var citation = new System.Text.Json.Nodes.JsonObject
        {
            ["v"] = 1,
            ["kind"] = 12,
            ["organization"] = f.Org.ToString("D"),
            ["project"] = f.Project.ToString("D"),
            ["subject"] = f.Id.ToString("D"),
            ["proposalFingerprint"] = f.Digest,
            ["attempt"] = null,
            ["digest"] = null,
            ["entry"] = null,
            ["key"] = null,
            ["mapping"] = null,
            ["ordinal"] = null,
            ["resource"] = null,
            ["revision"] = null,
            ["section"] = null,
            ["targetKind"] = null,
            ["version"] = null
        };
        Task<bool> Valid(System.Text.Json.Nodes.JsonObject value) => f.Scalar<bool>("weft_chat_writer",
            "SELECT weft.chat_metadata_valid(13,$1,$2)", value.ToJsonString(), Guid.NewGuid());
        Assert.True(await Valid(citation));
        foreach (var property in citation.ToArray())
        {
            var missing = (System.Text.Json.Nodes.JsonObject)citation.DeepClone();
            missing.Remove(property.Key);
            Assert.False(await Valid(missing));
        }
        foreach (var (name, value) in new (string, object)[]
        {
            ("proposalFingerprint", new string('A', 64)), ("proposalFingerprint", new string('a', 63)),
            ("kind", "12"), ("kind", 13), ("subject", Guid.Empty.ToString()),
            ("project", f.Project.ToString("N")), ("mapping", Guid.NewGuid().ToString()),
            ("version", 1), ("extra", true)
        })
        {
            var changed = (System.Text.Json.Nodes.JsonObject)citation.DeepClone();
            changed[name] = JsonSerializer.SerializeToNode(value);
            Assert.False(await Valid(changed));
        }
        var legacy = (System.Text.Json.Nodes.JsonObject)citation.DeepClone();
        legacy["kind"] = 1; legacy["subject"] = null;
        Assert.False(await Valid(legacy));
        legacy.Remove("proposalFingerprint");
        Assert.True(await Valid(legacy));
    }

    [PostgresFact]
    public async Task Retention_is_discoverable_after_lost_ack_and_specification_disclosure_is_audited()
    {
        await using var chat = await PostgresChatTestSupport.CreateAsync(fixture);
        var f = await Seed.Create(chat.Case, 4, 3, 5, existing: chat.Seed);
        var key = ChatConversationKey.ForWorkspace(f.Org, chat.Actor.PrincipalId, Guid.NewGuid());
        var now = new DateTimeOffset(await chat.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        Assert.Equal(ChatStoreOutcome.Applied, await chat.Writer.CreateAsync(chat.Actor, ChatConversationIntent.ForWorkspace(key, null, now.AddHours(1))));
        var turnId = Guid.NewGuid();
        var intent = new ChatTurnIntent(turnId, new ChatOriginalEnvelope(turnId, "workspace specification", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
            ChatTurnPolicyTests.Profile(), new(16, 65536, 3), now.AddMinutes(5));
        var started = await chat.Writer.StartAsync(chat.Actor, key, intent);
        Assert.Equal(ChatStoreOutcome.Applied, started.Outcome);
        var turn = started.Facts!;
        var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await chat.Writer.ReserveRoundAsync(chat.Actor, key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var disclosure = JsonSerializer.Serialize(new { v = 1, project = f.Project, source = 5, subject = f.Bundle, version = (long?)null, bytes = 1 });
        Assert.True(await f.Scalar<bool>("weft_chat_writer", "SELECT weft.chat_metadata_valid(14,$1,$2)", disclosure, round.RoundId));
        await f.Batch("weft_chat_writer",
            ("UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+$1 WHERE organization_id=$2 AND creator_id=$3 AND conversation_id=$4 AND turn_id=$5",
                [System.Text.Encoding.UTF8.GetByteCount(disclosure), f.Org, f.Actor, key.ConversationId, turn.Intent.TurnId]),
            ("INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json) VALUES($1,$2,$3,$4,2,$5,14,'',$6,$7,$8)",
                [f.Org, f.Actor, key.ConversationId, turn.Intent.TurnId, Guid.NewGuid(), round.RoundId, round.Fence, disclosure]));
        Assert.Equal(5, await chat.Case.ScalarAsync<int>("SELECT source_kind FROM weft.chat_payload_audit"));
        await f.Retain();
        await f.Retain();
        Assert.Equal(f.Id, await f.Scalar<Guid>("weft_spec_reader", "SELECT proposal_id FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,20)", f.Actor, f.Org, f.Project));
        Assert.Equal(0L, await f.Scalar<long>("weft_spec_reader", "SELECT count(*) FROM weft.creation_proposals_discover_v1($1,$2,$3,$4,20)", f.Actor, f.Org, f.Project, f.Id));
        Assert.Equal(1L, await chat.Case.ScalarAsync<long>("SELECT count(*) FROM weft.creation_proposals"));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", "SELECT * FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,21)", f.Actor, f.Org, f.Project))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_creation_reader", "SELECT weft.creation_approve($1,$2,$3,$4,$5,$6)", f.Actor, f.Org, f.Project, f.Id, f.Digest, Array.Empty<string>()))).SqlState);
    }

    [PostgresTheory]
    [InlineData(4, 3, 1)]
    [InlineData(5, 3, 1)]
    [InlineData(6, 4, 1)]
    [InlineData(4, 3, 5)]
    [InlineData(5, 3, 5)]
    [InlineData(6, 4, 5)]
    public async Task Structural_selection_is_pinned_approved_discoverable_and_revocable(int format, int extractor, int outcome)
    {
        await using var test = await fixture.CreateCaseAsync();
        var f = await Seed.Create(test, format, extractor, outcome);
        await f.Retain();
        Assert.Equal(outcome, await test.ScalarAsync<int>("SELECT extraction_outcome FROM weft.creation_sources"));
        await f.Call("weft_creation_approver", "SELECT weft.creation_approve($1,$2,$3,$4,$5,$6)", f.Actor, f.Org, f.Project, f.Id, f.Digest, Array.Empty<string>());
        Assert.Equal(1L, await f.Scalar<long>("weft_spec_reader", "SELECT count(*) FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,10)", f.Actor, f.Org, f.Project));
        Assert.Equal(1L, await f.Scalar<long>("weft_creation_reader", "SELECT count(*) FROM weft.creation_read($1,$2,$3,$4)", f.Actor, f.Org, f.Project, f.Id));
        await test.ExecuteAsync("UPDATE weft.spec_objects SET profile_digest=repeat('0',64) WHERE kind=2");
        var drift = await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_creation_approver", "SELECT weft.creation_fence($1,$2,$3,$4,$5)", f.Actor, f.Org, f.Project, f.Id, f.Digest));
        Assert.Equal("42501", drift.SqlState);
        await test.ExecuteAsync("UPDATE weft.spec_objects SET profile_digest=$1 WHERE kind=2", f.Digest);
        await test.ExecuteAsync("SELECT * FROM weft.spec_tombstone($1,$2,$3,$4,NULL,NULL,1,$5)", f.Actor, f.Org, f.Project, f.Bundle, Guid.NewGuid());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_creation_approver", "SELECT weft.creation_fence($1,$2,$3,$4,$5)", f.Actor, f.Org, f.Project, f.Id, f.Digest))).SqlState);
        // Retained discovery survives source retirement, but not content-authority revocation.
        Assert.Equal(1L, await f.Scalar<long>("weft_spec_reader", "SELECT count(*) FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,10)", f.Actor, f.Org, f.Project));
        await test.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE principal_id=$1", f.Actor);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Call("weft_spec_reader", "SELECT * FROM weft.creation_proposals_discover_v1($1,$2,$3,NULL,10)", f.Actor, f.Org, f.Project))).SqlState);
    }

    [PostgresTheory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(6, 3)]
    public async Task Partial_requires_structural_format_and_exact_extractor_family(int format, int extractor)
    {
        await using var test = await fixture.CreateCaseAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => Seed.Create(test, format, extractor, 5))).SqlState);
    }

    [PostgresFact]
    public async Task Format_drift_is_rejected_by_real_foreign_key()
    {
        await using var test = await fixture.CreateCaseAsync();
        await Seed.Create(test, 4, 3, 5);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync("UPDATE weft.spec_sources SET format=5"))).SqlState);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync("UPDATE weft.spec_objects SET source_format=5"))).SqlState);
    }

    [PostgresFact]
    public async Task Disabled_format_foreign_key_and_changed_discovery_acl_fail_admission()
    {
        await using var test = await fixture.CreateCaseAsync();
        await Seed.Create(test, 4, 3, 5);
        await SpecSchemaTestContract.AdmitAsync(test, default);
        await test.BootstrapAsync("ALTER TABLE weft.spec_objects DISABLE TRIGGER ALL");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecSchemaTestContract.AdmitAsync(test, default));
        await test.BootstrapAsync("ALTER TABLE weft.spec_objects ENABLE TRIGGER ALL");
        await SpecSchemaTestContract.AdmitAsync(test, default);
        await test.ExecuteAsync("GRANT EXECUTE ON FUNCTION weft.creation_proposals_discover_v1(uuid,uuid,uuid,uuid,integer) TO weft_creation_reader");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecSchemaTestContract.AdmitAsync(test, default));
    }

    [PostgresFact]
    public async Task Populated_44_then_45_then_46_preserves_original_and_approved_legacy_content()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(44).ToArray())) await runner.MigrateAsync();
        var f = await Seed.Create(test, 1, 1, 1, hasFormatPin: false);
        await using (var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(45).ToArray())) await runner.MigrateAsync();
        await f.Retain();
        await f.Call("weft_creation_approver", "SELECT weft.creation_approve($1,$2,$3,$4,$5,$6)", f.Actor, f.Org, f.Project, f.Id, f.Digest, Array.Empty<string>());
        var history = await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations");
        var content = await test.ScalarAsync<byte[]>("SELECT payload FROM weft.creation_proposals");
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(history, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations WHERE version<=45"));
        Assert.Equal(content, await f.Scalar<byte[]>("weft_creation_reader", "SELECT content FROM weft.creation_read($1,$2,$3,$4)", f.Actor, f.Org, f.Project, f.Id));
        await f.Call("weft_creation_approver", "SELECT weft.creation_fence($1,$2,$3,$4,$5)", f.Actor, f.Org, f.Project, f.Id, f.Digest);
        Assert.Equal(1, await test.ScalarAsync<int>("SELECT min(source_format) FROM weft.spec_objects"));
    }

    private sealed class Seed(PostgresCase test, PostgresSeedData seed, int format, int extractor, int outcome)
    {
        internal Guid Actor => seed.Alice;
        internal Guid Org => seed.OrganizationA;
        internal Guid Project => seed.ProjectA;
        internal Guid Id { get; } = Guid.NewGuid();
        private Guid Key { get; } = Guid.NewGuid();
        internal Guid Bundle { get; private init; } = Guid.NewGuid();
        internal Guid Store { get; } = Guid.NewGuid();
        private Guid Source { get; init; } = Guid.NewGuid();
        internal string Digest { get; } = Sha256Digest.FromBytes("synthetic document schema fixture"u8).Value;
        private string Selection => JsonSerializer.Serialize(new[] { new { source = Source, version = 1, sourceDigest = Digest, sourceCount = 1, format, extractor, profile = Digest, evidence = Digest, evidenceCount = 1, outcome } });
        private object[] Retention => [Actor, Org, Project, Id, Key, Bundle, Digest, Digest, new byte[] { 1 }, Digest, 1, "ferrule", "mapping.json", "Mapping", "Fixture", Array.Empty<string>(), false, Selection];
        internal Seed ForkProposal() => new(test, seed, format, extractor, outcome) { Bundle = Bundle, Source = Source };
        internal Task Retain() => Call("weft_creation_writer", "SELECT weft.creation_retain($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18::jsonb)", Retention);

        internal static async Task<Seed> Create(PostgresCase test, int format, int extractor, int outcome, bool hasFormatPin = true, PostgresSeedData? existing = null)
        {
            var seed = existing ?? await PostgresSeedData.CreateAsync(test);
            var f = new Seed(test, seed, format, extractor, outcome);
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", f.Actor);
            await test.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1) ON CONFLICT DO NOTHING", f.Org, f.Project, f.Actor);
            var store = f.Store;
            await test.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,true)", store, f.Digest);
            await test.ExecuteAsync("INSERT INTO weft.spec_bundles(organization_id,project_id,id,creator_id,creation_key) VALUES($1,$2,$3,$4,$5)", f.Org, f.Project, f.Bundle, f.Actor, Guid.NewGuid());
            await test.ExecuteAsync("INSERT INTO weft.spec_sources(organization_id,project_id,bundle_id,id,version,format,digest,byte_count) VALUES($1,$2,$3,$4,1,$5,$6,1)", f.Org, f.Project, f.Bundle, f.Source, format, f.Digest);
            foreach (var kind in new[] { 1, 2 })
            {
                var columns = hasFormatPin ? ",source_format" : "";
                var value = hasFormatPin ? ",$15" : "";
                object[] args = [f.Org, f.Project, f.Bundle, Guid.NewGuid(), f.Source, kind, f.Digest, store, f.Actor, Guid.NewGuid(),
                    kind == 2 ? extractor : DBNull.Value, kind == 2 ? f.Digest : DBNull.Value, kind == 2 ? outcome : DBNull.Value, kind == 2 ? f.Digest : DBNull.Value];
                if (hasFormatPin) args = [.. args, format];
                await test.ExecuteAsync($"INSERT INTO weft.spec_objects(organization_id,project_id,bundle_id,id,source_id,source_version,kind,digest,byte_count,store_id,owner_id,attempt_id,state,reservation_version,extractor_version,profile_digest,extraction_outcome,source_digest{columns}) VALUES($1,$2,$3,$4,$5,1,$6,$7,1,$8,$9,$10,'published',1,$11::integer,$12::text,$13::integer,$14::text{value})", args);
            }
            return f;
        }

        internal async Task Call(string role, string sql, params object[] args) => await Scalar<object>(role, sql, args);
        internal Task<T> Scalar<T>(string role, string sql, params object[] args) => Run<T>(role, [(sql, args)]);
        internal async Task Batch(string role, params (string Sql, object[] Args)[] statements) => await Run<object>(role, statements);
        private async Task<T> Run<T>(string role, (string Sql, object[] Args)[] statements)
        {
            var connectionString = role == "weft_chat_writer" ? test.ChatWriterConnectionString : test.RegressionConnectionString(role);
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config('weft.chat_principal_id',$2,true)", connection, transaction))
            {
                scope.Parameters.AddWithValue(Org.ToString("D"));
                scope.Parameters.AddWithValue(Actor.ToString("D"));
                await scope.ExecuteNonQueryAsync();
            }
            object? result = null;
            foreach (var statement in statements)
            {
                await using var command = new NpgsqlCommand(statement.Sql, connection, transaction);
                foreach (var value in statement.Args) command.Parameters.AddWithValue(value);
                result = await command.ExecuteScalarAsync();
            }
            await transaction.CommitAsync();
            return (T)result!;
        }
    }
}
