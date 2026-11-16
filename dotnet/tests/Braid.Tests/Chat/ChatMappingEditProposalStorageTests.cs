using System.Text;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class ChatMappingEditProposalStorageTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:000000000000}");
    private static readonly RevisionScope Base = new(Id(2), Id(3), Id(4), Id(5));
    private static readonly ChatConversationKey Conversation = ChatConversationKey.ForWorkspace(Id(2), new PrincipalId(Id(1)), Id(6));
    private static ChatMappingEditProposal Proposal(IEnumerable<MappingEditChange>? changes = null, string call = "call-one") =>
        new(Conversation, Id(7), Id(8), call, 2, new(NativeArtifact.Admit(Base, "exact native bytes"u8, 100),
            MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)), changes ?? [new("field", "before", "after")]));

    // Fixed cross-language fixtures: SQL must hash these exact identity bytes, not jsonb's rendering.
    [Theory]
    [InlineData("call-one", "call-one", "4fb2e865-a735-87f0-8198-0dc6bc5d5d83", "81357350-83fd-8595-9414-062de8fd7dac",
        "23bf960688dad651aec0730ea8b9cef6dca21d92daca189051043a3b4ee15c30")]
    [InlineData("call\"\\é", "call\\u0022\\\\\\u00E9", "e6245c65-312d-84ed-a701-0d8ea9252f20", "de63b949-9252-8120-907d-d3d5405a7b82",
        "33873d833225a83cd12afce9caa44d1dbe6c9784e295c54563322f5e330c4417")]
    public void Fixed_call_identity_vectors_for_persistence(string call, string escaped, string proposalId, string requestKey, string fingerprint)
    {
        var p = Proposal(call: call);
        var identity = "[\"00000000-0000-4000-8000-000000000002\",\"00000000-0000-4000-8000-000000000001\",\"00000000-0000-4000-8000-000000000006\",\"00000000-0000-4000-8000-000000000007\",\"00000000-0000-4000-8000-000000000008\",\"" + escaped + "\"]";
        Assert.Equal(identity, p.CanonicalIdentity);
        Assert.Equal(Guid.Parse(proposalId), p.ProposalId);
        Assert.Equal(Guid.Parse(requestKey), p.RequestKey);
        Assert.Equal(MappingEditScriptTests.Bind, Encoding.UTF8.GetString(p.Content.Operations.CopyUtf8()));
        Assert.Equal(fingerprint, p.Fingerprint.Value);
    }

    [Fact]
    public void Roundtrip_retains_full_script_native_summaries_and_copy_ownership()
    {
        var proposal = Proposal(call: "call\"\\é");
        var expected = ChatMappingEditStorageIdentity.FromProposal(proposal);
        var encoded = ChatMappingEditProposalStorage.Encode(proposal);
        var restored = ChatMappingEditProposalStorage.RestoreFromTrustedStorage(encoded, expected);
        Assert.Equal(expected, ChatMappingEditStorageIdentity.FromProposal(restored));
        Assert.Equal(proposal.Content.Operations.CopyUtf8(), restored.Content.Operations.CopyUtf8());
        Assert.Equal(proposal.Content.Checkpoint.CopyBytes(), restored.Content.Checkpoint.CopyBytes());
        Assert.Equal(proposal.Content.CopyChangesUtf8(), restored.Content.CopyChangesUtf8());
        Assert.Equal(encoded, ChatMappingEditProposalStorage.Encode(restored));
        Array.Fill(encoded, (byte)0);
        Array.Fill(restored.Content.Operations.CopyUtf8(), (byte)0);
        Array.Fill(restored.Content.Checkpoint.CopyBytes(), (byte)0);
        Array.Fill(restored.Content.CopyChangesUtf8(), (byte)0);
        Assert.Equal(expected, ChatMappingEditStorageIdentity.FromProposal(restored));
    }

    [Fact]
    public void Every_expected_immutable_fact_is_mandatory()
    {
        var proposal = Proposal(); var expected = ChatMappingEditStorageIdentity.FromProposal(proposal);
        var bytes = ChatMappingEditProposalStorage.Encode(proposal);
        var otherDigest = Sha256Digest.FromBytes("different"u8);
        var mismatches = new[]
        {
            expected with { Conversation = ChatConversationKey.ForWorkspace(Id(22), Conversation.Creator, Id(6)) },
            expected with { Conversation = ChatConversationKey.ForWorkspace(Id(2), new PrincipalId(Id(22)), Id(6)) },
            expected with { Conversation = ChatConversationKey.ForWorkspace(Id(2), Conversation.Creator, Id(22)) },
            expected with { TurnId = Id(22) }, expected with { RoundId = Id(22) }, expected with { CallId = "other" },
            expected with { ProposalId = Id(22) }, expected with { RequestKey = Id(22) }, expected with { Fingerprint = otherDigest },
            expected with { Base = new(Id(22), Id(3), Id(4), Id(5)) },
            expected with { Base = new(Id(2), Id(22), Id(4), Id(5)) },
            expected with { Base = new(Id(2), Id(3), Id(22), Id(5)) },
            expected with { Base = new(Id(2), Id(3), Id(4), Id(22)) }, expected with { BaseSequence = 3 },
            expected with { OperationsDigest = otherDigest }, expected with { OperationsByteCount = expected.OperationsByteCount + 1 },
            expected with { OperationCount = 2 }, expected with { NativeDigest = otherDigest }, expected with { NativeByteCount = 1 },
            expected with { ChangesDigest = otherDigest }, expected with { ChangesByteCount = 1 },
            expected with { ChangeCount = 2 }, expected with { ChangesTruncated = true }
        };
        foreach (var mismatch in mismatches)
            Assert.Throws<ArgumentException>(() => ChatMappingEditProposalStorage.RestoreFromTrustedStorage(bytes, mismatch));
        Assert.Throws<ArgumentNullException>(() => ChatMappingEditProposalStorage.RestoreFromTrustedStorage(bytes, null!));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("native")]
    [InlineData("operations")]
    [InlineData("noncanonicalOperations")]
    [InlineData("summary")]
    [InlineData("truncated")]
    [InlineData("extraMember")]
    [InlineData("oversizedNative")]
    [InlineData("oversizedOperations")]
    [InlineData("oversizedSummaries")]
    [InlineData("oversizedSummaryBytes")]
    public void Corrupt_unknown_noncanonical_and_oversized_content_is_refused(string mutation)
    {
        var p = Proposal(); var expected = ChatMappingEditStorageIdentity.FromProposal(p);
        var root = JsonNode.Parse(ChatMappingEditProposalStorage.Encode(p))!.AsArray();
        switch (mutation)
        {
            case "version": root[0] = "braid.chat-edit-storage.v2"; break;
            case "native": root[12] = Convert.ToBase64String("different native"u8); break;
            case "operations": root[11] = Convert.ToBase64String(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind.Replace("code", "other", StringComparison.Ordinal))); break;
            case "noncanonicalOperations": root[11] = Convert.ToBase64String(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(p.Content.Operations.CopyUtf8()))); break;
            case "summary": root[13]![0]![2] = "changed"; break;
            case "truncated": root[14] = true; break;
            case "extraMember": root.Add(1); break;
            case "oversizedNative": root[12] = Convert.ToBase64String(new byte[MappingEditPolicy.MaximumNativeBytes + 1]); break;
            case "oversizedOperations": root[11] = Convert.ToBase64String(new byte[MappingEditPolicy.MaximumOperationsBytes + 1]); break;
            case "oversizedSummaries":
                root[13] = new JsonArray(Enumerable.Range(0, MappingEditProposal.MaximumChanges + 1)
                    .Select(_ => (JsonNode)new JsonArray("field", "before", "after")).ToArray());
                break;
            case "oversizedSummaryBytes":
                root[13] = new JsonArray(Enumerable.Range(0, MappingEditProposal.MaximumChanges)
                    .Select(_ => (JsonNode)new JsonArray(new string('é', 256), new string('é', 256), new string('é', 256))).ToArray());
                break;
        }
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        Assert.Throws<ArgumentException>(() => ChatMappingEditProposalStorage.RestoreFromTrustedStorage(bytes, expected));
    }

    [Fact]
    public void Encoding_itself_must_be_canonical_bounded_and_well_formed()
    {
        var p = Proposal(); var expected = ChatMappingEditStorageIdentity.FromProposal(p);
        var bytes = ChatMappingEditProposalStorage.Encode(p);
        foreach (var invalid in new[] { Array.Empty<byte>(), "not JSON"u8.ToArray(), bytes[..^1],
            new byte[ChatMappingEditProposalStorage.MaximumEncodedBytes + 1], " "u8.ToArray().Concat(bytes).ToArray() })
            Assert.Throws<ArgumentException>(() => ChatMappingEditProposalStorage.RestoreFromTrustedStorage(invalid, expected));
    }

    [Fact]
    public void Summaries_are_a_deterministic_bounded_prefix_and_truncation_is_explicit()
    {
        var changes = Enumerable.Range(0, 100).Select(i => new MappingEditChange($"field{i}", "before", "after")).ToArray();
        var p = Proposal(changes); var again = Proposal(changes);
        Assert.Equal(MappingEditProposal.MaximumChanges, p.Content.Changes.Length);
        Assert.True(p.Content.ChangesTruncated);
        Assert.Equal(changes.Take(MappingEditProposal.MaximumChanges), p.Content.Changes);
        Assert.Equal(p.Fingerprint, again.Fingerprint);
        Assert.NotEqual(p.Fingerprint, Proposal(changes.Take(MappingEditProposal.MaximumChanges)).Fingerprint);
        Assert.Equal(Proposal().Content.Operations.CopyUtf8(), p.Content.Operations.CopyUtf8());
        var restored = ChatMappingEditProposalStorage.RestoreFromTrustedStorage(ChatMappingEditProposalStorage.Encode(p), ChatMappingEditStorageIdentity.FromProposal(p));
        Assert.True(restored.Content.ChangesTruncated);

        var large = new MappingEditChange(new string('é', 256), new string('é', 256), new string('é', 256));
        var byteLimited = Proposal(Enumerable.Repeat(large, 64));
        Assert.True(byteLimited.Content.ChangesTruncated);
        Assert.True(byteLimited.Content.Changes.Length < MappingEditProposal.MaximumChanges);
        Assert.InRange(byteLimited.Content.CopyChangesUtf8().Length, 1, MappingEditProposal.MaximumChangesBytes);
        Assert.True(byteLimited.Content.CopyChangesUtf8().Length + Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(new[] { large.Field, large.Before, large.After })) + 1 > MappingEditProposal.MaximumChangesBytes);
    }

    [Fact]
    public void Summary_changes_change_v2_content_identity_but_not_call_identity()
    {
        var p = Proposal(); var changed = Proposal([new("field", "before", "different")]);
        Assert.Equal(p.ProposalId, changed.ProposalId); Assert.Equal(p.RequestKey, changed.RequestKey);
        Assert.NotEqual(p.Fingerprint, changed.Fingerprint);
        Assert.Throws<ArgumentException>(() => ChatMappingEditProposalStorage.RestoreFromTrustedStorage(
            ChatMappingEditProposalStorage.Encode(changed), ChatMappingEditStorageIdentity.FromProposal(p)));
        Assert.Throws<ArgumentException>(() => Proposal([new("field", "before", new string('x', 257))]));
        Assert.Throws<ArgumentException>(() => Proposal([new("field", "before", "\ud800")]));
        Assert.ThrowsAny<ArgumentException>(() => Proposal(call: "call\ud800"));
    }
}
