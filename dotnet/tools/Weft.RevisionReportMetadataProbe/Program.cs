using System.Globalization;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

// Test-only independent-process readback through the public production DAL.
// Connection credentials stay in the named environment variable, never stdout.
internal static class Program
{
    private const int MaximumInputBytes = 32768;
    private static readonly string[] CommonFields =
        ["mode", "organizationId", "projectId", "mappingId", "revisionId", "actorId", "operation"];

    private static async Task<int> Main(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args.Length != 0) throw new ArgumentException("Arguments are unavailable.");
            var bytes = await ReadInputAsync(deadline.Token);
            _ = new UTF8Encoding(false, true).GetString(bytes);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                throw new ArgumentException("A byte-order mark is unavailable.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var input = document.RootElement;
            var mode = Text(input, "mode");
            Properties(input, CommonFields.Concat(mode switch
            {
                "current" => ["profile", "reportSchemaVersion", "algorithmVersion", "codecVersion", "policy"],
                "retained" => new[] { "derivationId" },
                _ => throw new ArgumentException("The operation is unavailable.")
            }).ToArray());
            var scope = new RevisionScope(Id(input, "organizationId"), Id(input, "projectId"),
                Id(input, "mappingId"), Id(input, "revisionId"));
            var actor = new ActorContext(new PrincipalId(Id(input, "actorId")));
            var operation = Text(input, "operation") switch
            {
                "projection" => RevisionReportOperation.Projection,
                "validation" => RevisionReportOperation.Validation,
                _ => throw new ArgumentException("The report operation is unavailable.")
            };
            RevisionReportBackendProfile? profile = null;
            RevisionReportPolicy? policy = null;
            string? reportSchemaVersion = null, algorithmVersion = null, codecVersion = null;
            Guid? derivationId = null;
            if (mode == "current")
            {
                var profileJson = input.GetProperty("profile");
                Properties(profileJson, ["backendId", "backendRevision", "backendBuild", "adapterBuild"]);
                profile = new RevisionReportBackendProfile(Text(profileJson, "backendId"),
                    Text(profileJson, "backendRevision"), Text(profileJson, "backendBuild"), Text(profileJson, "adapterBuild"));
                policy = Policy(input.GetProperty("policy"));
                reportSchemaVersion = Text(input, "reportSchemaVersion");
                algorithmVersion = Text(input, "algorithmVersion");
                codecVersion = Text(input, "codecVersion");
            }
            else derivationId = Id(input, "derivationId");
            var connection = Environment.GetEnvironmentVariable("WEFT_REPORT_METADATA_PROBE_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connection)) return Error("connectionUnavailable", 4);
            await using var database = new PostgresRevisionReportDatabase(connection);
            var reader = new PostgresRevisionReportMetadataReader(database);
            if (mode == "retained")
            {
                var retained = await reader.GetRetainedAsync(actor, scope, operation, derivationId!.Value, deadline.Token);
                if (retained is null) return Output(new { status = "notFound" });
                var facts = Facts(retained.Key, retained.CacheEntryId);
                facts["derivationId"] = retained.Attempt.DerivationId.ToString("D");
                facts["generation"] = Number(retained.Attempt.Generation);
                facts["completion"] = retained.Attempt.Completion is null ? "pending" :
                    retained.Attempt.Completion.IsUsable ? "usable" : "failed";
                facts["failure"] = retained.Attempt.Completion?.Failure?.ToString();
                facts["hasUsableResult"] = retained.Result is not null;
                facts["derivedObjectId"] = retained.Result?.DerivedObject.Id.ObjectId.ToString("D");
                facts["derivedDigest"] = retained.Result?.DerivedObject.Digest.Value;
                facts["derivedByteCount"] = retained.Result is { } result ? Number(result.DerivedObject.ByteCount) : null;
                return Output(facts);
            }

            var source = await reader.GetSourceAsync(actor, scope, deadline.Token);
            if (source is null) return Output(new { status = "notFound" });
            RevisionReportKey key = operation == RevisionReportOperation.Projection
                ? new ProjectionCacheKey(source.Source, profile!, reportSchemaVersion!, algorithmVersion!, codecVersion!, policy!)
                : new ValidationCacheKey(source.Source, profile!, reportSchemaVersion!, algorithmVersion!, codecVersion!, policy!);
            var snapshot = await reader.GetCurrentAsync(actor, key, deadline.Token);
            if (snapshot is null) return Output(new { status = "notFound" });
            var current = Facts(snapshot.Key, snapshot.CacheEntryId);
            current["state"] = snapshot.State.ToString().ToLowerInvariant();
            current["generation"] = Number(snapshot.Generation);
            current["selectedDerivationId"] = snapshot.SelectedResult?.DerivationId.ToString("D");
            current["currentDerivationId"] = snapshot.CurrentAttempt?.DerivationId.ToString("D");
            return Output(current);
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            return Error("invalidRequest", 2);
        }
        catch
        {
            return Error("metadataFailed", 3);
        }
    }

