using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

// Offline boundary tests. These do not qualify PostgreSQL execution, races, grants or deparse forms.
public sealed class PostgresChatEditProposalContractTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:000000000000}");
    private static readonly ChatConversationKey Conversation = ChatConversationKey.ForWorkspace(Id(2), new(Id(1)), Id(6));
    private static ChatMappingEditProposal Proposal() => new(Conversation, Id(7), Id(8), "call-one", 2,
        new(NativeArtifact.Admit(new(Id(2), Id(3), Id(4), Id(5)), "exact native bytes"u8, 100),
            MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)), [new("field", "before", "after")]));

    private static JsonObject Response(ChatMappingEditProposal p, string status = "AwaitingApproval")
    {
        var encoding = ChatMappingEditProposalStorage.Encode(p);
        return new()
        {
            ["facts"] = PostgresChatStore.ProposalFacts(p, encoding),
            ["encoding"] = Convert.ToBase64String(encoding),
            ["status"] = status,
            ["operation"] = null,
            ["revision"] = null,
            ["sequence"] = null
        };
    }

    [Fact]
    public async Task Reencoded_content_is_not_verified_generation_and_never_opens_a_connection()
    {
        var p = Proposal();
        var restored = ChatMappingEditProposalStorage.RestoreFromTrustedStorage(ChatMappingEditProposalStorage.Encode(p), ChatMappingEditStorageIdentity.FromProposal(p));
        Assert.Null(restored.Content.VerifiedGeneration);
        Assert.Empty(typeof(VerifiedMappingEditGeneration).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(MappingEditProposal).GetProperty(nameof(MappingEditProposal.VerifiedGeneration))!.SetMethod!.IsPublic);
        await using var database = new PostgresChatDatabase("Host=127.0.0.1;Port=1;Database=unused;Username=braid_chat_writer;Timeout=1", ChatDatabaseProfile.Writer);
        var result = await new PostgresChatStore(database).CreateAsync(new(Conversation.Creator), restored, default);
        Assert.Equal(ChatMappingEditCreateOutcome.Refused, result.Outcome);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task Model_writer_cannot_approve_even_with_exact_fingerprint()
    {
        var p = Proposal();
        await using var database = new PostgresChatDatabase("Host=127.0.0.1;Port=1;Database=unused;Username=braid_chat_writer;Timeout=1", ChatDatabaseProfile.Writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresChatStore(database).ApproveAndPrepareAsync(new(Conversation.Creator), Conversation,
            p.TurnId, p.ProposalId, p.Fingerprint, default));
        Assert.DoesNotContain(ChatSchemaContract.FunctionGrants(ChatDatabaseProfile.Writer), f => f.Contains("chat_edit_approve", StringComparison.Ordinal));
        Assert.DoesNotContain(ChatSchemaContract.FunctionGrants(ChatDatabaseProfile.Writer), f => f.Contains("chat_mapping_write_prepare", StringComparison.Ordinal));
        Assert.Equal(new[] { "braid.chat_edit_approve(uuid,uuid,uuid,uuid,uuid,text)", "braid.chat_edit_list(uuid,uuid,uuid,uuid)",
            "braid.chat_edit_read(uuid,uuid,uuid,uuid,uuid)", "braid.chat_schema_lock()" },
            ChatSchemaContract.FunctionGrants(ChatDatabaseProfile.Approver).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Independent_facts_restore_exact_content_without_minting_generation()
    {
        var p = Proposal();
        var state = PostgresChatStore.DecodeProposal(Response(p).ToJsonString(), Conversation, p.TurnId, p.ProposalId)!;
        Assert.Equal(p.Fingerprint, state.Proposal.Fingerprint);
        Assert.Equal(p.Content.Checkpoint.CopyBytes(), state.Proposal.Content.Checkpoint.CopyBytes());
        Assert.Equal(p.Content.Operations.CopyUtf8(), state.Proposal.Content.Operations.CopyUtf8());
        Assert.Null(state.Proposal.Content.VerifiedGeneration);
        Assert.Null(state.Receipt);
    }

    [Theory]
    [InlineData("creator")]
    [InlineData("conversation")]
    [InlineData("turn")]
    [InlineData("proposal")]
    [InlineData("nativeDigest")]
    [InlineData("operationsDigest")]
    [InlineData("encodingDigest")]
    [InlineData("fingerprint")]
    public void Storage_substitution_is_refused_without_private_diagnostics(string fact)
    {
        var p = Proposal(); var response = Response(p);
        var facts = JsonNode.Parse(response["facts"]!.GetValue<string>())!;
        facts[fact] = fact.EndsWith("Digest", StringComparison.Ordinal) || fact == "fingerprint" ? new string('a', 64) : Id(99).ToString();
        response["facts"] = facts.ToJsonString();
        var error = Assert.Throws<InvalidOperationException>(() => PostgresChatStore.DecodeProposal(response.ToJsonString(), Conversation, p.TurnId, p.ProposalId));
        Assert.Equal("Private chat stored facts are invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Approved", false, false, true)]
    [InlineData("Pending", true, false, true)]
    [InlineData("Saved", true, true, true)]
    [InlineData("Pending", false, false, false)]
    [InlineData("Saved", false, true, false)]
    [InlineData("Saved", true, false, false)]
    [InlineData("AwaitingApproval", true, false, false)]
    [InlineData("Approved", true, false, false)]
    public void Receipt_and_status_must_agree(string status, bool operation, bool revision, bool accepted)
    {
        var p = Proposal(); var response = Response(p, status);
        if (operation) response["operation"] = Id(90);
        if (revision) { response["revision"] = Id(91); response["sequence"] = 3; }
        if (!accepted)
        {
            Assert.Throws<InvalidOperationException>(() => PostgresChatStore.DecodeProposal(response.ToJsonString(), Conversation, p.TurnId, p.ProposalId));
            return;
        }
        var state = PostgresChatStore.DecodeProposal(response.ToJsonString(), Conversation, p.TurnId, p.ProposalId)!;
        Assert.Equal(Enum.Parse<ChatMappingEditStatus>(status), state.Status);
        Assert.Equal(p.RequestKey, state.Receipt!.Key);
    }

    [Fact]
    public void Migration_is_pinned_and_discovery_and_approval_are_separate_capabilities()
    {
        using var stream = typeof(PostgresChatStore).Assembly.GetManifestResourceStream("Braid.Persistence.Postgres.Migrations.0043_chat_edit_proposals.sql")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        Assert.Equal(ChatSchemaContract.EditProposalMigrationChecksum, Sha256Digest.FromBytes(bytes.ToArray()).Value);
        var sql = Encoding.UTF8.GetString(bytes.ToArray());
        Assert.Contains("LIMIT 8", sql, StringComparison.Ordinal);
        Assert.Contains("q.state IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("c.retain_until>clock_timestamp() AND braid.chat_authorized(p_org,p.project_id,p_creator)", sql, StringComparison.Ordinal);
        var approve = sql.Split("AS $chat_edit_approve$", StringSplitOptions.None)[1].Split("$chat_edit_approve$;", StringSplitOptions.None)[0];
        Assert.DoesNotContain("t.phase", approve, StringComparison.Ordinal);
        Assert.DoesNotContain("r.outcome", approve, StringComparison.Ordinal);
        Assert.True(approve.IndexOf("('Saved','NotSaved','OutOfDate')", StringComparison.Ordinal) < approve.IndexOf("m.head_revision_id", StringComparison.Ordinal));
        Assert.Contains("INSERT INTO braid.chat_edit_approvals", approve, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO braid.chat_mapping_write_intents", approve, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO braid.chat_mapping_write_content", approve, StringComparison.Ordinal);
        foreach (var refusal in new[] { "NotPermitted", "OutOfDate" })
        {
            var end = approve.IndexOf($"RETURN jsonb_set(observed::jsonb,'{{status}}','\"{refusal}\"')", StringComparison.Ordinal);
            Assert.True(end > 0);
            var prefix = approve[..end];
            var refresh = prefix.LastIndexOf("observed:=braid.chat_edit_read(", StringComparison.Ordinal);
            Assert.True(refresh > approve.IndexOf("('Saved','NotSaved','OutOfDate')", StringComparison.Ordinal));
            Assert.Contains("IF observed IS NULL THEN RETURN NULL; END IF;", prefix[refresh..], StringComparison.Ordinal);
            Assert.Contains("IF observed::jsonb->>'status'='Saved' THEN RETURN observed; END IF;", prefix[refresh..], StringComparison.Ordinal);
        }
        Assert.Contains("intent.request_key IS NULL OR p.request_key IS NULL", ChatSchemaContract.MappingCommitTriggerBody, StringComparison.Ordinal);
    }
}
