using System.Text.Json;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    /// <summary>Proves a readback against the actual claim and whole owned backend payload before completion.</summary>
    internal void VerifyCandidate(RevisionReportClaim claim, RevisionReportBackendResult expectedBackendResult,
        EncodedRevisionReport encodedCandidate, ReadOnlyMemory<byte> readback, RevisionReportPolicy activeReadCaps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(expectedBackendResult);
        ArgumentNullException.ThrowIfNull(encodedCandidate);
        ArgumentNullException.ThrowIfNull(activeReadCaps);
        cancellationToken.ThrowIfCancellationRequested();
        if (!expectedBackendResult.IsUsable || expectedBackendResult.Key != claim.Key) throw Integrity();

        var caps = ReadCaps(claim.Key.Policy, activeReadCaps);
        var reference = encodedCandidate.Reference;
        if (reference is null || reference.Id != claim.CandidateObjectId ||
            reference.Id.Scope != claim.Key.Scope || reference.Id.Class != PrivateObjectClass.DerivedArtifact ||
            reference.ByteCount < 1) throw Integrity();
        if (reference.ByteCount > caps.StoredObjectByteLimit || readback.Length > caps.StoredObjectByteLimit)
            throw ResourceLimit();
        if (readback.Length != reference.ByteCount) throw Integrity();

        var ownedReadback = CopyBytes(readback, caps.StoredObjectByteLimit, cancellationToken);
        // The wrapper owns at most the hard ceiling; its declared reference is still untrusted.
        var ownedCandidate = encodedCandidate.CopyBytes(cancellationToken);
        if (ownedCandidate.Length > caps.StoredObjectByteLimit) throw ResourceLimit();
        if (ownedCandidate.LongLength != reference.ByteCount ||
            !CandidateBytesEqual(ownedCandidate, ownedReadback, cancellationToken) ||
            Digest(ownedReadback, cancellationToken) != reference.Digest) throw Integrity();

        try
        {
            using var document = ValidateRaw(ownedReadback, caps, claim.Key.Operation, cancellationToken);
            var root = document.RootElement;
            var key = ReadKey(root.GetProperty("key"), cancellationToken);
            if (key != claim.Key || Text(root, "operation") != OperationText(key.Operation) ||
                Text(root, "codecVersion") != key.CodecVersion || Id(root, "cacheEntryId") != claim.CacheEntryId ||
                Id(root, "derivationId") != claim.DerivationId ||
                PositiveLong(root.GetProperty("generation"), cancellationToken) != claim.Generation ||
                Id(root, "candidateObjectId") != claim.CandidateObjectId.ObjectId) throw Integrity();

            var validator = StoredValidator(key);
            var rawReport = root.GetProperty("report");
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Operation == RevisionReportOperation.Projection && expectedBackendResult.ProjectionReport is { } expectedProjection)
            {
                var decoded = rawReport.Deserialize<ProjectionReportV1>(PrivateOptions()) ?? throw Integrity();
                var actual = RevisionReportSnapshots.CopyProjection(decoded, caps, cancellationToken);
                var expected = RevisionReportSnapshots.CopyProjection(expectedProjection, caps, cancellationToken);
                if (RevisionReportProjectionValidation.Validate(key, actual, caps, cancellationToken) != expectedBackendResult.Observation ||
                    RevisionReportProjectionValidation.Validate(key, expected, caps, cancellationToken) != expectedBackendResult.Observation)
                    throw Integrity();
                validator.Validate(key, actual, cancellationToken);
                validator.Validate(key, expected, cancellationToken);
                MatchCandidatePayload(actual, expected, caps, cancellationToken);
            }
            else if (key.Operation == RevisionReportOperation.Validation && expectedBackendResult.ValidationReport is { } expectedValidation)
            {
                var decoded = rawReport.Deserialize<ValidationReportV1>(PrivateOptions()) ?? throw Integrity();
                var actual = RevisionReportSnapshots.CopyValidation(decoded, caps, cancellationToken);
                var expected = RevisionReportSnapshots.CopyValidation(expectedValidation, caps, cancellationToken);
                if (RevisionReportValidationValidation.Validate(key, actual, caps, cancellationToken) != expectedBackendResult.Observation ||
                    RevisionReportValidationValidation.Validate(key, expected, caps, cancellationToken) != expectedBackendResult.Observation)
                    throw Integrity();
                validator.Validate(key, actual, cancellationToken);
                validator.Validate(key, expected, cancellationToken);
                MatchCandidatePayload(actual, expected, caps, cancellationToken);
            }
            else throw Integrity();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (RevisionReportValidationException error) { throw FromValidation(error); }
        catch (JsonException) { throw Integrity(); }
        catch (ArgumentException) { throw Integrity(); }
        catch (FormatException) { throw Integrity(); }
        catch (NotSupportedException) { throw Integrity(); }
    }

    private static void MatchCandidatePayload<T>(T actual, T expected, RevisionReportPolicy caps, CancellationToken token)
    {
        // Contract converters preserve literal text and binary64 bits. Comparing the entire owned
        // graph also covers facts intentionally absent from the compact metadata observation.
        var actualBytes = CandidatePayloadBytes(actual, caps.ResponseByteLimit, token);
        var expectedBytes = CandidatePayloadBytes(expected, caps.ResponseByteLimit, token);
        if (!CandidateBytesEqual(actualBytes, expectedBytes, token)) throw Integrity();
    }

    private static byte[] CandidatePayloadBytes<T>(T report, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new RevisionReportResponseSerializer.BoundedWriteStream(maximumBytes, token);
        JsonSerializer.Serialize(stream, report, PrivateOptions());
        token.ThrowIfCancellationRequested();
        return stream.CopyBytes();
    }

    private static bool CandidateBytesEqual(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (first.Length != second.Length) return false;
        for (var offset = 0; offset < first.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(4096, first.Length - offset);
            if (!first.Slice(offset, count).SequenceEqual(second.Slice(offset, count))) return false;
        }
        token.ThrowIfCancellationRequested();
        return true;
    }
}
