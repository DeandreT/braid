using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Deployment;

/// <summary>Closed binary protocol for one complete comparison. No canonical payload or executable selection.</summary>
internal static class XmlComparisonProtocol
{
    internal const string Version = "weft.xml-comparison-worker.v1";
    internal const string Comparer = "weft.regression.xml-c14n10-comments.v1";
    internal const string ModeArgument = "--xml-c14n10-comments";
    internal const string ProbeRefusal = "WeftXmlComparisonFailed:decode\n";
    internal const int MaximumHeaderBytes = CompiledRuntimeProtocol.MaximumHeaderBytes;
    internal const int MaximumRequestBytes = MaximumHeaderBytes + 2 * XmlDocumentPolicy.MaximumBytes + 4 * (1 + 2 * XmlDocumentPolicy.MaximumOutputs);
    internal const int MaximumResponseBytes = MaximumHeaderBytes + 4;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    internal sealed record Document(string? Name, int ByteCount, string Sha256, string MediaType, XmlDocumentMetadata? Metadata);
    internal sealed record Limits(int ComparedBytes, int Differences, int OriginalBytesPerSide, long CanonicalBytesPerDocument, long CanonicalBytesTotal);
    internal sealed record Body(OperationStatusV1 ExecutionStatus, Limits Limits, Document[] Expected, Document[] Actual);
    internal sealed record Request(string ProtocolVersion, string ComparerVersion, string RequestId, string RequestDigest, Body Body);
    internal sealed record Invocation(Request Header, XmlComparisonOutputSet Expected, XmlComparisonOutputSet Actual, XmlComparisonWorkerOptions Options);
    internal sealed record Difference(string? Name, XmlCanonicalDifferenceKind Kind, int? ExpectedByteCount, int? ActualByteCount,
        string? ExpectedDigest, string? ActualDigest, long? ExpectedCanonicalByteCount, long? ActualCanonicalByteCount, long? CanonicalByteOffset);
    internal sealed record Diagnostic(string? Name, bool HasKey, XmlComparisonSide? Side, XmlCanonicalPhase Phase,
        XmlCanonicalFailure Reason, long? Observed, long? Limit);
    internal sealed record Response(string ProtocolVersion, string ComparerVersion, string RequestId, string RequestDigest,
        RegressionComparisonOutcome Outcome, XmlCanonicalFailure Reason, int TotalDifferences, Difference[] Differences,
        XmlCanonicalTotals? ExpectedCanonical, XmlCanonicalTotals? ActualCanonical, Diagnostic? Diagnostic);

    internal static void VerifyRuntimeDependencies(CancellationToken token)
    {
        // Fixed host-owned health input, not a fallback acceptance limit for requests. Exercise the actual
        // package's LoadInput/GetOutput under the existing read grants before an empty probe can reach decode.
        using var document = XmlCanonicalDocument.Open("<probe/>"u8, 8, 15, token);
        Span<byte> bytes = stackalloc byte[16];
        var count = document.Read(bytes, token);
        if (count != 15 || !bytes[..count].SequenceEqual("<probe></probe>"u8) || document.Read(bytes, token) != 0) throw Invalid();
    }

    internal static Request For(XmlComparisonOutputSet expected, XmlComparisonOutputSet actual, OperationStatusV1 status,
        XmlComparisonWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(options);
        var body = new Body(status, new(options.Comparison.ComparedBytes, options.Comparison.Differences,
            options.Canonical.OriginalBytesPerSide, options.Canonical.CanonicalBytesPerDocument, options.Canonical.CanonicalBytesTotal),
            Describe(expected), Describe(actual));
        var request = new Request(Version, Comparer, Guid.NewGuid().ToString("D"), Digest(body), body);
        Validate(request);
        return request;
    }

    internal static byte[] EncodeRequest(Request request, XmlComparisonOutputSet expected, XmlComparisonOutputSet actual)
    {
        Validate(request);
        if (Digest(request.Body with { Expected = Describe(expected), Actual = Describe(actual) }) != request.RequestDigest) throw Invalid();
        using var output = new MemoryStream();
        Write(output, JsonSerializer.SerializeToUtf8Bytes(request, Json), MaximumHeaderBytes);
        foreach (var document in expected.Outputs.Concat(actual.Outputs)) Write(output, document.Span, XmlDocumentPolicy.MaximumBytes);
        if (output.Length > MaximumRequestBytes) throw Invalid();
        return output.ToArray();
    }

