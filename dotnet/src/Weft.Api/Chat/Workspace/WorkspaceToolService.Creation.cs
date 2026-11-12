using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Creation;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    private static readonly JsonSerializerOptions CreationJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };
    private sealed record EvidenceInput(Guid SourceId, long SourceVersion, int ChunkOrdinal);
    private sealed record NotesInput(EvidenceInput[] Evidence, CreationAssumption[] Assumptions, CreationQuestion[] Questions);

    private async Task<ChatReadOnlyToolResult> CreationAsync(CallScope scope, WorkspaceToolCall call, CancellationToken ct)
    {
        if (!await CreationRuntime.AvailableAsync(services, ct)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
        var project = new ProjectScope(scope.Organization, call.Id("projectId"));
        await CreationGrantAsync(scope, project, ct);
        if (call.Name == WorkspaceToolRegistry.ReadSpecification && call.OptionalText("mode", 20) == "proposal")
            return await ReadCreationFeedbackAsync(scope, call, project, ct);
        if (call.Name == WorkspaceToolRegistry.ReadSpecification && call.OptionalText("mode", 20) is "selections" or "proposals")
            return await DiscoverCreationAsync(scope, call, project, ct);
        var bundle = new SpecBundleScope(project, call.Id("bundleId"));
        var extractionIds = call.Ids("extractionIds").Order().ToArray();
        await CreationGrantAsync(scope, project, ct);
        var specification = await new CreationSpecificationLoader(services.GetRequiredService<ISpecBundleServiceFactory>())
            .LoadAsync(scope.Actor, bundle, extractionIds, ct, token => CreationGrantAsync(scope, project, token));
        await CreationGrantAsync(scope, project, ct);
        if (call.Name == WorkspaceToolRegistry.ReadSpecification)
        {
            if (scope.Budget.Nodes < 1) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
            var sourceIndex = (int)call.Count("sourceIndex", 0, 19, 0);
            if (sourceIndex >= specification.Extractions.Count) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            var extraction = specification.Extractions[sourceIndex];
            var extractionId = extractionIds[sourceIndex];
            var offset = (int)call.Count("offset", 0, 1_000_000, 0);
            var count = (int)call.Count("limit", 1, 20, 20);
            var chunks = extraction.Chunks.Skip(offset).Take(Math.Min(count, Math.Max(0, scope.Budget.Nodes - 1))).ToArray();
            while (true)
            {
                if (chunks.Length == 0 && offset < extraction.Chunks.Count)
                    throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
                var data = new
                {
                    projectId = Id(project.ProjectId),
                    bundleId = Id(bundle.BundleId),
                    extractionId = Id(extractionId),
                    snapshotDigest = specification.Snapshot.Digest.Value,
                    sourceId = Id(extraction.Identity.Source.SourceId),
                    sourceIndex,
                    sources = specification.Extractions.Select((e, i) => new
                    {
                        extractionId = Id(extractionIds[i]),
                        sourceId = Id(e.Identity.Source.SourceId),
                        sourceVersion = e.Identity.Source.Version,
                        sourceDigest = e.Identity.SourceDigest.Value,
                        evidenceDigest = e.Identity.EvidenceDigest.Value
                    }).ToArray(),
                    sourceVersion = extraction.Identity.Source.Version,
                    sourceDigest = extraction.Identity.SourceDigest.Value,
                    evidenceDigest = extraction.Identity.EvidenceDigest.Value,
                    offset,
                    totalChunks = extraction.Chunks.Count,
                    nextOffset = offset + chunks.Length < extraction.Chunks.Count ? (int?)(offset + chunks.Length) : null,
                    chunks
                };
                if (Serialize(new Envelope(Schema, call.Name, WorkspaceToolOutcome.Available, offset + chunks.Length < extraction.Chunks.Count, data)).Length <= scope.Budget.Bytes)
                {
                    await CreationGrantAsync(scope, project, ct);
                    return new(Weft.Ai.AiJsonObject.CopyFrom(Encoding.UTF8.GetString(Serialize(new Envelope(Schema, call.Name,
                        WorkspaceToolOutcome.Available, offset + chunks.Length < extraction.Chunks.Count, data)))), chunks.Length + 1,
                        dataShared: chunks.Length == 0 ? null : new(project.ProjectId, ChatPayloadSource.Specification, extractionId, null, Serialize(chunks).Length));
                }
                if (chunks.Length == 0) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
                chunks = chunks[..^1];
            }
        }
        if (scope.Turn is null || scope.RoundId is null || scope.CallId is null) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        if (specification.Snapshot.Digest.Value != call.Text("snapshotDigest", 64))
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        var plan = MappingCreationPlan.Parse(call.Object("plan"));
        var input = JsonSerializer.Deserialize<NotesInput>(call.Object("notes"), CreationJson) ?? throw new JsonException();
        var notes = new CreationGenerationNotes(input.Evidence.Select(n => new CreationEvidenceReference(
            new SpecSourceIdentity(bundle, n.SourceId, n.SourceVersion), n.ChunkOrdinal)), input.Assumptions, input.Questions);
        try { notes = CreationProposalNotes.Canonicalize(specification, notes); }
        catch (ArgumentException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall); }
        var metadata = new IngestionCreateMetadata(call.Text("name", 128), "");
        Guid Identity(string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            purpose,
            scope.Organization,
            actor = scope.Actor.PrincipalId.Value,
            conversation = scope.Conversation.Key.ConversationId,
            turn = scope.Turn.TurnId,
            round = scope.RoundId,
            call = scope.CallId
        })))[..16]);
        var id = Identity("creation-proposal");
        var store = services.GetRequiredService<IMappingCreationProposalStore>();
        var existing = await store.ReadAsync(scope.Actor, project, id, ct);
        MappingCreationProposal proposal;
        if (existing is not null)
        {
            proposal = existing.Proposal;
            if (proposal.Id != id || proposal.Author != scope.Actor.PrincipalId || proposal.Project != project ||
                proposal.RequestKey != Identity("creation-ingestion") || proposal.Specification.Snapshot.Digest != specification.Snapshot.Digest ||
                proposal.Plan.Digest != plan.Digest || proposal.Notes.Digest != notes.Digest || proposal.Request.Metadata != metadata)
                throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        }
        else
        {
            proposal = await new MappingCreationGenerationService(services.GetRequiredService<IMappingCreationCompiler>(),
                services.GetRequiredService<IMappingCreationAuthority>()).GenerateAsync(scope.Actor, id, Identity("creation-ingestion"), specification, plan, notes, metadata, ct);
            await CreationGrantAsync(scope, project, ct);
            MappingCreationState? retained;
            try { retained = await CreationRuntime.Approvals(services).RetainAsync(scope.Actor, proposal, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
            {
                // Lost acknowledgement: discover only the original deterministic identity, never allocate or retain a replacement.
                retained = await store.ReadAsync(scope.Actor, project, id, ct);
                if (retained is null) throw;
            }
            if (retained is null) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            if (retained.Proposal.Id != id || retained.Proposal.Project != project || retained.Proposal.Author != scope.Actor.PrincipalId ||
                retained.Proposal.Fingerprint != proposal.Fingerprint) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
        }
        await CreationGrantAsync(scope, project, ct);
        if (await services.GetRequiredService<IMappingCreationAuthority>().CheckAsync(scope.Actor, specification.Snapshot, ct) != CreationAuthority.Current)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotPermitted);
        // Only bounded identifiers leave this call; review text is read through the authenticated proposal endpoint.
        return Single(call.Name, scope.Budget, new
        {
            projectId = Id(project.ProjectId),
            proposalId = Id(proposal.Id),
            fingerprint = proposal.Fingerprint.Value,
            snapshotDigest = specification.Snapshot.Digest.Value,
            planDigest = plan.Digest.Value,
            nativeDigest = proposal.Compilation.NativeDigest.Value,
            reviewPath = $"/catalog/organizations/{Id(project.OrganizationId)}/projects/{Id(project.ProjectId)}/creation/{Id(proposal.Id)}?fingerprint={proposal.Fingerprint.Value}",
            requiresApproval = true
        }, [ChatWorkspaceCitationFact.CreationProposal(project, proposal.Id, proposal.Fingerprint)]);
    }

    private async Task CreationGrantAsync(CallScope scope, ProjectScope project, CancellationToken ct)
    {
        var current = await Catalog.GetProjectAsync(scope.Actor, project, ct);
        if (current is null || current.Summary.Scope != project || current.Access.Scope != project || current.Access.PrincipalId != scope.Actor.PrincipalId ||
            !current.Access.Can(ProjectAction.ReadMetadata)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        await RequireGrantAsync(scope, project.ProjectId, ct);
    }

    private async Task<ChatReadOnlyToolResult> DiscoverCreationAsync(CallScope scope, WorkspaceToolCall call, ProjectScope project, CancellationToken ct)
    {
        var discovery = new CreationDiscoveryService(services.GetRequiredService<ICreationDiscovery>(),
            services.GetRequiredService<ISpecBundleServiceFactory>(), services.GetRequiredService<IMappingCreationProposalStore>());
        if (scope.Budget.Nodes < 2) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        var limit = Math.Min((int)call.Count("limit", 1, 20, 20), scope.Budget.Nodes - 1);
        int count;
        object data;
        ChatWorkspaceCitationFact[] citations = [];
        if (call.Text("mode", 20) == "selections")
        {
            var rows = await discovery.SelectionsAsync(scope.Actor, project, call.OptionalId("afterBundle"), call.OptionalId("afterObject"), limit, ct,
                token => CreationGrantAsync(scope, project, token));
            count = rows.Count;
            data = new
            {
                projectId = Id(project.ProjectId),
                selections = rows.Select(r => new
                {
                    bundleId = Id(r.Reference.Bundle.BundleId),
                    extractionId = Id(r.Reference.ExtractionId),
                    sourceId = Id(r.Identity.Source.SourceId),
                    sourceVersion = r.Identity.Source.Version,
                    format = r.Identity.Format.ToString(),
                    outcome = r.Identity.Outcome.ToString(),
                    profileDigest = r.Identity.ProfileDigest.Value,
                    extractorVersion = r.Identity.ExtractorVersion,
                    evidenceDigest = r.Identity.EvidenceDigest.Value,
                    evidenceBytes = r.EvidenceBytes
                }),
                next = rows.Count == limit ? new { afterBundle = Id(rows[^1].Reference.Bundle.BundleId), afterObject = Id(rows[^1].Reference.ExtractionId) } : null
            };
        }
        else
        {
            var rows = await discovery.ProposalsAsync(scope.Actor, project, call.OptionalId("afterProposal"), limit, ct,
                token => CreationGrantAsync(scope, project, token));
            count = rows.Count;
            data = new
            {
                projectId = Id(project.ProjectId),
                proposals = rows.Select(r => new
                {
                    proposalId = Id(r.Proposal.Id),
                    fingerprint = r.Proposal.Fingerprint.Value,
                    reviewPath = $"/catalog/organizations/{Id(project.OrganizationId)}/projects/{Id(project.ProjectId)}/creation/{Id(r.Proposal.Id)}?fingerprint={r.Proposal.Fingerprint.Value}"
                }),
                next = rows.Count == limit ? new { afterProposal = Id(rows[^1].Proposal.Id) } : null
            };
            citations = rows.Select(r => ChatWorkspaceCitationFact.CreationProposal(project, r.Proposal.Id, r.Proposal.Fingerprint)).ToArray();
        }
        await CreationGrantAsync(scope, project, ct);
        var bytes = Serialize(new Envelope(Schema, call.Name, WorkspaceToolOutcome.Available, count == limit, data));
        if (bytes.Length > scope.Budget.Bytes) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        return new(Weft.Ai.AiJsonObject.CopyFrom(Encoding.UTF8.GetString(bytes)), count + 1, workspaceCitations: citations);
    }
}
