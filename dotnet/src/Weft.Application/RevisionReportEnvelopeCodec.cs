using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Examples;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Application;

public enum RevisionReportCodecFailure { UnsupportedFormat = 1, Integrity = 2, ResourceLimit = 3 }

/// <summary>A safe codec category; report bytes and private identities are never included in the message.</summary>
public sealed class RevisionReportCodecException : Exception
{
    public RevisionReportCodecException(RevisionReportCodecFailure failure) : base("Revision report codec refused the value.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }
    public RevisionReportCodecFailure Failure { get; }
}

internal sealed class EncodedRevisionReport
{
    private readonly byte[] bytes;
    internal EncodedRevisionReport(PrivateObjectReference reference, ReadOnlyMemory<byte> source, CancellationToken token)
    {
        Reference = reference;
        bytes = Copy(source, token);
    }
    internal PrivateObjectReference Reference { get; }
    internal byte[] CopyBytes(CancellationToken cancellationToken = default) => Copy(bytes, cancellationToken);
    private static byte[] Copy(ReadOnlyMemory<byte> source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > RevisionReportPolicy.MaximumStoredObjectByteLimit)
            throw new RevisionReportCodecException(RevisionReportCodecFailure.ResourceLimit);
        var owned = new byte[source.Length];
        for (var offset = 0; offset < owned.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(4096, owned.Length - offset);
            source.Span.Slice(offset, count).CopyTo(owned.AsSpan(offset, count));
        }
        token.ThrowIfCancellationRequested();
        return owned;
    }
}

internal sealed partial class RevisionReportEnvelopeCodec
{
    internal const string EnvelopeVersion = "weft.revision-report-envelope.v1";
    internal const string CodecVersion = "weft.revision-report-codec.v1";
    private readonly IRevisionReportProfileRegistry registry;

