using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

/// <summary>
/// A resumable native upload. The operation ID is the request's idempotency key and stays the same for every
/// attempt: retries, re-sending the source when the server reports it needs it again, and source-free resume.
/// </summary>
internal sealed class Upload
{
    private const int MaximumResends = 2;
    private const int MaximumOutcomeBytes = 1 << 20;

    public Argument<string> File { get; } = new("file") { Description = "The native mapping file, or - for standard input" };
    public Option<string?> FileName { get; } = new("--filename") { Description = "The display filename (default: the file's name)" };
    public Option<string?> OperationId { get; } = new("--operation-id")
    {
        Description = "Continue an earlier upload: the same operation ID with the same file (default: a new ID)"
    };
    public Option<bool> NoWait { get; } = new("--no-wait") { Description = "Return while validation is still in progress" };
    public Option<TimeSpan> Timeout { get; } = CommandSupport.Timeout("How long to wait for validation", TimeSpan.FromMinutes(10));
    public Symbol[] Symbols => [FileName, OperationId, NoWait, Timeout, File];

    public async Task<int> RunAsync(CliInvocation invocation, string projectId, ApiRequest request)
    {
        var key = invocation.OptionalId(OperationId) ?? CatalogCommands.NewId();
        var path = invocation.Value(File)!;
        var bytes = await ReadInputAsync(invocation, path, CatalogCommands.MaximumNativeBytes, "native mapping");
        if (bytes.Length == 0)
            throw CliException.Usage("The native mapping file is empty.");
        request.Query("originalFilename", invocation.Value(FileName) ?? CommandSupport.FileName(path))
            .Header("Idempotency-Key", key)
            .Body(() =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return content;
            });
        invocation.Environment.Error.WriteLine($"Upload operation {key}. If it is interrupted, rerun with --operation-id {key} " +
            $"or run: weft ingestions resume --project {projectId} {key}");
        var resends = 0;
        return await SettleAsync(invocation, projectId, key, await SendAsync(invocation, request), async () =>
            ++resends <= MaximumResends ? await SendAsync(invocation, request) : null);
    }

    public async Task<int> ResumeAsync(CliInvocation invocation, string projectId, string operationId) =>
        await SettleAsync(invocation, projectId, operationId, await ResumeOnceAsync(invocation, projectId, operationId), null);

    private async Task<int> SettleAsync(CliInvocation invocation, string projectId, string key, CatalogRevisionIngestionResultV1 first,
        Func<Task<CatalogRevisionIngestionResultV1?>>? resend)
    {
        var caller = invocation.Cancellation;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (!invocation.Value(NoWait))
            budget.CancelAfter(invocation.Value(Timeout));
        using var scope = invocation.WithCancellation(budget.Token);
        try { return await SettleCoreAsync(invocation, projectId, key, first, resend); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new CliException(CliExitCode.OperationFailed,
                $"Timed out waiting for upload {key}. Resume it later with: weft ingestions resume --project {projectId} {key}", "timeout");
        }
        catch (CliException) when (!caller.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new CliException(CliExitCode.OperationFailed,
                $"Timed out waiting for upload {key}. Resume it later with: weft ingestions resume --project {projectId} {key}", "timeout");
        }
    }

    private async Task<int> SettleCoreAsync(CliInvocation invocation, string projectId, string key, CatalogRevisionIngestionResultV1 first,
        Func<Task<CatalogRevisionIngestionResultV1?>>? resend)
    {
        var time = invocation.Environment.Time;
        var deadline = time.GetUtcNow() + invocation.Value(Timeout);
        var interval = TimeSpan.FromSeconds(1);
        var result = first;
        while (true)
        {
            switch (result.Outcome)
            {
                case CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid:
                    invocation.Output.Write(result, output => WriteCommitted(output, result));
                    return (int)CliExitCode.Success;
                case CatalogRevisionIngestionOutcomeV1.NeedsSource:
                    // The reserved source is gone: the same key with the same bytes supplies it again.
                    if (resend is not null && await resend() is { } resent)
                    {
                        result = resent;
                        continue;
                    }
                    throw new CliException(CliExitCode.OperationFailed,
                        $"The server needs the native file again. Rerun the upload with --operation-id {key} and the same file.", "needsSource");
                case CatalogRevisionIngestionOutcomeV1.InProgress or CatalogRevisionIngestionOutcomeV1.RetryableFailure or
                    CatalogRevisionIngestionOutcomeV1.ClaimLost:
                    if (invocation.Value(NoWait))
                    {
                        invocation.Output.Write(result, output => output.Line(
                            $"Upload {key} is {Name(result.Outcome)}. Check it with: weft ingestions status --project {projectId} {key}"));
                        return (int)CliExitCode.Success;
                    }
                    var remaining = deadline - time.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                        throw new CliException(CliExitCode.OperationFailed,
                            $"Upload {key} is still {Name(result.Outcome)}. Resume it later with: weft ingestions resume --project {projectId} {key}",
                            "timeout");
                    await Task.Delay(interval < remaining ? interval : remaining, time, invocation.Cancellation);
                    interval = TimeSpan.FromSeconds(Math.Min(5, interval.TotalSeconds * 2));
                    result = await ResumeOnceAsync(invocation, projectId, key);
                    continue;
                case CatalogRevisionIngestionOutcomeV1.Rejected:
                    throw new CliException(CliExitCode.InvalidRequest,
                        $"The server rejected the native file ({(result.Rejection is { } rejection ? Name(rejection) : "rejected")}).", "rejected");
                case CatalogRevisionIngestionOutcomeV1.HeadConflict:
                    throw new CliException(CliExitCode.Conflict, "The mapping's head changed; the revision was not appended.", "headConflict");
                case CatalogRevisionIngestionOutcomeV1.IdempotencyConflict:
                    throw new CliException(CliExitCode.Conflict,
                        "This operation ID was already used for a different upload. Use a new --operation-id.", "idempotencyConflict");
                case CatalogRevisionIngestionOutcomeV1.AttemptExhausted:
                    throw new CliException(CliExitCode.OperationFailed, "The upload used all of its processing attempts.", "attemptExhausted");
                default:
                    throw new CliException(CliExitCode.NotFound, "Not found, or you do not have access to it (notFound).", "notFound");
            }
        }
    }

    private static async Task<CatalogRevisionIngestionResultV1> ResumeOnceAsync(CliInvocation invocation, string projectId, string operationId)
    {
        var request = (await invocation.RequestAsync(ApiOperations.ResumeCatalogRevisionIngestion)).Path("projectId", projectId)
            .Path("operationId", operationId);
        return await SendAsync(invocation, request);
    }

    /// <summary>The ingestion outcome document, which also accompanies conflict, rejection and retryable statuses.</summary>
    internal static async Task<CatalogRevisionIngestionResultV1> SendAsync(CliInvocation invocation, ApiRequest request)
    {
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        var content = response.Message.Content;
        if (content.Headers.ContentType?.MediaType == "application/json" && content.Headers.ContentLength is null or <= MaximumOutcomeBytes)
        {
            var bytes = await response.ReadBytesAsync(MaximumOutcomeBytes, invocation.Cancellation);
            if (bytes.Length <= MaximumOutcomeBytes && IsOutcome(bytes))
                return await response.ReadOutcomeAsync<CatalogRevisionIngestionResultV1>(invocation.Cancellation);
        }
        await response.RequireSuccessAsync(invocation.Cancellation);
        throw new CliException(CliExitCode.Transport, "The server's response was not a valid Weft response.", "invalidResponse");
    }

    private static bool IsOutcome(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("outcome", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteCommitted(CliOutput output, CatalogRevisionIngestionResultV1 result)
    {
        var receipt = result.Receipt!;
        output.Line($"Committed revision {receipt.FutureRevision.RevisionId} (sequence {receipt.CommittedSequence}, " +
            $"{Name(receipt.CommittedAdmission!.Value)}) of mapping {receipt.FutureMapping.MappingId}.");
    }

    internal static void WriteReceipt(CliOutput output, CatalogRevisionIngestionReceiptV1 receipt) => output.Fields(
        ("Operation", receipt.OperationId), ("Phase", receipt.Phase), ("Claims", receipt.ClaimCount), ("Reserved", receipt.ReservedAtUtc),
        ("Completed", receipt.CompletedAtUtc), ("File", receipt.OriginalFilename), ("Mapping", receipt.FutureMapping.MappingId),
        ("Revision", receipt.FutureRevision.RevisionId), ("Sequence", receipt.CommittedSequence), ("Admission", receipt.CommittedAdmission),
        ("Latest failure", receipt.LatestFailure));

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
