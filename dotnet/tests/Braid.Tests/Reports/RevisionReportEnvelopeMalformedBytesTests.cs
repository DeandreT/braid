using System.Buffers;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.RevisionReportCodecTestSupport;

namespace Braid.Tests;

public sealed class RevisionReportEnvelopeMalformedBytesTests
{
    public static IEnumerable<object[]> AuthoredMalformedEnvelopes()
    {
        var key = ProjectionKey();
        var original = Envelope(Claim(key), ProjectionJson(key));
        foreach (var (name, raw) in new (string, string)[]
        {
            ("missing envelope version", original.Replace("\"schemaVersion\":\"braid.revision-report-envelope.v1\",", "", StringComparison.Ordinal)),
            ("case alias", original.Replace("\"schemaVersion\":\"braid.revision-report-envelope", "\"SchemaVersion\":\"braid.revision-report-envelope", StringComparison.Ordinal)),
            ("unknown envelope field", original.Insert(1, "\"extra\":false,")),
            ("duplicate envelope field", original.Insert(1, "\"generation\":1,")),
            ("escaped duplicate envelope alias", original.Insert(1, "\"\\u0067eneration\":1,")),
            ("null required report", original.Replace(ProjectionJson(key), "null", StringComparison.Ordinal)),
            ("root is array", "[]"),
            ("two root values", original + " {}"),
            ("trailing comma", original[..^1] + ",}"),
            ("comment", "/*not stored JSON*/" + original),
            ("truncated JSON", original[..^1]),
            ("string generation", original.Replace("\"generation\":1", "\"generation\":\"1\"", StringComparison.Ordinal)),
            ("fractional generation", original.Replace("\"generation\":1", "\"generation\":1.0", StringComparison.Ordinal)),
            ("exponent generation", original.Replace("\"generation\":1", "\"generation\":1e0", StringComparison.Ordinal)),
            ("zero generation", original.Replace("\"generation\":1", "\"generation\":0", StringComparison.Ordinal)),
            ("overflow generation", original.Replace("\"generation\":1", "\"generation\":9223372036854775808", StringComparison.Ordinal)),
            ("uppercase canonical ID", original.Replace("6789abcd-6789-abcd-ef01-23456789abcd", "6789ABCD-6789-ABCD-EF01-23456789ABCD", StringComparison.Ordinal)),
            ("zero canonical ID", original.Replace("6789abcd-6789-abcd-ef01-23456789abcd", "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal)),
            ("noncanonical key whitespace", original.Replace("\"key\":{", "\"key\":{ ", StringComparison.Ordinal)),
            ("escaped key alias is not canonical bytes", original.Replace("\"key\":{\"schemaVersion\"", "\"key\":{\"schem\\u0061Version\"", StringComparison.Ordinal)),
            ("bad policy fingerprint", original.Replace(key.Policy.Fingerprint.Value, new string('2', 64), StringComparison.Ordinal)),
            ("unknown policy member", original.Replace("\"policy\":{", "\"policy\":{\"unknown\":1,", StringComparison.Ordinal)),
            ("missing policy default", original.Replace("\"diagnosticLimit\":128,", "", StringComparison.Ordinal)),
            ("numeric enum", original.Replace("\"status\":\"succeeded\"", "\"status\":0", StringComparison.Ordinal)),
            ("enum case alias", original.Replace("\"status\":\"succeeded\"", "\"status\":\"Succeeded\"", StringComparison.Ordinal)),
            ("unknown enum", original.Replace("\"status\":\"succeeded\"", "\"status\":\"okay\"", StringComparison.Ordinal)),
            ("unknown document member", original.Replace("\"document\":{", "\"document\":{\"extra\":null,", StringComparison.Ordinal)),
            ("escaped duplicate document alias", original.Replace("\"document\":{", "\"document\":{\"\\u0066ields\":[],", StringComparison.Ordinal)),
            ("missing physical computed document version", original.Replace(",\"schemaVersion\":\"braid.mapping-projection.v1\"", "", StringComparison.Ordinal)),
            ("null required collection", original.Replace("\"fields\":[]", "\"fields\":null", StringComparison.Ordinal)),
            ("null required collection member", original.Replace("\"fields\":[]", "\"fields\":[null]", StringComparison.Ordinal)),
            ("missing physically present nullable report provenance", original.Replace("\"provenance\":" + OperationProvenanceJson(key) + ",", "", StringComparison.Ordinal)),
            ("unpaired escaped high surrogate", original.Replace("\"fields\":[]", "\"fields\":[\"\\ud800\"]", StringComparison.Ordinal)),
            ("unpaired escaped low surrogate", original.Replace("\"fields\":[]", "\"fields\":[\"\\udc00\"]", StringComparison.Ordinal)),
            ("high surrogate followed by ordinary scalar", original.Replace("\"fields\":[]", "\"fields\":[\"\\ud800\\u0061\"]", StringComparison.Ordinal))
        }) yield return [name, Bytes(raw), RevisionReportCodecFailure.Integrity];
        foreach (var (name, raw) in new (string, string)[]
        {
            ("unknown envelope version", original.Replace("braid.revision-report-envelope.v1", "braid.revision-report-envelope.v2", StringComparison.Ordinal)),
            ("unknown envelope codec", original.Replace("\"codecVersion\":\"braid.revision-report-codec.v1\"", "\"codecVersion\":\"braid.revision-report-codec.v2\"", StringComparison.Ordinal)),
            ("unknown key schema", original.Replace("braid.revision-report-key.v1", "braid.revision-report-key.v2", StringComparison.Ordinal)),
            ("unknown policy schema", original.Replace("braid.revision-report-policy.v1", "braid.revision-report-policy.v2", StringComparison.Ordinal)),
            ("wrong physically present document version", original.Replace("\"schemaVersion\":\"braid.mapping-projection.v1\"", "\"schemaVersion\":\"braid.mapping-projection.v2\"", StringComparison.Ordinal))
        }) yield return [name, Bytes(raw), RevisionReportCodecFailure.UnsupportedFormat];
        yield return ["UTF8 BOM", new byte[] { 0xef, 0xbb, 0xbf }.Concat(Bytes(original)).ToArray(), RevisionReportCodecFailure.Integrity];
        yield return ["invalid UTF8", new byte[] { 0xff }.Concat(Bytes(original)).ToArray(), RevisionReportCodecFailure.Integrity];
        yield return ["UTF8 surrogate scalar", Bytes(original[..^1]).Concat(new byte[] { 0xed, 0xa0, 0x80, (byte)'}' }).ToArray(), RevisionReportCodecFailure.Integrity];
    }

    [Theory]
    [MemberData(nameof(AuthoredMalformedEnvelopes))]
    public void ActualAuthoredWireBytesRefuseBeforeReturningAReport(string scenario, byte[] bytes, RevisionReportCodecFailure expected)
    {
        Assert.NotEmpty(scenario);
        // The expected reference matches these exact hostile bytes, so digest failure cannot hide parser behavior.
        var metadata = Metadata(Claim(ProjectionKey()), bytes);
        var error = Assert.Throws<RevisionReportCodecException>(() =>
            new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(metadata, bytes, new()));
        Assert.Equal(expected, error.Failure);
        Assert.Null(error.InnerException);
    }

    public static IEnumerable<object[]> MissingDefaultedOrNullableProjectionMembers()
    {
        var key = ProjectionKey();
        var parameter = Parameter("{\"kind\":\"string\",\"value\":\"x\"}");
        var original = Envelope(Claim(key), ProjectionJson(key, "[" + parameter + "]"));
        yield return ["defaulted nativeName", original.Replace(",\"nativeName\":null", "", StringComparison.Ordinal)];
        yield return ["nullable defaultValue", original.Replace("\"defaultValue\":{\"kind\":\"string\",\"value\":\"x\"},", "", StringComparison.Ordinal)];
        yield return ["unknown path discriminator", original.Replace("\"kind\":\"document\"", "\"kind\":\"unknownLocator\"", StringComparison.Ordinal)];
        yield return ["duplicate nested discriminator", original.Replace("\"kind\":\"document\"", "\"kind\":\"document\",\"kind\":\"document\"", StringComparison.Ordinal)];
        yield return ["escaped duplicate nested discriminator", original.Replace("\"kind\":\"document\"", "\"kind\":\"document\",\"k\\u0069nd\":\"document\"", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(MissingDefaultedOrNullableProjectionMembers))]
    public void DefaultedNullableAndDiscriminatorMembersAreRequiredInStoredProjectionBytes(string scenario, string raw)
    {
        Assert.NotEmpty(scenario);
        var key = ProjectionKey();
        var bytes = Bytes(raw);
        Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
            new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(Metadata(Claim(key), bytes,
                ProjectionObservation(key, 1)), bytes, new())).Failure);
    }

    public static IEnumerable<object[]> InvalidValidationMembers()
    {
        var key = ValidationKey();
        var original = Envelope(Claim(key), ValidationJson(key, valid: false));
        foreach (var member in new[] { "nativeLocator", "owner", "location", "projectionLocator" })
        {
            var property = member == "owner" ? "\"owner\":{\"kind\":\"failureRule\",\"index\":\"0\"}," : "\"" + member + "\":null";
            var dropped = original.Replace(property + (member == "projectionLocator" ? "" : member == "owner" ? "" : ","), "", StringComparison.Ordinal);
            if (member == "projectionLocator") dropped = original.Replace("," + property, "", StringComparison.Ordinal);
            yield return ["missing " + member, dropped];
        }
        yield return ["missing defaulted locationTruncated", original.Replace("\"locationTruncated\":false,", "", StringComparison.Ordinal)];
        yield return ["stored projection link", original.Replace("\"projectionLocator\":null", "\"projectionLocator\":{\"kind\":\"field\",\"fieldId\":\"f\"}", StringComparison.Ordinal)];
        yield return ["nullable validity contradiction", original.Replace("\"isValid\":false", "\"isValid\":null", StringComparison.Ordinal)];
        yield return ["numeric count as string", original.Replace("\"totalIssueCount\":1", "\"totalIssueCount\":\"1\"", StringComparison.Ordinal)];
        yield return ["missing owner identity", original.Replace(",\"index\":\"0\"", "", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(InvalidValidationMembers))]
    public void StoredValidationRequiresExplicitNativeOnlyNullableAndDefaultedFields(string scenario, string raw)
    {
        Assert.NotEmpty(scenario);
        var key = ValidationKey();
        var bytes = Bytes(raw);
        Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
            new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(Metadata(Claim(key), bytes,
                ValidationObservation(key, valid: false)), bytes, new())).Failure);
    }