    internal RevisionReportEnvelopeCodec(IRevisionReportProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    internal EncodedRevisionReport Encode(RevisionReportClaim claim, RevisionReportBackendResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsUsable || result.Key != claim.Key) throw Integrity();
        try
        {
            var validator = StoredValidator(claim.Key);
            if (result.ProjectionReport is { } projection)
            {
                if (RevisionReportProjectionValidation.Validate(claim.Key, projection, claim.Key.Policy, cancellationToken) != result.Observation)
                    throw Integrity();
                validator.Validate(claim.Key, projection, cancellationToken);
            }
            else if (result.ValidationReport is { } validation)
            {
                if (RevisionReportValidationValidation.Validate(claim.Key, validation, claim.Key.Policy, cancellationToken) != result.Observation)
                    throw Integrity();
                validator.Validate(claim.Key, validation, cancellationToken);
            }
            else throw Integrity();
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new RevisionReportResponseSerializer.BoundedWriteStream(claim.Key.Policy.StoredObjectByteLimit, cancellationToken);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = RevisionReportPolicy.MaximumJsonDepthLimit + 1 }))
            {
                writer.WriteStartObject();
                writer.WriteString("schemaVersion", EnvelopeVersion);
                writer.WriteString("codecVersion", CodecVersion);
                writer.WriteString("operation", OperationText(claim.Key.Operation));
                writer.WritePropertyName("key");
                writer.WriteRawValue(claim.Key.CopyCanonicalUtf8());
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteString("cacheEntryId", claim.CacheEntryId.ToString("D"));
                writer.WriteString("derivationId", claim.DerivationId.ToString("D"));
                writer.WriteNumber("generation", claim.Generation);
                writer.WriteString("candidateObjectId", claim.CandidateObjectId.ObjectId.ToString("D"));
                writer.WritePropertyName("report");
                if (result.ProjectionReport is { } report) JsonSerializer.Serialize(writer, report, PrivateOptions());
                else JsonSerializer.Serialize(writer, result.ValidationReport!, PrivateOptions());
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteEndObject();
                writer.Flush();
            }
            var bytes = stream.CopyBytes();
            using var raw = ValidateRaw(bytes, claim.Key.Policy, claim.Key.Operation, cancellationToken);
            var reference = new PrivateObjectReference(claim.CandidateObjectId, Digest(bytes, cancellationToken), bytes.LongLength);
            return new(reference, bytes, cancellationToken);
        }
        catch (RevisionReportValidationException error) { throw FromValidation(error); }
        catch (JsonException) { throw Integrity(); }
        catch (ArgumentException) { throw Integrity(); }
        catch (NotSupportedException) { throw Integrity(); }
    }

    internal VerifiedRevisionReport Decode(RevisionReportResult metadata, ReadOnlyMemory<byte> bytes,
        RevisionReportPolicy activeReadCaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(activeReadCaps);
        cancellationToken.ThrowIfCancellationRequested();
        var caps = ReadCaps(metadata.Key.Policy, activeReadCaps);
        var reference = metadata.DerivedObject;
        if (reference.Id.Scope != metadata.Key.Scope || reference.Id.Class != PrivateObjectClass.DerivedArtifact ||
            reference.ByteCount < 1) throw Integrity();
        if (reference.ByteCount > caps.StoredObjectByteLimit || bytes.Length > caps.StoredObjectByteLimit) throw ResourceLimit();
        if (bytes.Length != reference.ByteCount) throw Integrity();
        var owned = CopyBytes(bytes, caps.StoredObjectByteLimit, cancellationToken);
        if (Digest(owned, cancellationToken) != reference.Digest) throw Integrity();
        try
        {
            using var document = ValidateRaw(owned, caps, metadata.Key.Operation, cancellationToken);
            var root = document.RootElement;
            var key = ReadKey(root.GetProperty("key"), cancellationToken);
            if (key != metadata.Key || Text(root, "operation") != OperationText(key.Operation) ||
                Text(root, "codecVersion") != key.CodecVersion || Id(root, "cacheEntryId") != metadata.CacheEntryId ||
                Id(root, "derivationId") != metadata.DerivationId ||
                PositiveLong(root.GetProperty("generation"), cancellationToken) != metadata.Generation ||
                Id(root, "candidateObjectId") != reference.Id.ObjectId) throw Integrity();
            var validator = StoredValidator(key);
            var rawReport = root.GetProperty("report");
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Operation == RevisionReportOperation.Projection)
            {
                var report = rawReport.Deserialize<ProjectionReportV1>(PrivateOptions()) ?? throw Integrity();
                var verified = VerifiedProjectionReport.Create(metadata, report, activeReadCaps, cancellationToken);
                validator.Validate(key, verified.Report, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return verified;
            }
            else
            {
                var report = rawReport.Deserialize<ValidationReportV1>(PrivateOptions()) ?? throw Integrity();
                var verified = VerifiedValidationReport.Create(metadata, report, activeReadCaps, cancellationToken: cancellationToken);
                validator.Validate(key, verified.Report, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return verified;
            }
        }
        catch (RevisionReportValidationException error) { throw FromValidation(error); }
        catch (JsonException) { throw Integrity(); }
        catch (ArgumentException) { throw Integrity(); }
        catch (FormatException) { throw Integrity(); }
        catch (NotSupportedException) { throw Integrity(); }
    }

    private IRevisionReportFormatValidator StoredValidator(RevisionReportKey key)
    {
        if (key.CodecVersion != CodecVersion || key.SchemaVersion !=
            (key.Operation == RevisionReportOperation.Projection ? "weft.mapping-projection.v1" : "weft.mapping-validation.v1"))
            throw UnsupportedFormat();
        return registry.FindStored(new RevisionReportFormat(key.BackendId, key.Operation, key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion))
            ?? throw UnsupportedFormat();
    }

    private static JsonSerializerOptions PrivateOptions()
    {
        var options = RevisionReportResponseSerializer.CreateOptions(RevisionReportPolicy.MaximumJsonDepthLimit + 1);
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowOutOfOrderMetadataProperties = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return options;
    }

    private static JsonDocument ValidateRaw(byte[] bytes, RevisionReportPolicy caps, RevisionReportOperation operation,
        CancellationToken token)
    {
        Preflight(bytes, caps, token);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = RevisionReportPolicy.MaximumJsonDepthLimit + 1 });
        try
        {
            var root = document.RootElement;
            Object(root, token, ("schemaVersion", String), ("codecVersion", String), ("operation", OneOf("projection", "validation")),
                ("key", (value, cancellation) => _ = ReadKey(value, cancellation)), ("cacheEntryId", CanonicalId),
                ("derivationId", CanonicalId), ("generation", PositiveInteger), ("candidateObjectId", CanonicalId),
                ("report", operation == RevisionReportOperation.Projection ? ProjectionReport : ValidationReport));
            if (Text(root, "schemaVersion") != EnvelopeVersion || Text(root, "codecVersion") != CodecVersion) throw UnsupportedFormat();
            token.ThrowIfCancellationRequested();
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static string OperationText(RevisionReportOperation operation) => operation == RevisionReportOperation.Projection ? "projection" : "validation";
    private static RevisionReportCodecException Integrity() => new(RevisionReportCodecFailure.Integrity);
    private static RevisionReportCodecException ResourceLimit() => new(RevisionReportCodecFailure.ResourceLimit);
    private static RevisionReportCodecException UnsupportedFormat() => new(RevisionReportCodecFailure.UnsupportedFormat);
    private static RevisionReportCodecException FromValidation(RevisionReportValidationException error) =>
        new(error.Failure == RevisionReportValidationFailure.ResourceLimit ? RevisionReportCodecFailure.ResourceLimit : RevisionReportCodecFailure.Integrity);
}
