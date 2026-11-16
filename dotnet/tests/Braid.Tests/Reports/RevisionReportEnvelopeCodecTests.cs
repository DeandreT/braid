using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.RevisionReportCodecTestSupport;

namespace Braid.Tests;

public sealed class RevisionReportEnvelopeCodecTests
{
    [Fact]
    public void IndependentlySpelledCanonicalKeyAndPolicyBytesAgreeWithDomainIdentity()
    {
        var key = ProjectionKey();
        Assert.Equal(Bytes(PolicyJson(key.Policy)), key.Policy.CopyCanonicalUtf8());
        Assert.Equal(Bytes(KeyJson(key)), key.CopyCanonicalUtf8());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes(KeyJson(key)))), key.Fingerprint.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManuallyAuthoredEnvelopeDecodesUsingOnlyPureStoredFormatLookup(bool validation)
    {
        RevisionReportKey key = validation ? ValidationKey() : ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, validation ? ValidationJson((ValidationCacheKey)key) : ProjectionJson((ProjectionCacheKey)key)));
        var metadata = Metadata(claim, bytes);
        var registry = new StoredOnlyRegistry();
        var actual = new RevisionReportEnvelopeCodec(registry).Decode(metadata, bytes, new());
        Assert.Equal(metadata, actual.Metadata);
        Assert.Equal(key, actual.Metadata.Key);
        Assert.Equal(1, registry.StoredLookups);
        Assert.Equal(1, registry.Validations);
        if (validation) Assert.True(Assert.IsType<VerifiedValidationReport>(actual).Report.IsValid);
        else Assert.Empty(Assert.IsType<VerifiedProjectionReport>(actual).Report.Document!.Fields);
    }

    [Fact]
    public void ActualFalseValidityAndIndependentTruncationRemainUsableAndExactlyObserved()
    {
        var key = ValidationKey();
        var claim = Claim(key);
        var report = ValidationJson(key, valid: false).Replace("\"truncated\":false", "\"truncated\":true", StringComparison.Ordinal);
        var observation = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
            new(key.Scope, key.NativeDigest, key.Profile, true), null, new(false, 1, 1, true, 1));
        var bytes = Bytes(Envelope(claim, report));
        var actual = Assert.IsType<VerifiedValidationReport>(new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .Decode(Metadata(claim, bytes, observation), bytes, new()));
        Assert.False(actual.Report.IsValid);
        Assert.True(actual.Report.Truncated);
        Assert.Equal(1, actual.Report.TotalIssueCount);
        Assert.Equal(1, actual.Report.EmittedIssueCount);
        Assert.Null(actual.Report.Diagnostics[0].ProjectionLocator);
    }

    [Fact]
    public void EncodeOwnsReturnedBytesAndBindsExactReferenceHeaderAndReport()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var encoded = codec.Encode(claim, Backend(key, ProjectionJson(key)));
        var expected = encoded.CopyBytes();
        var returned = encoded.CopyBytes();
        returned.AsSpan().Fill(0);
        Assert.Equal(expected, encoded.CopyBytes());
        Assert.Equal(claim.CandidateObjectId, encoded.Reference.Id);
        Assert.Equal(expected.LongLength, encoded.Reference.ByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), encoded.Reference.Digest.Value);
        using var raw = JsonDocument.Parse(expected);
        Assert.Equal("braid.revision-report-envelope.v1", raw.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(claim.CacheEntryId.ToString("D"), raw.RootElement.GetProperty("cacheEntryId").GetString());
        Assert.Equal(claim.DerivationId.ToString("D"), raw.RootElement.GetProperty("derivationId").GetString());
        Assert.Equal(KeyJson(key), raw.RootElement.GetProperty("key").GetRawText());
        var verified = codec.Decode(Metadata(claim, expected), expected, new());
        expected.AsSpan().Fill(0);
        Assert.Equal("succeeded", JsonSerializer.SerializeToElement(Assert.IsType<VerifiedProjectionReport>(verified).Report,
            Braid.Contracts.Projections.ProjectionJson.CreateOptions()).GetProperty("status").GetString());
        Assert.Equal(key, verified.Metadata.Key);
    }

    [Fact]
    public void ReturnedBytesCannotBeSubstitutedAcrossCandidateDerivationCacheOrGeneration()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var metadata = Metadata(claim, bytes);
        var alternatives = new[]
        {
            new RevisionReportResult(Guid.NewGuid(), metadata.CacheEntryId, key, metadata.Generation, metadata.OriginalActor,
                metadata.StartedAtUtc, metadata.FinishedAtUtc, metadata.DerivedObject, metadata.Observation),
            new RevisionReportResult(metadata.DerivationId, Guid.NewGuid(), key, metadata.Generation, metadata.OriginalActor,
                metadata.StartedAtUtc, metadata.FinishedAtUtc, metadata.DerivedObject, metadata.Observation),
            new RevisionReportResult(metadata.DerivationId, metadata.CacheEntryId, key, 2, metadata.OriginalActor,
                metadata.StartedAtUtc, metadata.FinishedAtUtc, metadata.DerivedObject, metadata.Observation),
            Metadata(claim, bytes, referenceId: PrivateObjectId.Create(key.Scope, PrivateObjectClass.DerivedArtifact))
        };
        foreach (var other in alternatives)
            Assert.Equal(RevisionReportCodecFailure.Integrity,
                Assert.Throws<RevisionReportCodecException>(() => codec.Decode(other, bytes, new())).Failure);
    }

    [Fact]
    public void MissingStoredFormatAndSafeValidatorRefusalsStayCategorical()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var metadata = Metadata(claim, bytes);
        Assert.Equal(RevisionReportCodecFailure.UnsupportedFormat, Assert.Throws<RevisionReportCodecException>(() =>
            new RevisionReportEnvelopeCodec(new StoredOnlyRegistry(false)).Decode(metadata, bytes, new())).Failure);
        var registry = new StoredOnlyRegistry { OnValidate = () => throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit) };
        var failure = Assert.Throws<RevisionReportCodecException>(() => new RevisionReportEnvelopeCodec(registry).Decode(metadata, bytes, new()));
        Assert.Equal(RevisionReportCodecFailure.ResourceLimit, failure.Failure);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(key.Source.LogicalDocument, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(metadata.DerivedObject.Id.ObjectId.ToString("D"), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredReferenceLengthAndDigestAreDefensivelyVerifiedBeforeFormatLookup()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var metadata = Metadata(claim, bytes);
        var registry = new StoredOnlyRegistry();
        var codec = new RevisionReportEnvelopeCodec(registry);
        var altered = bytes.ToArray();
        altered[^2] ^= 1;
        Assert.Equal(RevisionReportCodecFailure.Integrity,
            Assert.Throws<RevisionReportCodecException>(() => codec.Decode(metadata, altered, new())).Failure);
        Assert.Equal(RevisionReportCodecFailure.Integrity,
            Assert.Throws<RevisionReportCodecException>(() => codec.Decode(metadata, bytes.AsMemory(0, bytes.Length - 1), new())).Failure);
        Assert.Equal(0, registry.StoredLookups);
    }

    [Fact]
    public void CancellationBeforeCodecOrDuringPureFormatValidationNeverReturnsAcceptedBytes()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = Backend(key, ProjectionJson(key));
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var metadata = Metadata(claim, bytes);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        Assert.Throws<OperationCanceledException>(() => codec.Encode(claim, report, canceled.Token));
        Assert.Throws<OperationCanceledException>(() => codec.Decode(metadata, bytes, new(), canceled.Token));
        using var during = new CancellationTokenSource();
        var registry = new StoredOnlyRegistry { OnValidate = during.Cancel };
        Assert.Throws<OperationCanceledException>(() => new RevisionReportEnvelopeCodec(registry).Decode(metadata, bytes, new(), during.Token));
        Assert.Equal(1, registry.Validations);
    }

    [Fact]
    public void EffectiveHeaderStoredResponseItemAndDepthReadCapsRefuseTheSameOtherwiseValidObject()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var bytes = Bytes(Envelope(claim, ProjectionJson(key)));
        var metadata = Metadata(claim, bytes);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        foreach (var caps in new RevisionReportPolicy[]
        {
            new RevisionReportPolicy(headerByteLimit: 1), new(storedObjectByteLimit: bytes.Length - 1),
            new(responseByteLimit: 1), new(responseItemLimit: 1), new(jsonDepthLimit: 1)
        })
            Assert.Equal(RevisionReportCodecFailure.ResourceLimit,
                Assert.Throws<RevisionReportCodecException>(() => codec.Decode(metadata, bytes, caps)).Failure);
    }

    [Fact]
    public void EncodeRejectsLimitPlusOneAndWrongFullKeyWithoutPublishingAnyBytes()
    {
        var tinyKey = ProjectionKey(new(storedObjectByteLimit: 1));
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        Assert.Equal(RevisionReportCodecFailure.ResourceLimit, Assert.Throws<RevisionReportCodecException>(() =>
            codec.Encode(Claim(tinyKey), Backend(tinyKey, ProjectionJson(tinyKey)))).Failure);
        var key = ProjectionKey();
        var other = ProjectionKey(new(itemLimit: 1));
        Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
            codec.Encode(Claim(key), Backend(other, ProjectionJson(other)))).Failure);
        Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
            codec.Encode(Claim(key), RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.BackendFailure))).Failure);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    public void ExactStandaloneReportContainerDepthAcceptsScalarLeavesAtTheDeepestContainer(bool validation, int depth)
    {
        RevisionReportKey key = validation ? ValidationKey() : ProjectionKey();
        var report = validation ? ValidationJson((ValidationCacheKey)key) : ProjectionJson((ProjectionCacheKey)key);
        var bytes = Bytes(Envelope(Claim(key), report));
        var metadata = Metadata(Claim(key), bytes);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        Assert.Equal(metadata, codec.Decode(metadata, bytes, new(jsonDepthLimit: depth)).Metadata);
        Assert.Equal(RevisionReportCodecFailure.ResourceLimit, Assert.Throws<RevisionReportCodecException>(() =>
            codec.Decode(metadata, bytes, new(jsonDepthLimit: depth - 1))).Failure);
    }

    [Fact]
    public void ExactHeaderStoredResponseAndReportValueBudgetsAcceptTheirBoundaryAndRefuseOneLess()
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key);
        var raw = Envelope(claim, report);
        var headerLength = Encoding.UTF8.GetByteCount(raw) - Encoding.UTF8.GetByteCount(report);
        var padded = Bytes(new string(' ', RevisionReportPolicy.MaximumHeaderByteLimit - headerLength) + raw);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var paddedMetadata = Metadata(claim, padded);
        Assert.Equal(paddedMetadata, codec.Decode(paddedMetadata, padded, new()).Metadata);
        var extra = Bytes(" " + Encoding.UTF8.GetString(padded));
        Assert.Equal(RevisionReportCodecFailure.ResourceLimit, Assert.Throws<RevisionReportCodecException>(() =>
            codec.Decode(Metadata(claim, extra), extra, new())).Failure);
        var bytes = Bytes(raw);
        var metadata = Metadata(claim, bytes);
        Assert.Equal(metadata, codec.Decode(metadata, bytes, new(storedObjectByteLimit: bytes.Length)).Metadata);
        using var reportDocument = JsonDocument.Parse(report);
        var items = ValueCount(reportDocument.RootElement);
        var reportLength = Encoding.UTF8.GetByteCount(report);
        Assert.Equal(metadata, codec.Decode(metadata, bytes,
            new(responseByteLimit: reportLength, responseItemLimit: items)).Metadata);
        foreach (var caps in new RevisionReportPolicy[]
        {
            new(responseByteLimit: reportLength - 1), new(responseItemLimit: items - 1)
        })
            Assert.Equal(RevisionReportCodecFailure.ResourceLimit, Assert.Throws<RevisionReportCodecException>(() =>
                codec.Decode(metadata, bytes, caps)).Failure);
    }

    [Fact]
    public void AHistoricalDerivedOnlyReadDoesNotAcquireCurrentNativeAdmissionLimits()
    {
        var key = ProjectionKey();
        var bytes = Bytes(Envelope(Claim(key), ProjectionJson(key)));
        var metadata = Metadata(Claim(key), bytes);
        Assert.Equal(metadata, new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .Decode(metadata, bytes, new(nativeByteLimit: 1)).Metadata);
        Assert.Equal(3, metadata.Key.Source.NativeObject.ByteCount);
    }

    private static int ValueCount(JsonElement value) => 1 + (value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Sum(property => ValueCount(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Sum(ValueCount),
        _ => 0
    });
}
