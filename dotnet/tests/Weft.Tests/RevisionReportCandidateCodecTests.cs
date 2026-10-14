using System.Security.Cryptography;
using System.Text;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.RevisionReportCodecTestSupport;

namespace Weft.Tests;

/// <summary>Pure precommit proofs over manually authored bytes; no committed metadata, storage or native worker.</summary>
public sealed class RevisionReportCandidateCodecTests
{
    [Theory]
    [InlineData("projection")]
    [InlineData("valid")]
    [InlineData("invalid")]
    public void IndependentlyAuthoredCandidateProvesWholeUsablePayloadWithoutCommittedMetadata(string operation)
    {
        RevisionReportKey key = operation == "projection" ? ProjectionKey() : ValidationKey();
        var claim = Claim(key, 9007199254740993L);
        var report = key is ProjectionCacheKey projection ? ProjectionJson(projection) : ValidationJson((ValidationCacheKey)key, operation == "valid");
        var expected = key is ProjectionCacheKey pk ? Backend(pk, report) : Backend((ValidationCacheKey)key, report, operation == "valid");
        var bytes = Bytes(Envelope(claim, report));
        var candidate = Candidate(claim, bytes);
        var registry = new StoredOnlyRegistry();

        new RevisionReportEnvelopeCodec(registry).VerifyCandidate(claim, expected, candidate, bytes, new());

        Assert.Equal(1, registry.StoredLookups);
        Assert.Equal(2, registry.Validations);
        Assert.Equal(bytes, candidate.CopyBytes());
        Assert.Equal(claim.CandidateObjectId, candidate.Reference.Id);
        Assert.Equal(bytes.LongLength, candidate.Reference.ByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), candidate.Reference.Digest.Value);
        Assert.Equal(9007199254740993L, claim.Generation);
    }

    [Fact]
    public void ActualEncoderCandidateAlsoRequiresIndependentReadbackAndRetainsOwnedBytes()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var expected = Backend(key, ProjectionJson(key));
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var candidate = codec.Encode(claim, expected);
        var bytes = candidate.CopyBytes();
        codec.VerifyCandidate(claim, expected, candidate, bytes, new());
        bytes[^1] ^= 1;
        Refuses(RevisionReportCodecFailure.Integrity, () => codec.VerifyCandidate(claim, expected, candidate, bytes, new()));
        codec.VerifyCandidate(claim, expected, candidate, candidate.CopyBytes(), new());
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("name")]
    [InlineData("locator")]
    [InlineData("requirement")]
    public void EqualProjectionSummaryCannotAuthorizeAnEntireDifferentParameterPayload(string substitution)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var parameter = Parameter("{\"kind\":\"string\",\"value\":\"alpha\"}");
        var replacement = substitution switch
        {
            "literal" => parameter.Replace("\"alpha\"", "\"omega\"", StringComparison.Ordinal),
            "name" => parameter.Replace("\"name\":\"exact\"", "\"name\":\"other\"", StringComparison.Ordinal),
            "locator" => parameter.Replace("\"kind\":\"document\"", "\"kind\":\"jsonPointer\",\"pointer\":\"/a~0~1b/01\"", StringComparison.Ordinal),
            "requirement" => parameter.Replace("\"requirement\":\"optional\"", "\"requirement\":\"required\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(substitution))
        };
        var expected = Backend(key, ProjectionJson(key, "[" + parameter + "]"), 1);
        var substituted = Backend(key, ProjectionJson(key, "[" + replacement + "]"), 1);
        Assert.Equal(expected.Observation, substituted.Observation);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key, "[" + replacement + "]")));
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .VerifyCandidate(claim, expected, Candidate(claim, bytes), bytes, new()));
    }

    [Theory]
    [InlineData("binaryFloat", "8000000000000000", "0000000000000000")]
    [InlineData("binaryFloat", "7ff8000000000042", "7ff8000000000043")]
    [InlineData("decimal", "1.2300", "1.23")]
    [InlineData("integer", "-0", "0")]
    public void FullPayloadBindingKeepsBinary64BitsAndNumericTextInsteadOfNormalizingThem(string flavor, string original, string other)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        string Report(string value) => ProjectionJson(key, "[" + Parameter(
            "{\"kind\":\"exactNumber\",\"flavor\":" + Quote(flavor) + ",\"value\":" + Quote(value) + "}", flavor) + "]");
        var expected = Backend(key, Report(original), 1);
        var substituted = Backend(key, Report(other), 1);
        Assert.Equal(expected.Observation, substituted.Observation);
        var originalBytes = Bytes(Envelope(claim, Report(original)));
        var otherBytes = Bytes(Envelope(claim, Report(other)));
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        codec.VerifyCandidate(claim, expected, Candidate(claim, originalBytes), originalBytes, new());
        Refuses(RevisionReportCodecFailure.Integrity, () => codec.VerifyCandidate(claim, expected, Candidate(claim, otherBytes), otherBytes, new()));
    }

    [Fact]
    public void EscapedNulControlsAndNonBmpLiteralTextAreBoundExactlyAsLegalUnicode()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        const string text = "left\0\t\n\r\b\f😀e\u0301";
        var report = ProjectionJson(key, "[" + Parameter("{\"kind\":\"string\",\"value\":" + Quote(text) + "}") + "]");
        var expected = Backend(key, report, 1);
        var bytes = Bytes(Envelope(claim, report));
        new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).VerifyCandidate(claim, expected, Candidate(claim, bytes), bytes, new());
        var changed = Bytes(Envelope(claim, report.Replace(Quote(text), Quote(text + "x"), StringComparison.Ordinal)));
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .VerifyCandidate(claim, expected, Candidate(claim, changed), changed, new()));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("message")]
    [InlineData("owner")]
    [InlineData("location")]
    public void EqualFalseValidationSummaryCannotAuthorizeDifferentDiagnosticFacts(string substitution)
    {
        var key = ValidationKey();
        var claim = Claim(key);
        var report = ValidationJson(key, valid: false);
        var other = substitution switch
        {
            "code" => report.Replace("\"code\":\"invalid\"", "\"code\":\"different\"", StringComparison.Ordinal),
            "message" => report.Replace("\"message\":\"mapping\"", "\"message\":\"changed\"", StringComparison.Ordinal),
            "owner" => report.Replace("\"index\":\"0\"", "\"index\":\"1\"", StringComparison.Ordinal),
            "location" => report.Replace("\"location\":null", "\"location\":\"/literal~1/01\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(substitution))
        };
        var expected = Backend(key, report, valid: false);
        var substituted = Backend(key, other, valid: false);
        Assert.Equal(expected.Observation, substituted.Observation);
        var bytes = Bytes(Envelope(claim, other));
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .VerifyCandidate(claim, expected, Candidate(claim, bytes), bytes, new()));
    }

    [Theory]
    [InlineData("cacheEntryId")]
    [InlineData("derivationId")]
    [InlineData("generation")]
    [InlineData("candidateObjectId")]
    [InlineData("operation")]
    public void EveryEncodedClaimHeaderIdentityMustMatchTheActualUncommittedClaim(string field)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var raw = Envelope(claim, ProjectionJson(key));
        var replaced = field switch
        {
            "generation" => raw.Replace("\"generation\":1", "\"generation\":2", StringComparison.Ordinal),
            "operation" => raw.Replace("\"operation\":\"projection\"", "\"operation\":\"validation\"", StringComparison.Ordinal),
            _ => raw.Replace("\"" + field + "\":\"" + (field == "cacheEntryId" ? claim.CacheEntryId :
                field == "derivationId" ? claim.DerivationId : claim.CandidateObjectId.ObjectId).ToString("D") + "\"",
                "\"" + field + "\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"", StringComparison.Ordinal)
        };
        var bytes = Bytes(replaced);
        var registry = new StoredOnlyRegistry();
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, Backend(key, ProjectionJson(key)), Candidate(claim, bytes), bytes, new()));
        Assert.Equal(0, registry.StoredLookups);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("profile")]
    [InlineData("native")]
    [InlineData("dependency")]
    [InlineData("scope")]
    public void CanonicalButDifferentFullKeyCannotBeHiddenBehindMatchingEnvelopeIds(string difference)
    {
        var key = ProjectionKey();
        var source = Source;
        var profile = Profile;
        var policy = new RevisionReportPolicy();
        if (difference == "policy") policy = new(itemLimit: 8191);
        if (difference == "profile") profile = new(Profile.BackendId, Profile.BackendRevision, "other-build", Profile.AdapterBuild);
        if (difference == "native") source = new(Source.Scope, new(Source.NativeObject.Id, Sha256Digest.Parse(new string('2', 64)), 3),
            Source.LogicalDocument, Source.DependencyIdentity, Source.SourceBackendId);
        if (difference == "dependency") source = new(Source.Scope, Source.NativeObject, Source.LogicalDocument, "other-manifest", Source.SourceBackendId);
        if (difference == "scope")
        {
            var scope = new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            source = new(scope, new(PrivateObjectId.Restore(scope, Source.NativeObject.Id.ObjectId, PrivateObjectClass.NativeArtifact), Source.NativeObject.Digest, 3),
                Source.LogicalDocument, Source.DependencyIdentity, Source.SourceBackendId);
        }
        var other = new ProjectionCacheKey(source, profile, key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion, policy);
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)).Replace(KeyJson(key), KeyJson(other), StringComparison.Ordinal));
        var registry = new StoredOnlyRegistry();
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, Backend(key, ProjectionJson(key)), Candidate(claim, bytes), bytes, new()));
        Assert.Equal(0, registry.StoredLookups);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("class")]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("empty")]
    public void CandidateReferenceRequiresExactObjectClassScopeDigestAndNonemptyCount(string corruption)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var id = claim.CandidateObjectId;
        var digest = Hash(bytes);
        long count = bytes.LongLength;
        if (corruption == "object") id = PrivateObjectId.Create(key.Scope, PrivateObjectClass.DerivedArtifact);
        if (corruption == "class") id = PrivateObjectId.Restore(key.Scope, id.ObjectId, PrivateObjectClass.NativeArtifact);
        if (corruption == "scope") id = PrivateObjectId.Restore(new(key.Scope.OrganizationId, key.Scope.ProjectId, key.Scope.MappingId,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), id.ObjectId, PrivateObjectClass.DerivedArtifact);
        if (corruption == "digest") digest = Sha256Digest.Parse(new string('0', 64));
        if (corruption == "short") count--;
        if (corruption == "long") count++;
        if (corruption == "empty") count = 0;
        var candidate = new EncodedRevisionReport(new(id, digest, count), bytes, default);
        var registry = new StoredOnlyRegistry();
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, Backend(key, ProjectionJson(key)), candidate, bytes, new()));
        Assert.Equal(0, registry.StoredLookups);
    }

    [Fact]
    public void MatchingReadbackDigestCannotHideDifferentBytesInsideTheUntrustedEncodedWrapper()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key, "[" + Parameter("{\"kind\":\"string\",\"value\":\"alpha\"}") + "]");
        var bytes = Bytes(Envelope(claim, report));
        var other = Bytes(Envelope(claim, report.Replace("\"alpha\"", "\"omega\"", StringComparison.Ordinal)));
        Assert.Equal(bytes.Length, other.Length);
        var wrapper = new EncodedRevisionReport(new(claim.CandidateObjectId, Hash(bytes), bytes.LongLength), other, default);
        var registry = new StoredOnlyRegistry();
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, Backend(key, report, 1), wrapper, bytes, new()));
        Assert.Equal(0, registry.StoredLookups);
    }

    [Theory]
    [InlineData("missingPhysicalSchema")]
    [InlineData("missingRequiredNull")]
    [InlineData("unknown")]
    [InlineData("escapedDuplicate")]
    [InlineData("unpairedSurrogate")]
    [InlineData("utf8")]
    [InlineData("bom")]
    public void MatchingWrapperAndReferenceDoNotBypassStrictRawSchemaOrUnicode(string corruption)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key, "[" + Parameter("{\"kind\":\"string\",\"value\":\"alpha\"}") + "]");
        var raw = Envelope(claim, report);
        var damaged = corruption switch
        {
            "missingPhysicalSchema" => raw.Replace(",\"schemaVersion\":\"weft.mapping-projection.v1\"", "", StringComparison.Ordinal),
            "missingRequiredNull" => raw.Replace(",\"nativeName\":null", "", StringComparison.Ordinal),
            "unknown" => raw.Replace("\"report\":{", "\"report\":{\"unknown\":true,", StringComparison.Ordinal),
            "escapedDuplicate" => raw.Replace("\"report\":{", "\"report\":{\"sta\\u0074us\":\"succeeded\",", StringComparison.Ordinal),
            "unpairedSurrogate" => raw.Replace("\"alpha\"", "\"\\ud800\"", StringComparison.Ordinal),
            _ => raw
        };
        var bytes = Bytes(damaged);
        if (corruption == "utf8")
        {
            var index = bytes.AsSpan().IndexOf("alpha"u8);
            Assert.True(index >= 0);
            bytes[index] = 0xff;
        }
        if (corruption == "bom") bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray();
        var registry = new StoredOnlyRegistry();
        Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, Backend(key, report, 1), Candidate(claim, bytes), bytes, new()));
        Assert.Equal(0, registry.StoredLookups);
    }

    [Fact]
    public void FailureBackendAndUsableDifferentKeyCannotSupplyTheExpectedPayload()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var other = ProjectionKey(new(itemLimit: 1));
        foreach (var expected in new[] { RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.BackendFailure), Backend(other, ProjectionJson(other)) })
            Refuses(RevisionReportCodecFailure.Integrity, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
                .VerifyCandidate(claim, expected, Candidate(claim, bytes), bytes, new()));
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("header")]
    [InlineData("response")]
    [InlineData("values")]
    [InlineData("depth")]
    [InlineData("text")]
    public void EffectiveReadCapsRefuseOtherwiseMatchingActualCandidateBeforeCompletion(string limit)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key);
        var bytes = Bytes(Envelope(claim, report));
        var caps = limit switch
        {
            "stored" => new RevisionReportPolicy(storedObjectByteLimit: bytes.Length - 1),
            "header" => new(headerByteLimit: 1),
            "response" => new(responseByteLimit: 1),
            "values" => new(responseItemLimit: 1),
            "depth" => new(jsonDepthLimit: 1),
            "text" => new(textByteLimit: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        Refuses(RevisionReportCodecFailure.ResourceLimit, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes, caps));
    }

    [Fact]
    public void ExactStoredHeaderAndContainerDepthBoundariesAreAcceptedWithoutRequiringNativeBytesAgain()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key);
        var bytes = Bytes(Envelope(claim, report));
        var header = bytes.Length - Encoding.UTF8.GetByteCount(report);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        codec.VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes,
            new(nativeByteLimit: 1, storedObjectByteLimit: bytes.Length, headerByteLimit: header, jsonDepthLimit: 4));
        Refuses(RevisionReportCodecFailure.ResourceLimit, () => codec.VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes,
            new(headerByteLimit: header - 1)));
        Refuses(RevisionReportCodecFailure.ResourceLimit, () => codec.VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes,
            new(jsonDepthLimit: 3)));
    }

    [Fact]
    public void ActualEncodedByteLimitIsCheckedEvenWhenItsReferenceLiesAboutItsSmallLength()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var tooLarge = new byte[bytes.Length + 1];
        bytes.CopyTo(tooLarge, 0);
        var candidate = new EncodedRevisionReport(new(claim.CandidateObjectId, Hash(bytes), bytes.LongLength), tooLarge, default);
        Refuses(RevisionReportCodecFailure.ResourceLimit, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .VerifyCandidate(claim, Backend(key, ProjectionJson(key)), candidate, bytes, new(storedObjectByteLimit: bytes.Length)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureStoredProviderRefusalsRemainSafeCategoriesWithoutCurrentBackendResolution(bool limit)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key);
        var bytes = Bytes(Envelope(claim, report));
        var registry = new StoredOnlyRegistry
        {
            OnValidate = () => throw new RevisionReportValidationException(
            limit ? RevisionReportValidationFailure.ResourceLimit : RevisionReportValidationFailure.Integrity)
        };
        Refuses(limit ? RevisionReportCodecFailure.ResourceLimit : RevisionReportCodecFailure.Integrity, () =>
            new RevisionReportEnvelopeCodec(registry).VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes, new()));
        Assert.Equal(1, registry.StoredLookups);
        Assert.Equal(1, registry.Validations);
        Refuses(RevisionReportCodecFailure.UnsupportedFormat, () => new RevisionReportEnvelopeCodec(new StoredOnlyRegistry(false))
            .VerifyCandidate(claim, Backend(key, report), Candidate(claim, bytes), bytes, new()));
    }

    [Fact]
    public void PreCanceledAndCancellationDuringPureProviderProofNeverReturnSuccessfulProof()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key);
        var expected = Backend(key, report);
        var bytes = Bytes(Envelope(claim, report));
        var candidate = Candidate(claim, bytes);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var registry = new StoredOnlyRegistry();
        Assert.Throws<OperationCanceledException>(() => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, expected, candidate, bytes, new(), canceled.Token));
        Assert.Equal(0, registry.StoredLookups);
        using var during = new CancellationTokenSource();
        registry.OnValidate = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => new RevisionReportEnvelopeCodec(registry)
            .VerifyCandidate(claim, expected, candidate, bytes, new(), during.Token));
        Assert.Equal(1, registry.Validations);
        new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).VerifyCandidate(claim, expected, candidate, bytes, new());
    }

    private static EncodedRevisionReport Candidate(RevisionReportClaim claim, byte[] bytes) =>
        new(new(claim.CandidateObjectId, Hash(bytes), bytes.LongLength), bytes, default);

    private static Sha256Digest Hash(byte[] bytes) => Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static void Refuses(RevisionReportCodecFailure category, Action action)
    {
        var exception = Assert.Throws<RevisionReportCodecException>(action);
        Assert.Equal(category, exception.Failure);
        Assert.Equal("Revision report codec refused the value.", exception.Message);
        Assert.Null(exception.InnerException);
    }
}
