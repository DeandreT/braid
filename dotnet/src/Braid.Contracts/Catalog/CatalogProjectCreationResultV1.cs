namespace Braid.Contracts.Catalog;

/// <summary>A successful project creation or exact replay and its full scoped relative resource URL.</summary>
public sealed record CatalogProjectCreationResultV1
{
    public CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1 outcome,
        CatalogProjectCreationReceiptV1 receipt, string projectUrl)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), "A project creation outcome must be created or replayed.");
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(projectUrl);
        var expectedUrl = $"/api/v1/organizations/{receipt.Project.OrganizationId}/projects/{receipt.Project.ProjectId}";
        if (!string.Equals(projectUrl, expectedUrl, StringComparison.Ordinal))
            throw new ArgumentException("A created project URL must match the full receipt scope.", nameof(projectUrl));
        Outcome = outcome;
        Receipt = receipt;
        ProjectUrl = projectUrl;
    }

    public CatalogProjectCreationOutcomeV1 Outcome { get; }
    public CatalogProjectCreationReceiptV1 Receipt { get; }
    public string ProjectUrl { get; }
}