    [Fact]
    public void EveryCommittedSourceIdentityAndSelectedProfileComponentMustMatchFullMetadataKey()
    {
        var original = ProjectionKey();
        var scopes = new[]
        {
            new RevisionScope(Guid.NewGuid(), Scope.ProjectId, Scope.MappingId, Scope.RevisionId),
            new RevisionScope(Scope.OrganizationId, Guid.NewGuid(), Scope.MappingId, Scope.RevisionId),
            new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Guid.NewGuid(), Scope.RevisionId),
            new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Guid.NewGuid())
        };
        var sources = scopes.Select(scope => new RevisionReportSource(scope,
            new(PrivateObjectId.Restore(scope, Source.NativeObject.Id.ObjectId, PrivateObjectClass.NativeArtifact), Source.NativeObject.Digest, 3),
            Source.LogicalDocument, Source.DependencyIdentity, Source.SourceBackendId)).Concat(new RevisionReportSource[]
        {
            new RevisionReportSource(Scope, new(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Source.NativeObject.Digest, 3), Source.LogicalDocument, Source.DependencyIdentity, "neutral"),
            new(Scope, new(Source.NativeObject.Id, Sha256Digest.Parse(new string('3', 64)), 3), Source.LogicalDocument, Source.DependencyIdentity, "neutral"),
            new(Scope, new(Source.NativeObject.Id, Source.NativeObject.Digest, 4), Source.LogicalDocument, Source.DependencyIdentity, "neutral"),
            new(Scope, Source.NativeObject, "logical-other", Source.DependencyIdentity, "neutral"),
            new(Scope, Source.NativeObject, Source.LogicalDocument, "dependency-other", "neutral")
        });
        var keys = sources.Select(source => new ProjectionCacheKey(source, Profile, original.SchemaVersion, original.AlgorithmVersion, original.CodecVersion, original.Policy)).ToList();
        keys.Add(new(new(Scope, Source.NativeObject, Source.LogicalDocument, Source.DependencyIdentity, "other"),
            new("other", Profile.BackendRevision, Profile.BackendBuild, Profile.AdapterBuild), original.SchemaVersion, original.AlgorithmVersion, original.CodecVersion, original.Policy));
        foreach (var profile in new RevisionReportBackendProfile[]
        {
            new RevisionReportBackendProfile("neutral", "revision-other", Profile.BackendBuild, Profile.AdapterBuild),
            new("neutral", Profile.BackendRevision, "build-other", Profile.AdapterBuild),
            new("neutral", Profile.BackendRevision, Profile.BackendBuild, "adapter-other")
        }) keys.Add(new(Source, profile, original.SchemaVersion, original.AlgorithmVersion, original.CodecVersion, original.Policy));
        foreach (var key in keys)
        {
            var bytes = Bytes(Envelope(Claim(key), ProjectionJson(key)));
            var metadata = Metadata(Claim(original), bytes);
            Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
                new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(metadata, bytes, new())).Failure);
        }
        Assert.Equal(13, keys.Count);
    }

    [Fact]
    public void ReportProvenanceCannotSubstituteScopeDigestProfileOrConfirmationDespiteCorrectHeader()
    {
        var key = ProjectionKey();
        var report = ProjectionJson(key);
        foreach (var changed in new[]
        {
            report.Replace(Scope.RevisionId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            report.Replace(key.NativeDigest.Value, new string('4', 64), StringComparison.Ordinal),
            report.Replace(Profile.BackendBuild, "other-build", StringComparison.Ordinal),
            report.Replace(Profile.BackendRevision, "other-revision", StringComparison.Ordinal),
            report.Replace(Profile.AdapterBuild, "other-adapter", StringComparison.Ordinal),
            report.Replace("\"confirmed\":true", "\"confirmed\":false", StringComparison.Ordinal)
        })
        {
            var bytes = Bytes(Envelope(Claim(key), changed));
            Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
                new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(Metadata(Claim(key), bytes), bytes, new())).Failure);
        }
    }

    [Fact]
    public void CopyOfCallerOwnedMemoryObservesCancellationBetweenBoundedChunks()
    {
        var key = ProjectionKey();
        var literal = "{\"kind\":\"string\",\"value\":" + Quote(new string('x', 1000)) + "}";
        var parameters = Enumerable.Range(0, 6).Select(index => Parameter(literal)
            .Replace("\"id\":\"p\"", "\"id\":\"p" + index + "\"", StringComparison.Ordinal));
        var bytes = Bytes(Envelope(Claim(key), ProjectionJson(key, "[" + string.Join(',', parameters) + "]")));
        Assert.True(bytes.Length > 4096);
        using var cancellation = new CancellationTokenSource();
        using var memory = new CancelingMemory(bytes, cancellation);
        var metadata = Metadata(Claim(key), bytes, ProjectionObservation(key, 6));
        var registry = new StoredOnlyRegistry();
        Assert.Throws<OperationCanceledException>(() => new RevisionReportEnvelopeCodec(registry)
            .Decode(metadata, memory.View, new(), cancellation.Token));
        Assert.True(memory.SpanCalls >= 2);
        Assert.Equal(0, registry.StoredLookups);
    }

    private sealed class CancelingMemory(byte[] bytes, CancellationTokenSource cancellation) : MemoryManager<byte>
    {
        internal int SpanCalls { get; private set; }
        internal ReadOnlyMemory<byte> View => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            if (++SpanCalls == 2) cancellation.Cancel();
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
