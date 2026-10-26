using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// One bounded text excerpt of sample data, offered only on a turn pinned with the payload manifest. The data is
    /// read through the same authorized read as its route, and the creator's active payload-read grant on the data's
    /// project is observed freshly before the read and again before release; without it the call is notPermitted.
    /// Only strict UTF-8 text without control characters other than tab and line breaks is shared, at most
    /// <see cref="WorkspaceChatGrounding.PayloadBytes"/> bytes of it, and the result records what was shared.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> PayloadAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var ordinal = (int)call.Count("ordinal", 0, 127, 0);
        var payload = call.Name switch
        {
            "get_preview_input" => await PreviewPayloadAsync(scope, call.Id("runId"), ordinal, input: true, token),
            "get_preview_output" => await PreviewPayloadAsync(scope, call.Id("runId"), ordinal, input: false, token),
            "get_regression_case_data" => await CasePayloadAsync(scope, call, ordinal, token),
            _ => await RunOutputPayloadAsync(scope, call.Id("runId"), ordinal, token)
        };
        if (payload.MediaType is { } mediaType && !Textual(mediaType)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.UnsupportedContent);
        var whole = Text(payload.Bytes);
        // The grant is observed again before anything is released.
        await RequireGrantAsync(scope, payload.Project, token);
        token.ThrowIfCancellationRequested();
        var maximum = Math.Min(WorkspaceChatGrounding.PayloadBytes, payload.Bytes.Length);
        while (true)
        {
            var end = maximum;
            // Never split a UTF-8 scalar.
            while (end > 0 && end < payload.Bytes.Length && (payload.Bytes[end] & 0xC0) == 0x80) end--;
            var text = end == payload.Bytes.Length ? whole : StrictUtf8.GetString(payload.Bytes, 0, end);
            var data = new
            {
                source = Name(payload.Source),
                payload.SubjectId,
                payload.Version,
                ordinal,
                payload.DocumentName,
                payload.MediaType,
                totalBytes = payload.Bytes.Length,
                sharedBytes = end,
                truncated = end < payload.Bytes.Length,
                text
            };
            var shared = end > 0 ? new ChatPayloadDisclosureFact(payload.Project, payload.Source, payload.Subject, payload.Version, end) : null;
            try { return Single(call.Name, scope.Budget, data, [payload.Citation], shared); }
            catch (WorkspaceToolRefusal refusal) when (refusal.Outcome == WorkspaceToolOutcome.ResourceLimit && maximum > 0)
            {
                maximum /= 2;
            }
        }
    }

    private async Task<Payload> PreviewPayloadAsync(CallScope scope, Guid runId, int ordinal, bool input, CancellationToken token)
    {
        RequireFeature(features.Previews);
        var previews = Required<PreviewRunService>();
        var run = await previews.ReadAsync(scope.Actor, scope.Organization, runId, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (run.RunId != runId || run.Revision.OrganizationId != scope.Organization) throw new InvalidOperationException("Preview read scope mismatch.");
        await RequireGrantAsync(scope, run.Revision.ProjectId, token);
        var citation = ChatWorkspaceCitationFact.PreviewRun(run.Revision, runId);
        if (input)
        {
            var inputs = await previews.ReadInputDescriptorsAsync(scope.Actor, scope.Organization, runId, token)
                ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            var descriptor = inputs.FirstOrDefault(item => item.Ordinal == ordinal) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            var content = await previews.ReadInputAsync(scope.Actor, scope.Organization, runId, ordinal, token)
                ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            if (content.Digest != descriptor.Digest || content.Content.Length != descriptor.ByteCount)
                throw new InvalidOperationException("Preview input identity mismatch.");
            return new(run.Revision.ProjectId, ChatPayloadSource.PreviewInput, runId, null, Id(runId), descriptor.Name ?? "primary", null,
                content.Content, citation);
        }
        var output = run.Outputs.FirstOrDefault(item => item.Ordinal == ordinal) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var bytes = await previews.ReadOutputAsync(scope.Actor, scope.Organization, runId, ordinal, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (bytes.Digest != output.Digest || bytes.Content.Length != output.ByteCount) throw new InvalidOperationException("Preview output identity mismatch.");
        return new(run.Revision.ProjectId, ChatPayloadSource.PreviewOutput, runId, null, Id(runId), output.TargetName, output.MediaType,
            bytes.Content, citation);
    }

    private async Task<Payload> CasePayloadAsync(CallScope scope, WorkspaceToolCall call, int ordinal, CancellationToken token)
    {
        RequireFeature(features.Regressions);
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var caseId = call.Id("caseId");
        var version = call.Count("version", 1, 9_007_199_254_740_991, 1);
        var part = call.Choice("part", "input", "expectedOutput");
        await RequireGrantAsync(scope, mapping.ProjectId, token);
        var value = await CatalogEndpoints.RegressionCaseVersionAsync(services, scope.Actor, mapping, caseId, version, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        string name;
        byte[] bytes;
        if (part == "input")
        {
            PreviewInputDocument[] inputs = [value.Replay.Primary, .. value.Replay.NamedInputs];
            if (ordinal >= inputs.Length) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            name = inputs[ordinal].Name ?? "primary";
            bytes = inputs[ordinal].CopyBytes();
        }
        else
        {
            if (ordinal >= value.Expected.Outputs.Count) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            name = value.Expected.Outputs[ordinal].Key.Name ?? "primary";
            bytes = value.Expected.Outputs[ordinal].CopyBytes();
        }
        return new(mapping.ProjectId, ChatPayloadSource.RegressionCase, caseId, version, Id(caseId), name, null, bytes,
            ChatWorkspaceCitationFact.RegressionCase(mapping, caseId, version));
    }

    private async Task<Payload> RunOutputPayloadAsync(CallScope scope, Guid runId, int ordinal, CancellationToken token)
    {
        RequireFeature(features.Regressions);
        var snapshot = await Required<IRegressionRunMetadataReader>().ReadMetadataAsync(scope.Actor, scope.Organization, runId, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (snapshot.RunId != runId || snapshot.Revision.OrganizationId != scope.Organization)
            throw new InvalidOperationException("Regression metadata scope mismatch.");
        var mapping = new MappingScope(scope.Organization, snapshot.Revision.ProjectId, snapshot.Revision.MappingId);
        await RequireGrantAsync(scope, mapping.ProjectId, token);
        var view = await CatalogEndpoints.RegressionRunViewAsync(services, scope.Actor, mapping, runId, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var outputs = view.Result?.Outputs?.Outputs ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (ordinal >= outputs.Count) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        return new(mapping.ProjectId, ChatPayloadSource.RegressionRunOutput, runId, null, Id(runId), outputs[ordinal].Key.Name ?? "primary", null,
            outputs[ordinal].CopyBytes(), ChatWorkspaceCitationFact.RegressionRun(mapping, runId));
    }

    /// <summary>The creator's active explicit payload-read grant on exactly this project, observed now.</summary>
    private async Task RequireGrantAsync(CallScope scope, Guid projectId, CancellationToken token)
    {
        var project = new ProjectScope(scope.Organization, projectId);
        var grant = await services.GetRequiredService<IProjectPayloadPermissionReader>().ObserveAsync(scope.Actor, project, token);
        token.ThrowIfCancellationRequested();
        if (grant is null || !grant.Active || grant.Scope != project || grant.PrincipalId != scope.Actor.PrincipalId)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotPermitted);
    }

    private static bool Textual(string mediaType)
    {
        var type = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "application/xml" or "application/csv" ||
            type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal);
    }

    /// <summary>Strict UTF-8 text with no control characters but tab and line breaks; anything else is not shared.</summary>
    private static string Text(byte[] bytes)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.UnsupportedContent); }
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r'))) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.UnsupportedContent);
        return text;
    }

    private sealed record Payload(Guid Project, ChatPayloadSource Source, Guid Subject, long? Version, string SubjectId, string DocumentName,
        string? MediaType, byte[] Bytes, ChatWorkspaceCitationFact Citation)
    {
        public override string ToString() => nameof(Payload);
    }
}
