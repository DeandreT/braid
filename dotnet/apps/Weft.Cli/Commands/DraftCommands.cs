using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Editing;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

/// <summary>Submits typed intent files to the server; no native codec or editing core is linked into the CLI.</summary>
internal static class DraftCommands
{
    private sealed record Scope(Option<string> Project, Option<string> Mapping)
    {
        internal Symbol[] Symbols => [Project, Mapping];
        internal async Task<ApiRequest> Request(CliInvocation invocation, ApiOperation operation, string? draft = null)
        {
            var request = (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(Project)).Path("mappingId", invocation.Id(Mapping));
            return draft is null ? request : request.Path("draftId", draft);
        }
    }
    private sealed record View(MappingDraftV1 Draft, string? ETag);

    public static Command Drafts(CliGlobals globals)
    {
        var scope = new Scope(Required("--project", "The project ID"), Required("--mapping", "The mapping ID"));
        var baseRevision = Required("--base-revision", "The exact immutable base revision ID");
        var key = Optional("--request-id", "A UUID for idempotent creation or saving; reuse it to recover the original result");
        var create = new Command("create", "Create an author-private mapping draft and its version-zero checkpoint").With([.. scope.Symbols, baseRevision, key]);
        create.Handle(globals, async invocation =>
        {
            var request = (await scope.Request(invocation, ApiOperations.CreateMappingDraft)).Header("Idempotency-Key", invocation.OptionalId(key) ?? CatalogCommands.NewId())
                .Body(CatalogCommands.JsonBody(new MappingDraftCreateV1(invocation.Id(baseRevision)), ApiOperations.CreateMappingDraft));
            return await ReadResult(invocation, request);
        });
        var list = new Command("list", "List your own open drafts").With(scope.Symbols);
        list.Handle(globals, async invocation =>
        {
            var result = await invocation.Api.ReadAsync<MappingDraftListV1>(await scope.Request(invocation, ApiOperations.ListMappingDrafts), invocation.Cancellation);
            invocation.Output.Write(result, output => output.Table(["DRAFT", "BASE", "VERSION", "OPERATIONS", "STATE"],
                result.Drafts.Select(draft => (IReadOnlyList<object?>)[draft.Id, draft.Base.Revision.RevisionId, draft.Version, draft.OperationCount, draft.State])));
        });

        var id = Argument("draftId", "Your draft ID");
        var show = new Command("show", "Show draft metadata and its strong ETag").With([.. scope.Symbols, id]);
        show.Handle(globals, async invocation =>
        {
            var view = await Read(invocation, scope, invocation.Id(id));
            invocation.Output.Write(view, output => Describe(output, view.Draft, view.ETag));
        });
        var ifMatch = Optional("--if-match", "Use this strong ETag (default: the draft tag just read)");
        var discard = new Command("discard", "Delete your draft's operation log and checkpoint").With([.. scope.Symbols, id, ifMatch]);
        discard.Handle(globals, async invocation =>
        {
            var draft = invocation.Id(id);
            var current = await Read(invocation, scope, draft);
            var request = (await scope.Request(invocation, ApiOperations.DiscardMappingDraft, draft)).Header("If-Match", Tag(invocation.Value(ifMatch) ?? current.ETag));
            return await ReadResult(invocation, request);
        });

        var getOps = new Command("get", "Write the effective ferrule.edit.v1 log as JSON").With([.. scope.Symbols, id]);
        getOps.Handle(globals, async invocation =>
        {
            var request = await scope.Request(invocation, ApiOperations.GetMappingDraftOperations, invocation.Id(id));
            var script = await invocation.Api.ReadAsync<EditScriptV1>(request, invocation.Cancellation);
            invocation.Output.Write(script, output => output.Line(JsonSerializer.Serialize(script, WeftApiClient.Json)));
        });
        var file = Argument("file", "A ferrule.edit.v1 operation file, or - for standard input");
        var putOps = new Command("put", "Autosave a complete typed operation log with If-Match").With([.. scope.Symbols, id, file, ifMatch]);
        putOps.Handle(globals, async invocation =>
        {
            var bytes = await ReadInputAsync(invocation, invocation.Value(file)!, 1024 * 1024, "operation log");
            JsonElement script;
            try { using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }); script = document.RootElement.Clone(); }
            catch (JsonException) { throw CliException.Usage("The operation log must be JSON in ferrule.edit.v1 format."); }
            var draft = invocation.Id(id);
            var current = await Read(invocation, scope, draft);
            var request = (await scope.Request(invocation, ApiOperations.PutMappingDraftOperations, draft))
                .Header("If-Match", Tag(invocation.Value(ifMatch) ?? current.ETag)).Body(CatalogCommands.JsonBody(script, ApiOperations.PutMappingDraftOperations));
            return await ReadResult(invocation, request);
        });
        var checkpoint = new Command("checkpoint", "Replay, encode, validate and project the current draft version").With([.. scope.Symbols, id]);
        checkpoint.Handle(globals, async invocation =>
        {
            var draft = invocation.Id(id);
            var current = await Read(invocation, scope, draft);
            var request = (await scope.Request(invocation, ApiOperations.CheckpointMappingDraft, draft))
                .Body(CatalogCommands.JsonBody(new MappingDraftVersionV1(current.Draft.Version), ApiOperations.CheckpointMappingDraft));
            return await ReadResult(invocation, request);
        });
        var filename = Optional("--filename", "The saved revision's display filename");
        var compare = new Command("compare", "Compare your current checkpoint with its exact immutable base").With([.. scope.Symbols, id]);
        compare.Handle(globals, async invocation =>
        {
            var result = await invocation.Api.ReadAsync<RevisionComparisonV1>(await scope.Request(invocation, ApiOperations.CompareMappingDraft, invocation.Id(id)), invocation.Cancellation);
            invocation.Output.Write(result, output => ReportCommands.WriteComparison(output, result));
        });
        var save = new Command("save", "Conditionally append the current nonempty checkpoint on its exact base").With([.. scope.Symbols, id, key, filename]);
        save.Handle(globals, async invocation =>
        {
            var draft = invocation.Id(id);
            var current = await Read(invocation, scope, draft);
            var request = (await scope.Request(invocation, ApiOperations.SaveMappingDraft, draft))
                .Header("Idempotency-Key", invocation.OptionalId(key) ?? current.Draft.SavingRequestId ?? CatalogCommands.NewId())
                .Body(CatalogCommands.JsonBody(new MappingDraftSaveV1(current.Draft.Version, invocation.Value(filename) ?? current.Draft.SavingFilename), ApiOperations.SaveMappingDraft));
            var result = await Upload.SendAsync(invocation, request);
            invocation.Output.Write(result, output => output.Fields(("Outcome", result.Outcome), ("Operation", result.Receipt?.OperationId), ("Revision", result.CommittedRevisionUrl)));
            return (int)(result.Outcome switch
            {
                CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid or CatalogRevisionIngestionOutcomeV1.InProgress => CliExitCode.Success,
                CatalogRevisionIngestionOutcomeV1.HeadConflict or CatalogRevisionIngestionOutcomeV1.IdempotencyConflict => CliExitCode.Conflict,
                CatalogRevisionIngestionOutcomeV1.Rejected => CliExitCode.InvalidRequest,
                CatalogRevisionIngestionOutcomeV1.RetryableFailure => CliExitCode.Unavailable,
                CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound => CliExitCode.NotFound,
                _ => CliExitCode.OperationFailed
            });
        });
        var target = Required("--target-revision", "The explicitly selected current head revision ID");
        var rebase = new Command("rebase", "Replay all operations on the selected current head, or change nothing").With([.. scope.Symbols, id, target, ifMatch]);
        rebase.Handle(globals, async invocation =>
        {
            var draft = invocation.Id(id);
            var current = await Read(invocation, scope, draft);
            var request = (await scope.Request(invocation, ApiOperations.RebaseMappingDraft, draft)).Header("If-Match", Tag(invocation.Value(ifMatch) ?? current.ETag))
                .Body(CatalogCommands.JsonBody(new MappingDraftRebaseV1(invocation.Id(target)), ApiOperations.RebaseMappingDraft));
            return await ReadResult(invocation, request);
        });
        return new Command("drafts", "Author-private exact-base mapping edits").With(create, list, show, discard,
            new Command("ops", "Effective typed operation log").With(getOps, putOps), checkpoint, compare, PreviewCommands.Create(globals, draft: true), save, rebase);
    }

    private static async Task<View> Read(CliInvocation invocation, Scope scope, string id)
    {
        using var response = await invocation.Api.SendAsync(await scope.Request(invocation, ApiOperations.GetMappingDraft, id), invocation.Cancellation);
        await response.RequireSuccessAsync(invocation.Cancellation);
        return new(await response.ReadJsonAsync<MappingDraftV1>(invocation.Cancellation), response.Message.Headers.ETag?.ToString());
    }
    private static string Tag(string? tag) => EntityTagHeaderValue.TryParse(tag, out var parsed) && !parsed.IsWeak && parsed.Tag != "*"
        ? parsed.ToString() : throw CliException.Usage("--if-match must be one strong draft ETag, including its quotes.");
    private static async Task<int> ReadResult(CliInvocation invocation, ApiRequest request)
    {
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        if (response.IsSuccess)
        {
            Write(invocation, await response.ReadJsonAsync<MappingDraftResultV1>(invocation.Cancellation));
            return (int)CliExitCode.Success;
        }
        if (response.Status is 409 or 422 or 503 && response.Message.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var bytes = await response.ReadBytesAsync(32 * 1024, invocation.Cancellation);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("outcome", out var outcome)
                && outcome.GetString() is "draftExists" or "conflict" or "emptyOperations" or "checkpointRequired" or "replayFailed")
            {
                var result = JsonSerializer.Deserialize<MappingDraftResultV1>(bytes, WeftApiClient.Json)!;
                Write(invocation, result);
                return (int)ApiResponse.Failure(response.Status, result.Outcome).Code;
            }
        }
        await response.RequireSuccessAsync(invocation.Cancellation);
        return (int)CliExitCode.ServerFailure;
    }
    private static void Write(CliInvocation invocation, MappingDraftResultV1 result) => invocation.Output.Write(result, output =>
    {
        output.Line(result.Outcome);
        if (result.Draft is { } draft) Describe(output, draft, null);
        if (result.Failure is { } failure) output.Fields(("Failure", failure.Code), ("Operation index", failure.OperationIndex), ("Reason", failure.Message));
    });
    private static void Describe(CliOutput output, MappingDraftV1 draft, string? tag) => output.Fields(("Draft", draft.Id), ("State", draft.State),
        ("Base", draft.Base.Revision.RevisionId), ("Version", draft.Version), ("Operations", draft.OperationCount),
        ("Checkpoint", draft.Checkpoint?.Version), ("Valid", draft.Checkpoint?.IsValid), ("Issues", draft.Checkpoint?.TotalIssueCount), ("ETag", tag));
}
