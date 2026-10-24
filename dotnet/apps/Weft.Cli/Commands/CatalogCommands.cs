using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Contracts.Catalog;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class CatalogCommands
{
    internal const int MaximumNativeBytes = 4_194_304;

    public static Command Projects(CliGlobals globals)
    {
        var paging = new Paging();
        var list = new Command("list", "List the projects you can see").With(paging.Symbols);
        list.Handle(globals, async invocation =>
        {
            var pages = await paging.ReadAsync(invocation, async cursor =>
            {
                var request = (await invocation.RequestAsync(ApiOperations.GetCatalogProjects)).Query("limit", paging.Limit(invocation))
                    .Query("cursor", cursor);
                var page = await invocation.Api.ReadAsync<CatalogProjectPageV1>(request, invocation.Cancellation);
                return (page, page.NextCursor);
            });
            var projects = pages.SelectMany(page => page.Projects).ToArray();
            invocation.Output.Write(paging.Document(invocation, pages, () => new { projects, nextCursor = (string?)null }), output =>
            {
                output.Table(["PROJECT", "NAME", "ROLE", "UPDATED"], projects.Select(project =>
                    (IReadOnlyList<object?>)[project.Scope.ProjectId, project.Name, project.Access.Role, project.UpdatedAtUtc]));
                paging.Footer(output, pages[^1].NextCursor);
            });
        });

        var id = Argument("projectId", "The project ID");
        var get = new Command("get", "Show one project").With(id);
        get.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogProject)).Path("projectId", invocation.Id(id));
            var project = await invocation.Api.ReadAsync<CatalogProjectV1>(request, invocation.Cancellation);
            invocation.Output.Write(project, output => output.Fields(("Project", project.Scope.ProjectId), ("Name", project.Name),
                ("Description", project.Description), ("Role", project.Access.Role), ("Version", project.Version),
                ("Created", project.CreatedAtUtc), ("Updated", project.UpdatedAtUtc)));
        });

        var name = Required("--name", "The project name");
        var description = new Option<string>("--description") { Description = "The project description", DefaultValueFactory = _ => "" };
        var requestId = Optional("--request-id", "A UUID that identifies this creation; reuse it to retry safely (default: new)");
        var create = new Command("create", "Create a project; you become its administrator").With(name, description, requestId);
        create.Handle(globals, async invocation =>
        {
            var key = invocation.OptionalId(requestId) ?? NewId();
            CatalogProjectCreateV1 body;
            try { body = new CatalogProjectCreateV1(invocation.Value(name)!, invocation.Value(description) ?? ""); }
            catch (ArgumentException) { throw CliException.Usage("The project name must be 1-200 characters and the description at most 4000."); }
            var request = (await invocation.RequestAsync(ApiOperations.CreateCatalogProject)).Header("Idempotency-Key", key)
                .Body(JsonBody(body, ApiOperations.CreateCatalogProject));
            var result = await invocation.Api.ReadAsync<CatalogProjectCreationResultV1>(request, invocation.Cancellation);
            invocation.Output.Write(result, output => output.Line(
                $"{(result.Outcome == CatalogProjectCreationOutcomeV1.Created ? "Created" : "Already created")} project {result.Receipt.Project.ProjectId} ({Output.CliOutput.Safe(result.Receipt.Name)})."));
        });

        return new Command("projects", "Projects in your organization").With(list, get, create);
    }

    public static Command Mappings(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var paging = new Paging();
        var list = new Command("list", "List a project's mappings").With([project, .. paging.Symbols]);
        list.Handle(globals, async invocation =>
        {
            var pages = await paging.ReadAsync(invocation, async cursor =>
            {
                var request = (await invocation.RequestAsync(ApiOperations.GetCatalogMappings)).Path("projectId", invocation.Id(project))
                    .Query("limit", paging.Limit(invocation)).Query("cursor", cursor);
                var page = await invocation.Api.ReadAsync<CatalogMappingPageV1>(request, invocation.Cancellation);
                return (page, page.NextCursor);
            });
            var mappings = pages.SelectMany(page => page.Mappings).ToArray();
            invocation.Output.Write(paging.Document(invocation, pages, () => new { mappings, nextCursor = (string?)null }), output =>
            {
                output.Table(["MAPPING", "NAME", "BACKEND", "HEAD REVISION", "SEQ", "ADMISSION"], mappings.Select(mapping =>
                    (IReadOnlyList<object?>)[mapping.Scope.MappingId, mapping.Name, mapping.BackendId, mapping.Head.Scope.RevisionId,
                        mapping.Head.Sequence, mapping.Head.Admission]));
                paging.Footer(output, pages[^1].NextCursor);
            });
        });

        var mappingId = Argument("mappingId", "The mapping ID");
        var get = new Command("get", "Show one mapping").With(project, mappingId);
        get.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogMapping)).Path("projectId", invocation.Id(project))
                .Path("mappingId", invocation.Id(mappingId));
            var mapping = await invocation.Api.ReadAsync<CatalogMappingV1>(request, invocation.Cancellation);
            invocation.Output.Write(mapping, output => output.Fields(("Mapping", mapping.Scope.MappingId), ("Name", mapping.Name),
                ("Description", mapping.Description), ("Backend", mapping.BackendId), ("Head revision", mapping.Head.Scope.RevisionId),
                ("Head sequence", mapping.Head.Sequence), ("Head admission", mapping.Head.Admission), ("Role", mapping.Access.Role),
                ("Version", mapping.Version), ("Updated", mapping.UpdatedAtUtc)));
        });

        var name = Required("--name", "The mapping name");
        var backend = Required("--backend", "The registered backend ID, for example ferrule");
        var description = Optional("--description", "The mapping description");
        var upload = new Upload();
        var create = new Command("create", "Create a mapping from its first native revision")
            .With([project, name, backend, description, .. upload.Symbols]);
        create.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            var request = (await invocation.RequestAsync(ApiOperations.CreateCatalogMapping)).Path("projectId", projectId)
                .Query("name", invocation.Value(name)).Query("backendId", invocation.Value(backend))
                .Query("description", invocation.Value(description));
            return await upload.RunAsync(invocation, projectId, request);
        });

        return new Command("mappings", "Mappings in a project").With(list, get, create, Metadata(globals, project));
    }

    private static Command Metadata(CliGlobals globals, Option<string> project)
    {
        var mappingId = Argument("mappingId", "The mapping ID");
        var get = new Command("get", "Show a mapping's editable metadata and its entity tag").With(project, mappingId);
        get.Handle(globals, async invocation =>
        {
            var (metadata, tag) = await ReadMetadataAsync(invocation, invocation.Id(project), invocation.Id(mappingId));
            invocation.Output.Write(new MetadataView(metadata, tag), output => output.Fields(("Name", metadata.Name),
                ("Description", metadata.Description), ("Version", metadata.Version), ("ETag", tag)));
        });

        var name = Optional("--name", "The new name (default: unchanged)");
        var description = Optional("--description", "The new description (default: unchanged)");
        var ifMatch = Optional("--if-match", "Edit only if the metadata still has this entity tag (default: the tag just read)");
        var edit = new Command("edit", "Edit a mapping's name or description with an If-Match precondition")
            .With(project, mappingId, name, description, ifMatch);
        edit.Handle(globals, async invocation =>
        {
            if (invocation.Value(name) is null && invocation.Value(description) is null)
                throw CliException.Usage("Give --name, --description or both.");
            var projectId = invocation.Id(project);
            var mapping = invocation.Id(mappingId);
            var (current, tag) = await ReadMetadataAsync(invocation, projectId, mapping);
            var precondition = invocation.Value(ifMatch) ?? tag;
            if (!EntityTagHeaderValue.TryParse(precondition, out var parsed) || parsed.IsWeak)
                throw CliException.Usage("--if-match must be one strong entity tag, including its quotes.");
            CatalogMappingMetadataEditV1 body;
            try { body = new CatalogMappingMetadataEditV1(invocation.Value(name) ?? current.Name, invocation.Value(description) ?? current.Description); }
            catch (ArgumentException) { throw CliException.Usage("The name must be 1-200 characters and the description at most 4000."); }
            var request = (await invocation.RequestAsync(ApiOperations.EditCatalogMappingMetadata)).Path("projectId", projectId)
                .Path("mappingId", mapping).Header("If-Match", parsed.ToString()).Body(JsonBody(body, ApiOperations.EditCatalogMappingMetadata));
            using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
            if (response.Status == 412)
                throw new CliException(CliExitCode.PreconditionFailed,
                    "The metadata changed since it was read. Read it again with: weft mappings metadata get", "preconditionFailed");
            await response.RequireSuccessAsync(invocation.Cancellation);
            var edited = await response.ReadJsonAsync<CatalogMappingMetadataV1>(invocation.Cancellation);
            var editedTag = response.Message.Headers.ETag?.ToString();
            invocation.Output.Write(new MetadataView(edited, editedTag), output =>
                output.Line($"Updated mapping {edited.Scope.MappingId} to version {edited.Version}."));
        });

        return new Command("metadata", "A mapping's editable name and description").With(get, edit);
    }

    private static async Task<(CatalogMappingMetadataV1 Metadata, string? Tag)> ReadMetadataAsync(CliInvocation invocation,
        string projectId, string mappingId)
    {
        var request = (await invocation.RequestAsync(ApiOperations.GetCatalogMappingMetadata)).Path("projectId", projectId)
            .Path("mappingId", mappingId);
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        await response.RequireSuccessAsync(invocation.Cancellation);
        var metadata = await response.ReadJsonAsync<CatalogMappingMetadataV1>(invocation.Cancellation);
        return (metadata, response.Message.Headers.ETag?.ToString());
    }

    public static Command Revisions(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var mapping = Required("--mapping", "The mapping ID");
        var paging = new Paging();
        var list = new Command("list", "List a mapping's immutable revisions").With([project, mapping, .. paging.Symbols]);
        list.Handle(globals, async invocation =>
        {
            var pages = await paging.ReadAsync(invocation, async cursor =>
            {
                var request = (await invocation.RequestAsync(ApiOperations.GetCatalogRevisions)).Path("projectId", invocation.Id(project))
                    .Path("mappingId", invocation.Id(mapping)).Query("limit", paging.Limit(invocation)).Query("cursor", cursor);
                var page = await invocation.Api.ReadAsync<CatalogRevisionPageV1>(request, invocation.Cancellation);
                return (page, page.NextCursor);
            });
            var revisions = pages.SelectMany(page => page.Revisions).ToArray();
            invocation.Output.Write(paging.Document(invocation, pages, () => new { revisions, nextCursor = (string?)null }), output =>
            {
                output.Table(["REVISION", "SEQ", "ADMISSION", "FILE", "BYTES", "COMMITTED"], revisions.Select(revision =>
                    (IReadOnlyList<object?>)[revision.Scope.RevisionId, revision.Sequence, revision.Admission, revision.OriginalFilename,
                        revision.NativeByteCount, revision.CommittedAtUtc]));
                paging.Footer(output, pages[^1].NextCursor);
            });
        });

        var revisionId = Argument("revisionId", "The revision ID");
        var get = new Command("get", "Show one revision").With(project, mapping, revisionId);
        get.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogRevision)).Path("projectId", invocation.Id(project))
                .Path("mappingId", invocation.Id(mapping)).Path("revisionId", invocation.Id(revisionId));
            var revision = await invocation.Api.ReadAsync<CatalogRevisionV1>(request, invocation.Cancellation);
            invocation.Output.Write(revision, output => output.Fields(("Revision", revision.Scope.RevisionId), ("Sequence", revision.Sequence),
                ("Admission", revision.Admission), ("File", revision.OriginalFilename), ("Bytes", revision.NativeByteCount),
                ("SHA-256", revision.NativeDigest.Value), ("Backend", revision.BackendId), ("Committed", revision.CommittedAtUtc)));
        });

        var expectedHead = Optional("--expected-head", "Append only if this revision is still the mapping's head");
        var upload = new Upload();
        var append = new Command("append", "Append an immutable revision from a native mapping file")
            .With([project, mapping, expectedHead, .. upload.Symbols]);
        append.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            var request = (await invocation.RequestAsync(ApiOperations.AppendCatalogRevision)).Path("projectId", projectId)
                .Path("mappingId", invocation.Id(mapping));
            if (invocation.OptionalId(expectedHead) is { } head)
                request.Query("head", "equal").Query("expectedHeadRevisionId", head);
            return await upload.RunAsync(invocation, projectId, request);
        });

        return new Command("revisions", "Immutable revisions of a mapping").With(list, get, append);
    }

    public static Command Ingestions(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var operation = Argument("operationId", "The upload's operation ID");
        var status = new Command("status", "Show an upload's receipt").With(project, operation);
        status.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogRevisionIngestionReceipt))
                .Path("projectId", invocation.Id(project)).Path("operationId", invocation.Id(operation));
            var receipt = await invocation.Api.ReadAsync<CatalogRevisionIngestionReceiptV1>(request, invocation.Cancellation);
            invocation.Output.Write(receipt, output => Upload.WriteReceipt(output, receipt));
        });

        var wait = new Upload();
        var resume = new Command("resume", "Resume an interrupted upload without resending it").With(project, operation, wait.NoWait, wait.Timeout);
        resume.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            return await wait.ResumeAsync(invocation, projectId, invocation.Id(operation));
        });

        return new Command("ingestions", "Native upload receipts and recovery").With(status, resume);
    }

    internal static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>The exact JSON bytes, sent with a known length and the operation's media type.</summary>
    internal static Func<HttpContent> JsonBody<T>(T value, ApiOperation operation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WeftApiClient.Json);
        return () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(operation.RequestMediaType!);
            return content;
        };
    }

    internal sealed record MetadataView(CatalogMappingMetadataV1 Metadata, string? ETag);
}