    internal static Invocation DecodeRequest(byte[] bytes)
    {
        if (bytes is null || bytes.Length > MaximumRequestBytes) throw Invalid();
        var offset = 0;
        var request = ReadJson<Request>(Read(bytes, ref offset, MaximumHeaderBytes));
        var options = Validate(request);
        XmlComparisonOutputSet ReadSet(Document[] descriptions)
        {
            var result = new List<XmlComparisonOutput>();
            foreach (var document in descriptions)
            {
                var value = Read(bytes, ref offset, document.ByteCount);
                if (value.Length != document.ByteCount || Sha256Digest.FromBytes(value).Value != document.Sha256) throw Invalid();
                result.Add(XmlComparisonOutput.Capture(Key(document.Name), value, document.Metadata, document.MediaType));
            }
            return XmlComparisonOutputSet.Capture(result);
        }
        var expected = ReadSet(request.Body.Expected);
        var actual = ReadSet(request.Body.Actual);
        if (offset != bytes.Length) throw Invalid();
        return new(request, expected, actual, options);
    }

    internal static byte[] Compare(Invocation invocation, CancellationToken cancellationToken)
    {
        var result = XmlCanonicalComparison.Compare(invocation.Expected, invocation.Actual, invocation.Header.Body.ExecutionStatus,
            invocation.Options.Comparison, invocation.Options.Canonical, cancellationToken);
        var diagnostic = result.Diagnostic is { } item ? new Diagnostic(item.Key?.Name, item.Key is not null,
            item.Side, item.Phase, item.Reason, item.Observed, item.Limit) : null;
        var header = invocation.Header;
        var response = new Response(Version, Comparer, header.RequestId, header.RequestDigest, result.Outcome,
            result.NotEvaluatedReason, result.TotalDifferenceCount, result.Differences.Select(value => new Difference(value.Key.Name,
                value.Kind, value.ExpectedByteCount, value.ActualByteCount, value.ExpectedDigest?.Value, value.ActualDigest?.Value,
                value.ExpectedCanonicalByteCount, value.ActualCanonicalByteCount, value.CanonicalByteOffset)).ToArray(),
            result.ExpectedCanonical, result.ActualCanonical, diagnostic);
        using var output = new MemoryStream();
        Write(output, JsonSerializer.SerializeToUtf8Bytes(response, Json), MaximumHeaderBytes);
        return output.ToArray();
    }

