using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Private persistence codec; never an HTTP request or response contract.</summary>
public static class ImportReviewCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 10
    };

    public static byte[] Encode(ImportReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var destination = review.Destination;
        var profile = review.Conversion.Profile;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, review.Id, review.Actor.Value,
            destination.Project.OrganizationId, destination.Project.ProjectId, destination.BackendId,
            destination.Metadata?.Name, destination.Metadata?.Description, destination.Target?.MappingId,
            destination.Head?.RevisionId, review.ExpiresAt, review.Package.Encode(), review.Conversion.Native.ToArray(),
            profile.EngineRevision, profile.BackendBuild, profile.AdapterBuild, review.Conversion.Report.Summary,
            review.Conversion.Report.Warnings.Select(value => new Warning(value.Code, value.Message)).ToArray()), Options);
        if (bytes.Length > MaximumBytes)
            throw new ArgumentException("The review exceeds its persistence limit.", nameof(review));
        return bytes;
    }

    public static ImportReview Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("The review exceeds its persistence limit.", nameof(bytes));
        var value = JsonSerializer.Deserialize<Envelope>(bytes, Options)
            ?? throw new ArgumentException("Missing review.", nameof(bytes));
        if (value.Version != 1 || value.Package is null || value.Native is null || value.Warnings is null ||
            value.Warnings.Length > InterchangeReport.MaximumWarnings || value.Warnings.Any(warning => warning is null))
            throw new ArgumentException("Invalid review shape.", nameof(bytes));
        var project = new ProjectScope(value.OrganizationId, value.ProjectId);
        ImportDestination destination;
        if (value.MappingId is { } mapping && value.HeadId is { } head && value.Name is null && value.Description is null)
            destination = ImportDestination.Append(new(project.OrganizationId, project.ProjectId, mapping), value.BackendId, new(head));
        else if (value.MappingId is null && value.HeadId is null && value.Name is not null && value.Description is not null)
            destination = ImportDestination.Create(project, value.BackendId, new(value.Name, value.Description));
        else throw new ArgumentException("Invalid review destination.", nameof(bytes));
        var package = InterchangePackage.Decode(value.Package);
        var conversion = new InterchangeImportOutput(new(value.BackendId, value.EngineRevision, value.BackendBuild, value.AdapterBuild),
            package.Digest, value.Native, new(value.Summary, value.Warnings.Select(warning =>
                new InterchangeWarning(warning.Code, warning.Message))));
        var review = new ImportReview(value.Id, new(value.ActorId), destination, package, conversion, value.ExpiresAt);
        if (!bytes.SequenceEqual(Encode(review)))
            throw new ArgumentException("Review encoding must be canonical.", nameof(bytes));
        return review;
    }

    private sealed record Warning(string Code, string Message);
    private sealed record Envelope(int Version, Guid Id, Guid ActorId, Guid OrganizationId, Guid ProjectId,
        string BackendId, string? Name, string? Description, Guid? MappingId, Guid? HeadId,
        DateTimeOffset ExpiresAt, byte[] Package, byte[] Native, string EngineRevision,
        string BackendBuild, string AdapterBuild, string Summary, Warning[] Warnings);
}
