using System.Text;
using System.Text.Json.Nodes;
using Weft.Contracts.Catalog;
using Weft.Contracts.Editing;
using Weft.Contracts.Projections;

namespace Weft.Tests.Cli;

public sealed partial class CliCatalogCommandTests
{
    [Fact]
    public async Task DraftCommandsUseExactScopesVersionsKeysAndTypedOperationFiles()
    {
        var id = CliStubData.Id();
        var key = CliStubData.Id();
        var draft = new MappingDraftV1(id, data.MappingValue().Scope, data.Actor,
            new(data.RevisionValue().Scope, "1", new ArtifactDigest("sha256", new string('a', 64)), 12),
            "open", "3", 1, new ArtifactDigest("sha256", new string('b', 64)), CliStubData.Time, CliStubData.Time,
            CliStubData.Time.AddDays(30), null, null, null);
        var tag = $"\"draft-{id}-3\"";
        stub.Reply("CreateMappingDraft", _ => CliStubReply.Of(201, new MappingDraftResultV1("created", draft)));
        stub.Reply("ListMappingDrafts", _ => CliStubReply.Of(200, new MappingDraftListV1([draft])));
        stub.Reply("GetMappingDraft", _ => CliStubReply.Of(200, draft, ("ETag", tag)));
        foreach (var operation in new[] { "PutMappingDraftOperations", "CheckpointMappingDraft", "RebaseMappingDraft", "DiscardMappingDraft" })
            stub.Reply(operation, _ => CliStubReply.Of(200, new MappingDraftResultV1("succeeded", draft)));
        var script = "{\"format\":\"ferrule.edit.v1\",\"operations\":[{\"op\":\"edit_constant\",\"node\":\"7\",\"value\":{\"type\":\"int\",\"text\":\"9007199254740993\"}}]}";
        stub.Reply("GetMappingDraftOperations", _ => CliStubReply.Of(200, JsonNode.Parse(script)!));
        string[] scope = ["--project", data.Project, "--mapping", data.Mapping];
        var create = await cli.RunAsync(["drafts", "create", .. scope, "--base-revision", draft.Base.Revision.RevisionId, "--request-id", key]);
        Assert.True(create.Exit == 0, create.ToString());
        Assert.Equal(key, stub.Calls("CreateMappingDraft").Single().Headers["Idempotency-Key"]);
        var list = await cli.RunAsync(["drafts", "list", .. scope]); Assert.True(list.Exit == 0, list.ToString());
        var show = await cli.RunAsync(["drafts", "show", id, .. scope]); Assert.True(show.Exit == 0, show.ToString());
        var get = await cli.RunAsync(["drafts", "ops", "get", id, .. scope]); Assert.True(get.Exit == 0, get.ToString());
        Assert.Contains("9007199254740993", get.Out, StringComparison.Ordinal);
        var put = await cli.RunWithInputAsync(Encoding.UTF8.GetBytes(script), ["drafts", "ops", "put", id, "-", .. scope]);
        Assert.True(put.Exit == 0, put.ToString());
        Assert.Equal(tag, stub.Calls("PutMappingDraftOperations").Single().Headers["If-Match"]);
        Assert.Equal("9007199254740993", stub.Calls("PutMappingDraftOperations").Single().Json!["operations"]![0]!["value"]!["text"]!.GetValue<string>());
        var checkpoint = await cli.RunAsync(["drafts", "checkpoint", id, .. scope]); Assert.True(checkpoint.Exit == 0, checkpoint.ToString());
        Assert.Equal("3", stub.Calls("CheckpointMappingDraft").Single().Json!["version"]!.GetValue<string>());
        var rebase = await cli.RunAsync(["drafts", "rebase", id, .. scope, "--target-revision", draft.Base.Revision.RevisionId]); Assert.True(rebase.Exit == 0, rebase.ToString());
        Assert.Equal(tag, stub.Calls("RebaseMappingDraft").Single().Headers["If-Match"]);
        var discard = await cli.RunAsync(["drafts", "discard", id, .. scope]); Assert.True(discard.Exit == 0, discard.ToString());
        Assert.Equal(tag, stub.Calls("DiscardMappingDraft").Single().Headers["If-Match"]);
        var saveKey = CliStubData.Id();
        stub.Reply("SaveMappingDraft", _ => CliStubReply.Of(200, data.Committed(CliStubData.Id())));
        var saved = await cli.RunAsync(["drafts", "save", id, .. scope, "--request-id", saveKey]);
        Assert.True(saved.Exit == 0, saved.ToString());
        Assert.Equal(saveKey, stub.Calls("SaveMappingDraft").Single().Headers["Idempotency-Key"]);
        Assert.Equal("3", stub.Calls("SaveMappingDraft").Single().Json!["version"]!.GetValue<string>());
        var previewRun = data.PreviewRun(CliStubData.Id(), "queued") with
        {
            Revision = null,
            Draft = new(data.MappingScope, id, "3", data.RevisionScope(), new string('a', 64), 12)
        };
        stub.Reply("PreviewMappingDraft", _ => CliStubReply.Of(201, new CatalogPreviewSubmitResultV1("created", previewRun)));
        var preview = await cli.RunWithInputAsync("{}"u8.ToArray(), ["drafts", "preview", .. scope, "--draft", id, "--input", "-", "--request-id", key]);
        Assert.True(preview.Exit == 0, preview.ToString());
        Assert.Equal(id, stub.Calls("PreviewMappingDraft").Single().Path["draftId"]);
        Assert.Equal("e30=", stub.Calls("PreviewMappingDraft").Single().Json!["inputs"]![0]!["content"]!.GetValue<string>());
        stub.Reply("CheckpointMappingDraft", _ => CliStubReply.Of(422, new MappingDraftResultV1("replayFailed", draft,
            new("operationRejected", "The binding is unavailable.", false, "1", "invalidMapping"))));
        var rejected = await cli.RunAsync(["drafts", "checkpoint", id, .. scope, "--json"]);
        Assert.Equal(Weft.Cli.CliExitCode.InvalidRequest, rejected.Code);
        Assert.Equal("1", JsonNode.Parse(rejected.Out)!["failure"]!["operationIndex"]!.GetValue<string>());
        stub.Reply("SaveMappingDraft", _ => CliStubReply.Of(409, data.HeadConflict(CliStubData.Id())));
        var conflict = await cli.RunAsync(["drafts", "save", id, .. scope, "--request-id", saveKey, "--json"]);
        Assert.Equal(Weft.Cli.CliExitCode.Conflict, conflict.Code);
        Assert.Equal("headConflict", JsonNode.Parse(conflict.Out)!["outcome"]!.GetValue<string>());
    }
}