    internal static XmlCanonicalComparisonResult DecodeResponse(byte[] bytes, Request request,
        XmlComparisonOutputSet expected, XmlComparisonOutputSet actual)
    {
        var options = Validate(request);
        if (bytes is null || Digest(request.Body with { Expected = Describe(expected), Actual = Describe(actual) }) != request.RequestDigest ||
            bytes.Length > MaximumResponseBytes) throw Invalid();
        var offset = 0;
        var response = ReadJson<Response>(Read(bytes, ref offset, MaximumHeaderBytes));
        if (offset != bytes.Length || response.ProtocolVersion != Version || response.ComparerVersion != Comparer ||
            response.RequestId != request.RequestId || response.RequestDigest != request.RequestDigest ||
            !Enum.IsDefined(response.Outcome) || !Enum.IsDefined(response.Reason) || response.Differences is null ||
            response.TotalDifferences < 0 || response.TotalDifferences > expected.Outputs.Select(value => value.Key)
                .Union(actual.Outputs.Select(value => value.Key)).Count()) throw Invalid();
        var differences = new List<XmlCanonicalDifference>();
        var seen = new HashSet<RegressionOutputKey>();
        string? previous = null;
        foreach (var value in response.Differences)
        {
            if (value is null || !Enum.IsDefined(value.Kind)) throw Invalid();
            var key = Key(value.Name);
            var left = expected.Outputs.SingleOrDefault(item => item.Key == key);
            var right = actual.Outputs.SingleOrDefault(item => item.Key == key);
            if (!seen.Add(key) || differences.Count > 0 && StringComparer.Ordinal.Compare(previous, key.Name) >= 0 ||
                left is null && right is null || value.ExpectedByteCount != left?.ByteCount ||
                value.ActualByteCount != right?.ByteCount || value.ExpectedDigest != left?.Digest.Value ||
                value.ActualDigest != right?.Digest.Value) throw Invalid();
            CheckCanonical(value.ExpectedCanonicalByteCount, left);
            CheckCanonical(value.ActualCanonicalByteCount, right);
            var expectedKind = left is null ? XmlCanonicalDifferenceKind.UnexpectedOutput : right is null ? XmlCanonicalDifferenceKind.MissingOutput :
                left.MediaType != right.MediaType ? XmlCanonicalDifferenceKind.MediaTypeDiffer :
                left.Metadata != right.Metadata ? XmlCanonicalDifferenceKind.XmlMetadataDiffer : XmlCanonicalDifferenceKind.CanonicalBytesDiffer;
            if (value.Kind != expectedKind || (value.Kind == XmlCanonicalDifferenceKind.CanonicalBytesDiffer
                ? value.CanonicalByteOffset is not >= 0 || value.CanonicalByteOffset >= Math.Max(value.ExpectedCanonicalByteCount!.Value, value.ActualCanonicalByteCount!.Value)
                : value.CanonicalByteOffset is not null)) throw Invalid();
            differences.Add(new(key, value.Kind, value.ExpectedByteCount, value.ActualByteCount, left?.Digest, right?.Digest,
                value.ExpectedCanonicalByteCount, value.ActualCanonicalByteCount, value.CanonicalByteOffset));
            previous = key.Name;
        }
        XmlCanonicalDiagnostic? diagnostic = null;
        if (response.Diagnostic is { } detail)
        {
            var key = detail.HasKey ? Key(detail.Name) : null;
            if (!Enum.IsDefined(detail.Phase) || !Enum.IsDefined(detail.Reason) || detail.Reason != response.Reason ||
                detail.Side is { } side && !Enum.IsDefined(side) || !detail.HasKey && detail.Name is not null ||
                detail.Observed < 0 || detail.Limit < 0 ||
                key is not null && !(detail.Side switch
                {
                    XmlComparisonSide.Expected => expected.Outputs.Any(item => item.Key == key),
                    XmlComparisonSide.Actual => actual.Outputs.Any(item => item.Key == key),
                    _ => false
                })) throw Invalid();
            diagnostic = new(key, detail.Side, detail.Phase, detail.Reason, detail.Observed, detail.Limit);
        }
        if (response.Outcome == RegressionComparisonOutcome.NotEvaluated)
        {
            if (response.Reason == XmlCanonicalFailure.None || response.TotalDifferences != 0 || differences.Count != 0 ||
                response.ExpectedCanonical is not null || response.ActualCanonical is not null) throw Invalid();
        }
        else
        {
            var requiredHeaderDifferences = expected.Outputs.Select(value => value.Key).Union(actual.Outputs.Select(value => value.Key))
                .Count(key =>
                {
                    var left = expected.Outputs.SingleOrDefault(value => value.Key == key);
                    var right = actual.Outputs.SingleOrDefault(value => value.Key == key);
                    return left is null || right is null || left.MediaType != right.MediaType || left.Metadata != right.Metadata;
                });
            if (request.Body.ExecutionStatus != OperationStatusV1.Succeeded || expected.Outputs.Count == 0 ||
                response.Reason != XmlCanonicalFailure.None || diagnostic is not null ||
                response.TotalDifferences < requiredHeaderDifferences ||
                (response.Outcome == RegressionComparisonOutcome.Passed) != (response.TotalDifferences == 0) ||
                differences.Count != Math.Min(response.TotalDifferences, options.Comparison.Differences)) throw Invalid();
            var expectedTotals = response.ExpectedCanonical ?? throw Invalid();
            var actualTotals = response.ActualCanonical ?? throw Invalid();
            CheckTotals(expectedTotals, expected);
            CheckTotals(actualTotals, actual);
            if (expected.ByteCount > options.Canonical.OriginalBytesPerSide || actual.ByteCount > options.Canonical.OriginalBytesPerSide ||
                (long)expected.ByteCount + actual.ByteCount > options.Comparison.ComparedBytes ||
                expectedTotals.Bytes > options.Canonical.CanonicalBytesTotal - actualTotals.Bytes) throw Invalid();
            var remainingExpected = expectedTotals.Bytes;
            var remainingActual = actualTotals.Bytes;
            foreach (var value in differences)
            {
                var left = value.ExpectedCanonicalByteCount ?? 0;
                var right = value.ActualCanonicalByteCount ?? 0;
                if (left > remainingExpected || right > remainingActual) throw Invalid();
                remainingExpected -= left;
                remainingActual -= right;
            }
        }
        return new(response.Outcome, response.Reason, expected, actual, response.TotalDifferences, differences.AsReadOnly(),
            response.ExpectedCanonical, response.ActualCanonical, diagnostic);

        void CheckCanonical(long? count, XmlComparisonOutput? document)
        {
            if (document is null ? count is not null : count is not > 0 ||
                count > Math.Min(options.Canonical.CanonicalBytesPerDocument, options.Canonical.CanonicalBytesTotal)) throw Invalid();
        }
        void CheckTotals(XmlCanonicalTotals? totals, XmlComparisonOutputSet set)
        {
            if (totals is null || totals.Documents != set.Outputs.Count || totals.Bytes < 0 ||
                totals.Bytes > options.Canonical.CanonicalBytesTotal || set.Outputs.Count == 0 && totals.Bytes != 0 ||
                set.Outputs.Count > 0 && totals.Bytes == 0 ||
                set.Outputs.Count > 0 && totals.Bytes > 0 &&
                (totals.Bytes - 1) / set.Outputs.Count >= options.Canonical.CanonicalBytesPerDocument) throw Invalid();
        }
    }