    private static Dictionary<string, object?> Facts(RevisionReportKey key, Guid? cacheEntryId) => new()
    {
        ["status"] = "found",
        ["organizationId"] = key.Scope.OrganizationId.ToString("D"),
        ["projectId"] = key.Scope.ProjectId.ToString("D"),
        ["mappingId"] = key.Scope.MappingId.ToString("D"),
        ["revisionId"] = key.Scope.RevisionId.ToString("D"),
        ["operation"] = key.Operation.ToString().ToLowerInvariant(),
        ["keyFingerprint"] = key.Fingerprint.Value,
        ["policyFingerprint"] = key.Policy.Fingerprint.Value,
        ["nativeObjectId"] = key.Source.NativeObject.Id.ObjectId.ToString("D"),
        ["nativeDigest"] = key.NativeDigest.Value,
        ["nativeByteCount"] = Number(key.Source.NativeObject.ByteCount),
        ["cacheEntryId"] = cacheEntryId?.ToString("D")
    };

    private static RevisionReportPolicy Policy(JsonElement value)
    {
        Properties(value, ["schemaVersion", "nativeByteLimit", "workerResponseByteLimit", "itemLimit", "responseItemLimit",
            "jsonDepthLimit", "detailDepthLimit", "diagnosticLimit", "validationIssueLimit", "textByteLimit", "headerByteLimit",
            "storedObjectByteLimit", "responseByteLimit"]);
        if (Text(value, "schemaVersion") != "weft.revision-report-policy.v1")
            throw new ArgumentException("The policy version is unavailable.");
        return new RevisionReportPolicy(Int(value, "nativeByteLimit"), Int(value, "workerResponseByteLimit"),
            Int(value, "itemLimit"), Int(value, "responseItemLimit"), Int(value, "jsonDepthLimit"), Int(value, "detailDepthLimit"),
            Int(value, "diagnosticLimit"), Int(value, "validationIssueLimit"), Int(value, "textByteLimit"), Int(value, "headerByteLimit"),
            Int(value, "storedObjectByteLimit"), Int(value, "responseByteLimit"));
    }

    private static async Task<byte[]> ReadInputAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        var buffer = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > MaximumInputBytes) throw new ArgumentException("The input size is unavailable.");
        return buffer.AsSpan(0, count).ToArray();
    }

    private static void Properties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("An object is required.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException("The object fields are unavailable.");
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && property.GetString() is { } text
            ? text : throw new ArgumentException("A string is required.");

    private static int Int(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number : throw new ArgumentException("An integer is required.");

    private static Guid Id(JsonElement value, string name)
    {
        var text = Text(value, name);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D")
            ? id : throw new ArgumentException("A canonical identifier is required.");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static int Error(string status, int exitCode) { Output(new { status }); return exitCode; }
    private static int Output(object value)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumInputBytes) throw new InvalidOperationException("The output is too large.");
        Console.WriteLine(json);
        return 0;
    }
}
