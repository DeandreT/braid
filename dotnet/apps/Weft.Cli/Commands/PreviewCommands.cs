using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class PreviewCommands
{
    private const int MaximumInputBytes = 8 * 1024 * 1024;
    // PreviewPolicy caps the complete run's outputs at 8 MiB, so each stdout staging buffer is bounded too.
    private const int MaximumOutputBytes = 8 * 1024 * 1024;
    private const int MaximumInputs = 16;
    private const int MaximumParameters = 32;

    internal static Command Create(CliGlobals globals, bool draft = false)
    {
        var project = Required("--project", "The project ID");
        var mapping = Required("--mapping", "The mapping ID");
        var revision = Required(draft ? "--draft" : "--revision", draft ? "Your draft ID" : "The revision ID");
        var input = Required("--input", "The primary input document, or - for standard input");
        var named = new Option<string[]>("--named-input")
        {
            Description = "A named input as NAME=FILE; repeat for several",
            AllowMultipleArgumentsPerToken = false
        };
        var parameters = new Option<string[]>("--parameter")
        {
            Description = "A mapping parameter as NAME=VALUE or NAME:KIND=VALUE (KIND: string, integer, float, boolean); repeat for several"
        };
        var target = new Option<string>("--target")
        {
            Description = "Which outputs to produce: all, primary, or the name of one target",
            DefaultValueFactory = _ => "all"
        };
        var requestId = Optional("--request-id", "A UUID that identifies this preview; reuse it to retry safely (default: new)");
        var wait = new Option<bool>("--wait") { Description = "Wait until the run finishes" };
        var timeout = Timeout("How long to wait for the run", TimeSpan.FromMinutes(10));
        var operation = draft ? ApiOperations.PreviewMappingDraft : ApiOperations.SubmitCatalogPreview;
        var create = new Command(draft ? "preview" : "create", draft ? "Preview the current draft checkpoint" : "Run a bounded sample preview of a revision")
            .With(project, mapping, revision, input, named, parameters, target, requestId, wait, timeout);
        create.Handle(globals, async invocation =>
        {
            var key = invocation.OptionalId(requestId) ?? CatalogCommands.NewId();
            var submission = await SubmissionAsync(invocation, input, named, parameters, target);
            var request = (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(project))
                .Path("mappingId", invocation.Id(mapping)).Path(draft ? "draftId" : "revisionId", invocation.Id(revision))
                .Header("Idempotency-Key", key).Body(CatalogCommands.JsonBody(submission, operation));
            var submitted = await invocation.Api.ReadAsync<CatalogPreviewSubmitResultV1>(request, invocation.Cancellation);
            if (!invocation.Value(wait))
            {
                invocation.Output.Write(submitted, output => WriteRun(output, submitted.Run));
                return (int)CliExitCode.Success;
            }
            var run = await PollAsync(invocation, invocation.Value(timeout), "preview run", async () =>
            {
                var current = await ReadRunAsync(invocation, submitted.Run.RunId);
                return current.State is "completed" or "canceled" ? current : null;
            });
            invocation.Output.Write(run, output => WriteRun(output, run));
            return run.Outcome == "succeeded" ? (int)CliExitCode.Success : (int)CliExitCode.OperationFailed;
        });
        return create;
    }

    public static Command Previews(CliGlobals globals)
    {
        var create = Create(globals);
        var runId = Argument("runId", "The preview run ID");
        var get = new Command("get", "Show a preview run").With(runId);
        get.Handle(globals, async invocation =>
        {
            var run = await ReadRunAsync(invocation, invocation.Id(runId));
            invocation.Output.Write(run, output => WriteRun(output, run));
        });

        var cancel = new Command("cancel", "Cancel a queued run, or ask a running one to stop").With(runId);
        cancel.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.CancelCatalogPreview)).Path("runId", invocation.Id(runId));
            var result = await invocation.Api.ReadAsync<CatalogPreviewCancelResultV1>(request, invocation.Cancellation);
            invocation.Output.Write(result, output => output.Line($"Preview {result.Run.RunId}: cancellation {result.Outcome}."));
        });

        var ordinal = new Option<int>("--ordinal") { Description = "The output's ordinal", DefaultValueFactory = _ => 0 };
        var destination = new Option<string>("--output")
        {
            Description = "Where to write the output, or - for standard output",
            Required = true
        };
        var download = new Command("download", "Download one output of a succeeded preview run, verifying its digest")
            .With(runId, ordinal, destination);
        download.Handle(globals, async invocation =>
        {
            var id = invocation.Id(runId);
            var index = invocation.Value(ordinal);
            if (index is < 0 or > 63)
                throw CliException.Usage("--ordinal must be between 0 and 63.");
            var run = await ReadRunAsync(invocation, id);
            var output = run.Outputs.FirstOrDefault(item => item.Ordinal == index) ?? throw new CliException(CliExitCode.NotFound,
                "The run has no output with that ordinal (notFound).", "notFound");
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogPreviewOutput)).Path("runId", id)
                .Path("ordinal", index.ToString(CultureInfo.InvariantCulture));
            using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
            await response.RequireSuccessAsync(invocation.Cancellation);
            var written = await DownloadAsync(invocation, response, invocation.Value(destination)!, output);
            invocation.Environment.Error.WriteLine($"Wrote {written.ToString(CultureInfo.InvariantCulture)} bytes (sha256 {output.Sha256}).");
        });

        return new Command("previews", "Sample previews of a revision").With(create, get, cancel, download);
    }

    private static async Task<CatalogPreviewRunV1> ReadRunAsync(CliInvocation invocation, string runId)
    {
        var request = (await invocation.RequestAsync(ApiOperations.GetCatalogPreview)).Path("runId", runId);
        return await invocation.Api.ReadAsync<CatalogPreviewRunV1>(request, invocation.Cancellation);
    }

    private static async Task<CatalogPreviewSubmissionV1> SubmissionAsync(CliInvocation invocation, Option<string> input,
        Option<string[]> named, Option<string[]> parameters, Option<string> target)
    {
        var budget = MaximumInputBytes;
        async Task<string> Read(string path, string what)
        {
            var bytes = await ReadInputAsync(invocation, path, budget, what);
            if (bytes.Length == 0)
                throw CliException.Usage($"The {what} is empty.");
            budget -= bytes.Length;
            return Convert.ToBase64String(bytes);
        }
        var inputs = new List<CatalogPreviewInputV1> { new(await Read(invocation.Value(input)!, "primary input")) };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in invocation.Value(named) ?? [])
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0 || equals == entry.Length - 1 || !names.Add(entry[..equals]))
                throw CliException.Usage("--named-input must be NAME=FILE with a unique name.");
            if (entry[(equals + 1)..] == "-")
                throw CliException.Usage("Only the primary input can be read from standard input.");
            inputs.Add(new(await Read(entry[(equals + 1)..], "named input " + entry[..equals]), entry[..equals]));
        }
        if (inputs.Count > MaximumInputs)
            throw CliException.Usage($"A preview takes at most {MaximumInputs} inputs.");
        var values = (invocation.Value(parameters) ?? []).Select(Parameter).ToArray();
        if (values.Length > MaximumParameters || values.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw CliException.Usage($"Give at most {MaximumParameters} parameters with unique names.");
        var selected = invocation.Value(target)!;
        var selection = selected switch
        {
            "all" or "primary" => new CatalogPreviewTargetV1(selected),
            { Length: > 0 } name => new CatalogPreviewTargetV1("named", name),
            _ => throw CliException.Usage("--target must be all, primary or a target name.")
        };
        return new CatalogPreviewSubmissionV1(selection, values, inputs);
    }

    internal static CatalogPreviewParameterV1 Parameter(string text)
    {
        var equals = text.IndexOf('=');
        if (equals <= 0)
            throw CliException.Usage("--parameter must be NAME=VALUE or NAME:KIND=VALUE.");
        var key = text[..equals];
        var value = text[(equals + 1)..];
        var colon = key.LastIndexOf(':');
        var (name, kind) = colon < 0 ? (key, "string") : (key[..colon], key[(colon + 1)..]);
        if (name.Length == 0 || kind is not ("string" or "integer" or "float" or "boolean"))
            throw CliException.Usage("--parameter kinds are string, integer, float and boolean.");
        return new CatalogPreviewParameterV1(name, kind, value);
    }

    /// <summary>Streams the output to its destination while hashing it, and keeps a file only if the digest matches.</summary>
    private static async Task<long> DownloadAsync(CliInvocation invocation, ApiResponse response, string destination,
        CatalogPreviewOutputV1 expected)
    {
        var advertised = ContentDigest(response);
        if (!string.Equals(Convert.ToHexStringLower(advertised), expected.Sha256, StringComparison.Ordinal))
            throw Mismatch();
        if (expected.ByteCount is < 0 or > MaximumOutputBytes)
            throw Mismatch();
        string? temporary = null;
        MemoryStream? staged = null;
        Stream target;
        try
        {
            if (destination == "-")
                // The server's hard output bound caps staging; stdout cannot retract corrupt bytes.
                target = staged = new MemoryStream(expected.ByteCount);
            else
            {
                temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
                    "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");
                target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw CliException.Usage($"The output file {destination} cannot be written.");
        }
        try
        {
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (target)
            {
                await using var source = await response.ReadStreamAsync(invocation.Cancellation);
                var buffer = new byte[81920];
                int read;
                while ((read = await ReadAsync(source, buffer, invocation.Cancellation)) > 0)
                {
                    total += read;
                    if (total > expected.ByteCount)
                        throw Mismatch();
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), invocation.Cancellation);
                }
                await target.FlushAsync(invocation.Cancellation);
            }
            var digest = hash.GetHashAndReset();
            if (total != expected.ByteCount || !string.Equals(Convert.ToHexStringLower(digest), expected.Sha256, StringComparison.Ordinal) ||
                !advertised.AsSpan().SequenceEqual(digest))
                throw Mismatch();
            if (temporary is not null)
                File.Move(temporary, destination, overwrite: true);
            else
            {
                // GetBuffer remains available after MemoryStream disposal; avoid a second bounded copy.
                await using var stdout = invocation.Environment.OpenStandardOutput();
                await stdout.WriteAsync(staged!.GetBuffer().AsMemory(0, (int)total), invocation.Cancellation);
                await stdout.FlushAsync(invocation.Cancellation);
            }
            return total;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && !invocation.Cancellation.IsCancellationRequested)
        {
            throw CliException.Usage($"The output file {destination} cannot be written.");
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // Network failures while reading the body are transport failures; local write failures are reported separately.
    private static async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new CliException(CliExitCode.Transport, "The download was interrupted; nothing was kept.", "transport");
        }
    }

    private static byte[] ContentDigest(ApiResponse response)
    {
        if (!response.Message.Headers.TryGetValues("Content-Digest", out var values))
            throw Mismatch();
        var entries = values.ToArray();
        if (entries.Length != 1)
            throw Mismatch();
        var text = entries[0];
        if (text is null || !text.StartsWith("sha-256=:", StringComparison.Ordinal) || !text.EndsWith(':'))
            throw Mismatch();
        try
        {
            var digest = Convert.FromBase64String(text["sha-256=:".Length..^1]);
            return digest.Length == SHA256.HashSizeInBytes ? digest : throw Mismatch();
        }
        catch (FormatException) { throw Mismatch(); }
    }

    private static CliException Mismatch() =>
        new(CliExitCode.Transport, "The downloaded output did not match its recorded size and SHA-256 digest; nothing was kept.", "digestMismatch");

    internal static void WriteRun(CliOutput output, CatalogPreviewRunV1 run)
    {
        output.Fields(("Run", run.RunId), ("Revision", run.Revision?.RevisionId), ("Draft", run.Draft?.DraftId), ("Draft version", run.Draft?.Version), ("State", run.State), ("Outcome", run.Outcome),
            ("Attempt", $"{run.Attempt}/{run.MaxAttempts}"), ("Cancel requested", run.CancelRequested), ("Records", run.RecordsWritten),
            ("Failure", run.Failure is { } failure ? $"{failure.Code}: {failure.Message}{(failure.MessageTruncated ? "…" : "")}" : null),
            ("Created", run.CreatedAtUtc), ("Finished", run.FinishedAtUtc), ("Expires", run.ExpiresAtUtc));
        if (run.Outputs.Count == 0)
            return;
        output.Line();
        output.Table(["ORDINAL", "TARGET", "MEDIA TYPE", "RECORDS", "BYTES", "SHA-256"], run.Outputs.Select(item =>
            (IReadOnlyList<object?>)[item.Ordinal, item.TargetName + (item.TargetNameTruncated ? "…" : ""), item.MediaType,
                item.RecordsWritten, item.ByteCount, item.Sha256]));
    }
}