    private static XmlComparisonWorkerOptions Validate(Request request)
    {
        if (request is null || request.ProtocolVersion != Version || request.ComparerVersion != Comparer ||
            !Guid.TryParseExact(request.RequestId, "D", out var id) || id == Guid.Empty || id.ToString("D") != request.RequestId ||
            request.Body is not { Limits: { } limits, Expected: { } expected, Actual: { } actual } ||
            !Enum.IsDefined(request.Body.ExecutionStatus) || request.RequestDigest != Digest(request.Body)) throw Invalid();
        XmlComparisonWorkerOptions options;
        try { options = new(new(limits.ComparedBytes, limits.Differences), new(limits.OriginalBytesPerSide, limits.CanonicalBytesPerDocument, limits.CanonicalBytesTotal)); }
        catch (ArgumentException) { throw Invalid(); }
        ValidateSet(expected);
        ValidateSet(actual);
        return options;
    }

    private static void ValidateSet(Document[] documents)
    {
        if (documents.Length > XmlDocumentPolicy.MaximumOutputs) throw Invalid();
        var seen = new HashSet<RegressionOutputKey>();
        var remaining = XmlDocumentPolicy.MaximumBytes;
        string? previous = null;
        for (var index = 0; index < documents.Length; index++)
        {
            var value = documents[index];
            if (value is null || !seen.Add(Key(value.Name)) || index > 0 && StringComparer.Ordinal.Compare(previous, value.Name) >= 0 ||
                value.ByteCount < 0 || value.ByteCount > remaining || !Hash(value.Sha256) || value.MediaType is null ||
                System.Text.Encoding.UTF8.GetByteCount(value.MediaType) > 256 ||
                JsonSerializer.SerializeToUtf8Bytes(value.Metadata, Json).Length > XmlDocumentPolicy.MaximumMetadataBytes) throw Invalid();
            remaining -= value.ByteCount;
            previous = value.Name;
        }
    }

    private static Document[] Describe(XmlComparisonOutputSet set) => set.Outputs.Select(value =>
        new Document(value.Key.Name, value.ByteCount, value.Digest.Value, value.MediaType, value.Metadata)).ToArray();
    private static RegressionOutputKey Key(string? name)
    {
        try { return name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name); }
        catch (RegressionComparisonException) { throw Invalid(); }
    }
    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(Body body) => Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(body, Json)).Value;
    private static T ReadJson<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            CheckKeys(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or XmlDocumentException) { throw Invalid(); }
    }
    private static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); CheckKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckKeys(item);
    }
    private static ReadOnlySpan<byte> Read(byte[] bytes, ref int offset, int maximum)
    {
        if (offset > bytes.Length - 4) throw Invalid();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4;
        if (length < 0 || length > maximum || length > bytes.Length - offset) throw Invalid();
        var result = bytes.AsSpan(offset, length); offset += length; return result;
    }
    private static void Write(Stream stream, ReadOnlySpan<byte> bytes, int maximum)
    {
        if (bytes.Length > maximum) throw Invalid();
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        stream.Write(prefix); stream.Write(bytes);
    }
    private static InvalidOperationException Invalid() => new("XML comparison protocol refused.");
}
