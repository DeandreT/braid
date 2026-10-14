using System.Globalization;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

/// <summary>Original committed creation facts, independently bound to the accepted request; no current access grant.</summary>
public static class CatalogProjectCreationMapper
{
    public static CatalogProjectCreationResultV1 Result(ProjectCreationResult result, ActorContext actor,
        Guid organizationId, Guid idempotencyKey, ProjectCreationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(metadata);
        var outcome = result.Outcome switch
        {
            ProjectCreationOutcome.Created => CatalogProjectCreationOutcomeV1.Created,
            ProjectCreationOutcome.Replayed => CatalogProjectCreationOutcomeV1.Replayed,
            _ => throw InvalidFacts()
        };
        var receipt = result.Receipt ?? throw InvalidFacts();
        if (organizationId == Guid.Empty || idempotencyKey == Guid.Empty ||
            receipt.Project.OrganizationId != organizationId || receipt.IdempotencyKey != idempotencyKey ||
            receipt.OriginalActor != actor.PrincipalId ||
            !string.Equals(receipt.Metadata.Name, metadata.Name, StringComparison.Ordinal) ||
            !string.Equals(receipt.Metadata.Description, metadata.Description, StringComparison.Ordinal) ||
            receipt.Fingerprint != ProjectCreationFingerprint.Create(organizationId, actor.PrincipalId, idempotencyKey, metadata) ||
            receipt.InitialProjectVersion != 1 || receipt.InitialMembershipRole != ProjectRole.Administrator ||
            !receipt.InitialMembershipActive || receipt.InitialMembershipVersion != 1 ||
            receipt.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw InvalidFacts();

        var facts = new CatalogProjectCreationReceiptV1(CatalogPublicMapper.ProjectScope(receipt.Project),
            receipt.Metadata.Name, receipt.Metadata.Description, receipt.OriginalActor.Value.ToString("D"),
            receipt.CreatedAtUtc, receipt.InitialProjectVersion.ToString(CultureInfo.InvariantCulture),
            CatalogRoleV1.Administrator, receipt.InitialMembershipActive,
            receipt.InitialMembershipVersion.ToString(CultureInfo.InvariantCulture));
        return new(outcome, facts, ProjectUrl(receipt.Project));
    }

    public static string ProjectUrl(ProjectScope project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $"/api/v1/organizations/{project.OrganizationId:D}/projects/{project.ProjectId:D}";
    }

    private static InvalidOperationException InvalidFacts() =>
        new("The project creation result does not match its accepted original facts.");
}
