using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Creation;
using Weft.Domain;

namespace Weft.Api.Creation;

public static partial class CreationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };
    public static IEndpointRouteBuilder MapWeftCreation(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.BusinessRoutes();
        foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 503 })
            routes.WithMetadata(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(CreationErrorV1), status, "application/json"));
        const string root = "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}";
        routes.MapGet("/api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities",
            (HttpContext c, string organizationId, string projectId) => Capabilities(c, organizationId, projectId))
            .WithName("GetCreationCapabilities").WithTags("Mapping creation").Produces<CreationCapabilitiesV1>();
        routes.MapGet("/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals",
            (HttpContext c, string organizationId, string projectId) => Run(c, organizationId, projectId, "", "list"))
            .WithName("ListCreationProposals").WithTags("Mapping creation").Produces<CreationProposalPageV1>();
        routes.MapGet(root, (HttpContext c, string organizationId, string projectId, string proposalId) => Run(c, organizationId, projectId, proposalId, "read"))
            .WithName("ReadCreationProposal").WithTags("Mapping creation").Produces<CreationProposalV1>();
        routes.MapPost(root + "/approve", (HttpContext c, string organizationId, string projectId, string proposalId) => Run(c, organizationId, projectId, proposalId, "approve"))
            .WithName("ApproveCreationProposal").WithTags("Mapping creation").Accepts<CreationApprovalV1>("application/json").Produces<CreationProposalV1>();
        routes.MapPost(root + "/recover", (HttpContext c, string organizationId, string projectId, string proposalId) => Run(c, organizationId, projectId, proposalId, "recover"))
            .WithName("RecoverCreationProposal").WithTags("Mapping creation").Accepts<CreationRecoveryV1>("application/json").Produces<CreationProposalV1>();
        routes.MapPost(root + "/preview", (HttpContext c, string organizationId, string projectId, string proposalId) => Run(c, organizationId, projectId, proposalId, "preview"))
            .WithName("PreviewCreationProposal").WithTags("Mapping creation").Accepts<CreationPreviewRequestV1>("application/json").Produces<CreationPreviewV1>();
        return endpoints;
    }

    private static async Task<IResult> Run(HttpContext c, string organizationId, string projectId, string proposalId, string action)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.XContentTypeOptions = "nosniff";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        try
        {
            var boundary = c.RequestServices.GetService<CatalogRequestBoundary>();
            if (boundary is null) return Error(503, "Unavailable");
            var auth = await boundary.AuthenticateAsync(c, action is not ("read" or "list"), ct);
            if (!auth.IsAdmitted) return Error(auth.StatusCode, "IdentityRejected");
            if (auth.ContextMode is not ("session" or "bearer")) return Error(403, "IdentityRejected");
            if (!await CreationRuntime.AvailableAsync(c.RequestServices, ct)) return Error(503, "Unavailable");
            var project = new ProjectScope(Id(organizationId), Id(projectId));
            if (auth.AllowedOrganizationId != project.OrganizationId) return Error(404, "NotFound");
            if (c.Request.Headers.ContentEncoding.Count != 0) return Error(400, "InvalidRequest");
            if (action == "list")
            {
                if (c.Request.Query.Any(q => q.Key is not ("afterProposal" or "limit") || q.Value.Count != 1)) return Error(400, "InvalidRequest");
                var after = c.Request.Query.ContainsKey("afterProposal") ? (Guid?)Id(c.Request.Query["afterProposal"][0]!) : null;
                var limit = c.Request.Query.ContainsKey("limit") ? int.Parse(c.Request.Query["limit"][0]!, System.Globalization.CultureInfo.InvariantCulture) : 20;
                var discovery = new CreationDiscoveryService(c.RequestServices.GetRequiredService<ICreationDiscovery>(),
                    c.RequestServices.GetRequiredService<ISpecBundleServiceFactory>(), c.RequestServices.GetRequiredService<IMappingCreationProposalStore>());
                var rows = await discovery.ProposalsAsync(auth.Actor!, project, after, limit, ct);
                foreach (var row in rows)
                    if (!await Current(c, auth.Actor!, row.Proposal, ct, false)) return Error(404, "NotFound");
                return Results.Ok(new CreationProposalPageV1(rows.Select(r => new CreationProposalLinkV1(r.Proposal.Id.ToString("D"), r.Proposal.Fingerprint.Value,
                    $"/catalog/organizations/{project.OrganizationId:D}/projects/{project.ProjectId:D}/creation/{r.Proposal.Id:D}?fingerprint={r.Proposal.Fingerprint.Value}")).ToArray(),
                    rows.Count == limit ? rows[^1].Proposal.Id.ToString("D") : null));
            }
            var id = Id(proposalId);
            CreationApprovalV1? approval = null; CreationPreviewRequestV1? preview = null;
            string fingerprint;
            if (action == "read")
            {
                if (c.Request.Query.Count != 1 || c.Request.Query["expectedFingerprint"].Count != 1) return Error(400, "InvalidRequest");
                fingerprint = c.Request.Query["expectedFingerprint"][0]!;
            }
            else
            {
                if (c.Request.QueryString.HasValue) return Error(400, "InvalidRequest");
                if (action == "approve") { approval = await Body<CreationApprovalV1>(c, 16_384, ct); fingerprint = approval.Fingerprint; }
                else if (action == "preview") { preview = await Body<CreationPreviewRequestV1>(c, 1_400_000, ct); fingerprint = preview.Fingerprint; }
                else fingerprint = (await Body<CreationRecoveryV1>(c, 1024, ct)).Fingerprint;
            }
            var digest = Digest(fingerprint);
            var store = c.RequestServices.GetRequiredService<IMappingCreationProposalStore>();
            var state = await store.ReadAsync(auth.Actor!, project, id, ct);
            if (state is null || state.Proposal.Author != auth.Actor!.PrincipalId || state.Proposal.Project != project || state.Proposal.Id != id)
                return Error(404, "NotFound");
            if (state.Proposal.Fingerprint != digest) return Error(409, "Conflict");
            var service = CreationRuntime.Approvals(c.RequestServices);
            var before = await service.ReadAsync(auth.Actor!, project, id, digest, ct);
            if (before is null || !await Current(c, auth.Actor!, state.Proposal, ct,
                action == "preview" || action != "read" && !Terminal(before))) return Error(404, "NotFound");
            if (preview is not null)
            {
                if (preview.SampleBase64.Length > 1_398_104) return Error(413, "LimitExceeded");
                var sample = Convert.FromBase64String(preview.SampleBase64);
                if (sample.Length > 1_048_576) return Error(413, "LimitExceeded");
                if (Sha256Digest.FromBytes(sample) != Digest(preview.SampleDigest)) return Error(409, "Conflict");
                var evidence = await new MappingCreationPreviewService(c.RequestServices.GetRequiredService<IMappingCreationPreview>(),
                    c.RequestServices.GetRequiredService<IMappingCreationAuthority>()).PreviewAsync(auth.Actor!, state.Proposal, sample, ct);
                if (evidence is null || !await Current(c, auth.Actor!, state.Proposal, ct)) return Error(404, "NotFound");
                var succeeded = evidence.Outcome == CreationPreviewOutcome.Succeeded;
                return Results.Ok(new CreationPreviewV1(id.ToString("D"), fingerprint, evidence.Subject.Native.Value, evidence.Subject.Sample.Value,
                    evidence.Outcome.ToString(), evidence.Format, evidence.MediaType, succeeded ? evidence.OutputDigest.Value : null,
                    succeeded ? Convert.ToBase64String(evidence.CopyOutput()) : null));
            }
            var result = action switch
            {
                "approve" => await service.ApproveAsync(auth.Actor!, project, id, digest, new(digest, approval!.AcceptedCriticalAssumptions), ct),
                "recover" => await service.RecoverAsync(auth.Actor!, project, id, digest, ct),
                _ => await service.ReadAsync(auth.Actor!, project, id, digest, ct)
            };
            if (result is null || !await Current(c, auth.Actor!, result.State.Proposal, ct, action != "read" && !Terminal(result))) return Error(404, "NotFound");
            return Results.Ok(Review(result));
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { throw; }
        catch (SpecStorageException e) { return Error(e.Error is SpecStorageError.NotAdmitted or SpecStorageError.Missing ? 404 : 503, "Unavailable"); }
        catch (Exception e) when (e is ArgumentException or JsonException or FormatException or OverflowException) { return Error(400, "InvalidRequest"); }
        catch (BadHttpRequestException e) { return Error(e.StatusCode == 413 ? 413 : 400, "InvalidRequest"); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return Error(503, "Unavailable"); }
    }

    private static bool Terminal(MappingCreationObservation value) => value.Receipt?.Phase is IngestionPhase.Committed or IngestionPhase.Rejected or
        IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted;

    private static async Task<bool> Current(HttpContext c, ActorContext actor, MappingCreationProposal proposal, CancellationToken ct, bool fresh = true)
    {
        var current = await c.RequestServices.GetRequiredService<ICatalogReader>().GetProjectAsync(actor, proposal.Project, ct);
        // The durable reader re-authorizes current content access even when the retained source has since been retired.
        var retained = await c.RequestServices.GetRequiredService<IMappingCreationProposalStore>().ReadAsync(actor, proposal.Project, proposal.Id, ct);
        return current is not null && current.Summary.Scope == proposal.Project && current.Access.Scope == proposal.Project &&
            current.Access.PrincipalId == actor.PrincipalId && current.Access.Can(ProjectAction.ReadMetadata) &&
            retained?.Proposal.Author == actor.PrincipalId && retained.Proposal.Project == proposal.Project && retained.Proposal.Id == proposal.Id &&
            retained.Proposal.Fingerprint == proposal.Fingerprint && (!fresh ||
            await c.RequestServices.GetRequiredService<IMappingCreationAuthority>().CheckAsync(actor, proposal.Specification.Snapshot, ct) == CreationAuthority.Current);
    }
    private static CreationProposalV1 Review(MappingCreationObservation value)
    {
        var p = value.State.Proposal; var receipt = value.Receipt;
        return new(p.Project.ProjectId.ToString("D"), p.Id.ToString("D"), p.Fingerprint.Value, p.Specification.Snapshot.Digest.Value,
            p.Plan.Digest.Value, p.Compilation.NativeDigest.Value, p.Request.Metadata.Name, p.Request.Metadata.Description,
            Encoding.UTF8.GetString(p.Plan.CopyBytes()), value.State.Approved,
            p.Notes.Evidence.Select(e => new CreationCitationV1(e.Source.Bundle.BundleId.ToString("D"), e.Source.SourceId.ToString("D"), e.Source.Version, e.ChunkOrdinal)).ToArray(),
            p.Notes.Assumptions.Select(a => new CreationAssumptionV1(a.Id, a.Text, a.Critical)).ToArray(),
            p.Notes.Questions.Select(q => new CreationQuestionV1(q.Text, q.Critical)).ToArray(), receipt is null ? null :
                new(receipt.OperationId.ToString("D"), receipt.Phase.ToString(), receipt.Phase == IngestionPhase.Committed ? receipt.FutureMapping.MappingId.ToString("D") : null,
                    receipt.Phase == IngestionPhase.Committed ? receipt.FutureRevision.RevisionId.ToString("D") : null));
    }
    private static Guid Id(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") ? id : throw new ArgumentException();
    private static Sha256Digest Digest(string text)
    {
        var digest = Sha256Digest.Parse(text);
        return digest.Value == text ? digest : throw new ArgumentException();
    }
    private static IResult Error(int status, string code) => Results.Json(new CreationErrorV1(code), statusCode: status);
    private static async Task<T> Body<T>(HttpContext c, int maximum, CancellationToken ct)
    {
        if (c.Request.ContentType is not ("application/json" or "application/json; charset=utf-8")) throw new ArgumentException();
        if (c.Request.ContentLength > maximum) throw new BadHttpRequestException("Limit", 413);
        using var memory = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await c.Request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)memory.Length)), ct);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            if (memory.Length > maximum) throw new BadHttpRequestException("Limit", 413);
        }
        return JsonSerializer.Deserialize<T>(memory.ToArray(), Json) ?? throw new JsonException();
    }
}
