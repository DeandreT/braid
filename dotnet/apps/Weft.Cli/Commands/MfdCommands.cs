using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using Weft.Domain;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

/// <summary>Conversion review and explicit confirmation are separate commands; neither assumes warning consent.</summary>
internal static class MfdCommands
{
    private const int MaximumReviewBytes = 1024 * 1024;
    private const int MaximumDownloadBytes = 16 * 1024 * 1024 + 256 * 1024;

    public static Command Create(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var mapping = Required("--mapping", "The mapping ID");
        var revision = Required("--revision", "The revision ID");
        var reviewId = Argument("reviewId", "The server-owned review ID");
        var digest = Required("--review-digest", "The exact SHA-256 printed by the reviewed conversion");
        var acknowledge = new Option<bool>("--acknowledge-warnings") { Description = "Explicitly acknowledge every warning in that exact review" };

        var capabilities = new Command("capabilities", "Read interchange availability and package limits");
        capabilities.Handle(globals, async invocation =>
        {
            var result = await invocation.Api.ReadAsync<CatalogInterchangeCapabilitiesV1>(new(ApiOperations.GetCatalogInterchangeCapabilities), invocation.Cancellation);
            invocation.Output.Write(result, output => output.Fields(("Enabled", result.Enabled), ("Files", result.MaximumFiles),
                ("Bytes per file", result.MaximumFileBytes), ("Total bytes", result.MaximumTotalBytes), ("Path bytes", result.MaximumPathBytes)));
        });

        var file = Argument("file", "The .mfd file, or - for stdin with --design-path");
        var design = Optional("--design-path", "The design's safe relative package path (default: its filename)");
        var assets = new Option<string[]>("--asset") { Description = "A referenced asset as PACKAGE_PATH=LOCAL_FILE; repeat for each file", AllowMultipleArgumentsPerToken = false };
        var target = Optional("--mapping", "Append to this mapping instead of creating one");
        var head = Optional("--expected-head", "The exact head revision reviewed for an append");
        var name = Optional("--name", "Name for a new mapping");
        var description = Optional("--description", "Description for a new mapping");
        var import = new Command("import", "Convert an MFD package and show its complete review; does not publish a revision")
            .With(project, file, design, assets, target, head, name, description);
        import.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            var targetId = invocation.OptionalId(target);
            if (targetId is null && string.IsNullOrWhiteSpace(invocation.Value(name)))
                throw CliException.Usage("A new mapping requires --name.");
            if (targetId is not null && (invocation.Value(name) is not null || invocation.Value(description) is not null))
                throw CliException.Usage("An append cannot change the mapping's name or description.");
            if (targetId is null && invocation.Value(head) is not null)
                throw CliException.Usage("--expected-head is only valid with --mapping.");
            var expectedHead = targetId is null ? null : CliInvocation.Id(invocation.Value(head), "--expected-head");
            var path = invocation.Value(file)!;
            var package = await PackageAsync(invocation, path, invocation.Value(design) ?? FileName(path) ??
                throw CliException.Usage("Stdin requires --design-path."), invocation.Value(assets) ?? []);
            var operation = targetId is null ? ApiOperations.ReviewMfdCreate : ApiOperations.ReviewMfdAppend;
            var request = (await invocation.RequestAsync(operation)).Path("projectId", projectId);
            if (targetId is null)
                request.Query("name", invocation.Value(name)).Query("description", invocation.Value(description)).Query("backendId", "ferrule");
            else request.Path("mappingId", targetId).Query("expectedHeadRevisionId", expectedHead);
            var bytes = package.Encode();
            request.Body(() => new ByteArrayContent(bytes));
            var review = await ReadReviewAsync(invocation, request);
            if (review.Engine.BackendId != "ferrule" || review.PackageDigest != package.Digest.Value || review.TargetMappingId != targetId || review.ExpectedHeadRevisionId != expectedHead)
                throw InvalidResponse();
            WriteReview(invocation, review);
            invocation.Environment.Error.WriteLine("No revision was published. Review every warning, then use mfd confirm with this review ID and digest.");
        });

        var reviewCommand = new Command("review", "Restore your import review without publishing").With(project, reviewId);
        reviewCommand.Handle(globals, async invocation => WriteReview(invocation, await ReadReviewAsync(invocation,
            (await invocation.RequestAsync(ApiOperations.GetMfdImportReview)).Path("projectId", invocation.Id(project)).Path("reviewId", invocation.Id(reviewId)))));

        var wait = new Option<bool>("--wait") { Description = "Wait for confirmation to settle, retrying only this same review and key" };
        var timeout = Timeout("How long to wait for confirmation", TimeSpan.FromMinutes(10));
        var confirm = new Command("confirm", "Publish or recover only the exact server-owned conversion you reviewed")
            .With(project, reviewId, digest, acknowledge, wait, timeout);
        confirm.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            var id = invocation.Id(reviewId);
            var reviewedDigest = Digest(invocation.Value(digest));
            var review = await ReadReviewAsync(invocation, (await invocation.RequestAsync(ApiOperations.GetMfdImportReview))
                .Path("projectId", projectId).Path("reviewId", id));
            if (review.ReviewId != id || review.ReviewDigest != reviewedDigest)
                throw new CliException(CliExitCode.PreconditionFailed, "The supplied digest does not match this review.", "reviewMismatch");
            if (review.Warnings.Count != 0 && !invocation.Value(acknowledge))
                throw CliException.Usage("Read every warning, then pass --acknowledge-warnings to confirm that exact review.");
            var request = (await invocation.RequestAsync(ApiOperations.ConfirmMfdImport)).Path("projectId", projectId)
                .Path("reviewId", id).Header("Idempotency-Key", id)
                .Body(CatalogCommands.JsonBody(new CatalogInterchangeConfirmationV1(reviewedDigest, invocation.Value(acknowledge)), ApiOperations.ConfirmMfdImport));
            var result = invocation.Value(wait)
                ? await PollAsync(invocation, invocation.Value(timeout), "MFD import", async () =>
                {
                    var current = await ConfirmAsync(invocation, request, projectId, review.TargetMappingId);
                    return Pending(current) ? null : current;
                })
                : await ConfirmAsync(invocation, request, projectId, review.TargetMappingId);
            invocation.Output.Write(result, output =>
            {
                output.Fields(("Outcome", result.Outcome));
                if (result.Receipt is { } receipt) Upload.WriteReceipt(output, receipt);
            });
            if (Pending(result)) invocation.Environment.Error.WriteLine("Confirmation is pending. Repeat this same confirm command and digest; do not create another import.");
            return result.Outcome switch
            {
                CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid => (int)CliExitCode.Success,
                CatalogRevisionIngestionOutcomeV1.HeadConflict or CatalogRevisionIngestionOutcomeV1.IdempotencyConflict => (int)CliExitCode.Conflict,
                CatalogRevisionIngestionOutcomeV1.Rejected => (int)CliExitCode.InvalidRequest,
                CatalogRevisionIngestionOutcomeV1.AttemptExhausted => (int)CliExitCode.OperationFailed,
                CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound => (int)CliExitCode.NotFound,
                _ => (int)CliExitCode.Success
            };
        });

        var export = new Command("export", "Review a revision's MFD export; does not download until explicit consent")
            .With(project, mapping, revision);
        export.Handle(globals, async invocation =>
        {
            WriteReview(invocation, await ReadReviewAsync(invocation, (await invocation.RequestAsync(ApiOperations.ReviewMfdExport))
                .Path("projectId", invocation.Id(project)).Path("mappingId", invocation.Id(mapping)).Path("revisionId", invocation.Id(revision))));
            invocation.Environment.Error.WriteLine("Review every warning, then use mfd download with this review ID and digest.");
        });
        var output = Required("--output", "Destination file, or - for verified bytes on stdout; existing files are not replaced");
        var download = new Command("download", "Download only the reviewed MFD export, verifying its SHA-256 before keeping bytes")
            .With(project, mapping, revision, reviewId, digest, acknowledge, output);
        download.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.DownloadMfdExport)).Path("projectId", invocation.Id(project))
                .Path("mappingId", invocation.Id(mapping)).Path("revisionId", invocation.Id(revision)).Path("reviewId", invocation.Id(reviewId))
                .Body(CatalogCommands.JsonBody(new CatalogInterchangeConfirmationV1(Digest(invocation.Value(digest)), invocation.Value(acknowledge)), ApiOperations.DownloadMfdExport));
            using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
            await response.RequireSuccessAsync(invocation.Cancellation);
            await SaveDownloadAsync(invocation, response, invocation.Value(output)!);
        });
        return new Command("mfd", "Bounded MapForce import and export with explicit warning review")
            .With(capabilities, import, reviewCommand, confirm, export, download);
    }

    internal static async Task<InterchangePackage> PackageAsync(CliInvocation invocation, string file, string design, string[] assets)
    {
        if (!design.EndsWith(".mfd", StringComparison.OrdinalIgnoreCase) || assets.Length >= InterchangePackage.MaximumFiles)
            throw CliException.Usage("Choose an .mfd design and at most 127 referenced files.");
        try
        {
            var files = new List<InterchangeFile> { new(design, await ReadInputAsync(invocation, file, InterchangePackage.MaximumFileBytes, "MFD design")) };
            if (files[0].ByteCount == 0) throw CliException.Usage("The MFD design is empty.");
            var remaining = InterchangePackage.MaximumTotalBytes - files[0].ByteCount;
            foreach (var asset in assets)
            {
                var equals = asset.IndexOf('=');
                if (equals <= 0 || equals == asset.Length - 1 || asset[(equals + 1)..] == "-")
                    throw CliException.Usage("Each --asset must be PACKAGE_PATH=LOCAL_FILE; only the design may use stdin.");
                var bytes = await ReadInputAsync(invocation, asset[(equals + 1)..], Math.Min(remaining, InterchangePackage.MaximumFileBytes), "MFD asset");
                files.Add(new(asset[..equals], bytes));
                remaining -= bytes.Length;
            }
            if (files.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new ArgumentException();
            return new(design, files);
        }
        catch (ArgumentException) { throw CliException.Usage("The package has an unsafe/duplicate path or exceeds the 4 MiB/file, 16 MiB total limits."); }
    }

    private static async Task<CatalogInterchangeReviewV1> ReadReviewAsync(CliInvocation invocation, ApiRequest request)
    {
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        await response.RequireSuccessAsync(invocation.Cancellation);
        if (response.Message.Content.Headers.ContentType?.MediaType != "application/json") throw InvalidResponse();
        var bytes = await response.ReadBytesAsync(MaximumReviewBytes, invocation.Cancellation);
        try
        {
            var value = JsonSerializer.Deserialize<CatalogInterchangeReviewV1>(bytes, WeftApiClient.Json) ?? throw InvalidResponse();
            _ = CliInvocation.Id(value.ReviewId, "review ID");
            _ = Digest(value.ReviewDigest); _ = Digest(value.PackageDigest); _ = Digest(value.NativeDigest);
            if (value.ExpiresAt == default || (value.TargetMappingId is null) != (value.ExpectedHeadRevisionId is null) ||
                value.TargetMappingId is not null && (!ValidId(value.TargetMappingId) || !ValidId(value.ExpectedHeadRevisionId)) ||
                !Text(value.Summary, 8192) || value.Warnings is null || value.Warnings.Count > 256 ||
                value.Warnings.Any(item => item is null || !Text(item.Code, 128) || !Text(item.Message, 2048)) || value.Engine is null ||
                !Text(value.Engine.BackendId, 128) || !Text(value.Engine.EngineRevision, 256) ||
                !Text(value.Engine.BackendBuild, 2048) || !Text(value.Engine.AdapterBuild, 256)) throw InvalidResponse();
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or CliException) { throw InvalidResponse(); }
    }

    private static async Task<CatalogRevisionIngestionResultV1> ConfirmAsync(CliInvocation invocation, ApiRequest request, string project, string? target)
    {
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        if (response.Message.Content.Headers.ContentType?.MediaType != "application/json")
        {
            await response.RequireSuccessAsync(invocation.Cancellation);
            throw InvalidResponse();
        }
        var bytes = await response.ReadBytesAsync(MaximumReviewBytes, invocation.Cancellation);
        CatalogRevisionIngestionResultV1? value;
        try { value = JsonSerializer.Deserialize<CatalogRevisionIngestionResultV1>(bytes, WeftApiClient.Json); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { value = null; }
        if (value is null || !ValidStatus(response.Status, value.Outcome))
        {
            await response.RequireSuccessAsync(invocation.Cancellation);
            throw InvalidResponse();
        }
        if (value.Receipt is { } receipt && !ReceiptMatches(receipt, await invocation.OrganizationAsync(), project, target)) throw InvalidResponse();
        if (value.Outcome is CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid &&
            value.Receipt?.Phase != CatalogRevisionIngestionPhaseV1.Committed) throw InvalidResponse();
        return value;
    }
    private static bool ReceiptMatches(CatalogRevisionIngestionReceiptV1 receipt, string organization, string project, string? target) =>
        receipt.Project is { } scope && scope.OrganizationId == organization && scope.ProjectId == project &&
        receipt.FutureMapping is { } mapping && mapping.OrganizationId == organization && mapping.ProjectId == project &&
        receipt.FutureRevision is { } revision && revision.OrganizationId == organization && revision.ProjectId == project &&
        revision.MappingId == mapping.MappingId && (target is null || mapping.MappingId == target) &&
        ValidId(receipt.OperationId) && ValidId(mapping.MappingId) && ValidId(revision.RevisionId);
    private static bool ValidId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static bool ValidStatus(int status, CatalogRevisionIngestionOutcomeV1 outcome) => outcome switch
    {
        CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid => status == 200,
        CatalogRevisionIngestionOutcomeV1.InProgress => status == 202,
        CatalogRevisionIngestionOutcomeV1.RetryableFailure => status == 503,
        CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound => status == 404,
        CatalogRevisionIngestionOutcomeV1.Rejected => status is 400 or 413 or 422,
        CatalogRevisionIngestionOutcomeV1.NeedsSource or CatalogRevisionIngestionOutcomeV1.HeadConflict or
            CatalogRevisionIngestionOutcomeV1.AttemptExhausted or CatalogRevisionIngestionOutcomeV1.IdempotencyConflict or
            CatalogRevisionIngestionOutcomeV1.ClaimLost => status == 409,
        _ => false
    };
    private static bool Pending(CatalogRevisionIngestionResultV1 value) => value.Outcome is
        CatalogRevisionIngestionOutcomeV1.InProgress or CatalogRevisionIngestionOutcomeV1.NeedsSource or
        CatalogRevisionIngestionOutcomeV1.RetryableFailure or CatalogRevisionIngestionOutcomeV1.ClaimLost;
    private static void WriteReview(CliInvocation invocation, CatalogInterchangeReviewV1 value) => invocation.Output.Write(value, output =>
    {
        output.Fields(("Review", value.ReviewId), ("Review digest", value.ReviewDigest), ("Package digest", value.PackageDigest),
            ("Native digest", value.NativeDigest), ("Summary", value.Summary), ("Engine", value.Engine.EngineRevision),
            ("Expires", value.ExpiresAt), ("Target mapping", value.TargetMappingId), ("Expected head", value.ExpectedHeadRevisionId));
        foreach (var warning in value.Warnings) output.Line(CliOutput.Safe(warning.Code + ": " + warning.Message));
    });
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && new UTF8Encoding(false, true).GetByteCount(value) <= maximum;
    private static string Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        ? value : throw CliException.Usage("A review digest must be 64 lowercase SHA-256 hexadecimal characters.");
    private static CliException InvalidResponse() => new(CliExitCode.Transport, "The server returned an invalid interchange response.", "invalidResponse");

    private static async Task SaveDownloadAsync(CliInvocation invocation, ApiResponse response, string destination)
    {
        if (response.Message.Content.Headers.ContentType is not { } type || type.Parameters.Count != 0 ||
            type.MediaType is not ("application/xml" or "application/zip") ||
            !response.Message.Headers.TryGetValues("X-Content-Sha256", out var values)) throw InvalidResponse();
        var headers = values.ToArray();
        if (headers.Length != 1) throw InvalidResponse();
        string expected;
        try { expected = Digest(headers[0]); }
        catch (CliException) { throw InvalidResponse(); }
        var bytes = await response.ReadBytesAsync(MaximumDownloadBytes, invocation.Cancellation);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected) throw InvalidResponse();
        if (destination == "-")
        {
            await using var stdout = invocation.Environment.OpenStandardOutput();
            await stdout.WriteAsync(bytes, invocation.Cancellation);
            await stdout.FlushAsync(invocation.Cancellation);
        }
        else
        {
            string? temporary = null;
            try
            {
                var path = Path.GetFullPath(destination);
                temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".partial");
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await stream.WriteAsync(bytes, invocation.Cancellation);
                invocation.Cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw CliException.Usage("The export destination cannot be written, or already exists."); }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
        }
        invocation.Environment.Error.WriteLine($"Wrote {bytes.Length} verified bytes (sha256 {expected}).");
    }
}
