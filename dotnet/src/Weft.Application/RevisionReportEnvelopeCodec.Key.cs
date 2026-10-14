using System.Globalization;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    private static RevisionReportKey ReadKey(JsonElement value, CancellationToken token)
    {
        Object(value, token, ("schemaVersion", String), ("operation", OneOf("projection", "validation")),
            ("scope", Scope), ("nativeObject", NativeObject), ("logicalDocument", String), ("dependencyIdentity", String),
            ("sourceBackendId", String), ("profile", Profile), ("reportSchemaVersion", String),
            ("algorithmVersion", String), ("codecVersion", String), ("policy", Policy), ("policyFingerprint", CanonicalDigest));
        if (Text(value, "schemaVersion") != "weft.revision-report-key.v1") throw UnsupportedFormat();
        var scopeValue = value.GetProperty("scope");
        var scope = new RevisionScope(Id(scopeValue, "organizationId"), Id(scopeValue, "projectId"),
            Id(scopeValue, "mappingId"), Id(scopeValue, "revisionId"));
        var native = value.GetProperty("nativeObject");
        var reference = new PrivateObjectReference(PrivateObjectId.Restore(scope, Id(native, "objectId"), PrivateObjectClass.NativeArtifact),
            Sha256Digest.Parse(Text(native, "digest")), PositiveLong(native.GetProperty("byteCount"), token));
        var source = new RevisionReportSource(scope, reference, Text(value, "logicalDocument"), Text(value, "dependencyIdentity"),
            Text(value, "sourceBackendId"));
        var profileValue = value.GetProperty("profile");
        var profile = new RevisionReportBackendProfile(Text(profileValue, "backendId"), Text(profileValue, "backendRevision"),
            Text(profileValue, "backendBuild"), Text(profileValue, "adapterBuild"));
        var policyValue = value.GetProperty("policy");
        if (Text(policyValue, "schemaVersion") != "weft.revision-report-policy.v1") throw UnsupportedFormat();
        var policy = new RevisionReportPolicy(Int(policyValue, "nativeByteLimit"), Int(policyValue, "workerResponseByteLimit"),
            Int(policyValue, "itemLimit"), Int(policyValue, "responseItemLimit"), Int(policyValue, "jsonDepthLimit"),
            Int(policyValue, "detailDepthLimit"), Int(policyValue, "diagnosticLimit"), Int(policyValue, "validationIssueLimit"),
            Int(policyValue, "textByteLimit"), Int(policyValue, "headerByteLimit"), Int(policyValue, "storedObjectByteLimit"),
            Int(policyValue, "responseByteLimit"));
        token.ThrowIfCancellationRequested();
        if (policy.Fingerprint.Value != Text(value, "policyFingerprint")) throw Integrity();
        RevisionReportKey key = Text(value, "operation") == "projection"
            ? new ProjectionCacheKey(source, profile, Text(value, "reportSchemaVersion"), Text(value, "algorithmVersion"), Text(value, "codecVersion"), policy)
            : new ValidationCacheKey(source, profile, Text(value, "reportSchemaVersion"), Text(value, "algorithmVersion"), Text(value, "codecVersion"), policy);
        var raw = StrictUtf8.GetBytes(value.GetRawText());
        token.ThrowIfCancellationRequested();
        if (!raw.AsSpan().SequenceEqual(key.CopyCanonicalUtf8())) throw Integrity();
        token.ThrowIfCancellationRequested();
        return key;
    }

    private static void NativeObject(JsonElement value, CancellationToken token) => Object(value, token,
        ("objectId", CanonicalId), ("class", OneOf("native")), ("digest", CanonicalDigest), ("byteCount", PositiveInteger));
    private static void Profile(JsonElement value, CancellationToken token) => Object(value, token,
        ("backendId", String), ("backendRevision", String), ("backendBuild", String), ("adapterBuild", String));
    private static void Policy(JsonElement value, CancellationToken token) => Object(value, token,
        ("schemaVersion", String), ("nativeByteLimit", Integer), ("workerResponseByteLimit", Integer), ("itemLimit", Integer),
        ("responseItemLimit", Integer), ("jsonDepthLimit", Integer), ("detailDepthLimit", Integer), ("diagnosticLimit", Integer),
        ("validationIssueLimit", Integer), ("textByteLimit", Integer), ("headerByteLimit", Integer),
        ("storedObjectByteLimit", Integer), ("responseByteLimit", Integer));
    private static void PositiveInteger(JsonElement value, CancellationToken token) => _ = PositiveLong(value, token);
    private static long PositiveLong(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0 ||
            value.GetRawText() != number.ToString(CultureInfo.InvariantCulture))
            throw Integrity();
        return number;
    }
    private static string Text(JsonElement value, string member) => value.GetProperty(member).GetString() ?? throw Integrity();
    private static Guid Id(JsonElement value, string member) => Guid.ParseExact(Text(value, member), "D");
    private static int Int(JsonElement value, string member) => value.GetProperty(member).GetInt32();

    private static RevisionReportPolicy ReadCaps(RevisionReportPolicy historical, RevisionReportPolicy active) => new(
        historical.NativeByteLimit,
        Math.Min(historical.WorkerResponseByteLimit, active.WorkerResponseByteLimit),
        Math.Min(historical.ItemLimit, active.ItemLimit), Math.Min(historical.ResponseItemLimit, active.ResponseItemLimit),
        Math.Min(historical.JsonDepthLimit, active.JsonDepthLimit), Math.Min(historical.DetailDepthLimit, active.DetailDepthLimit),
        Math.Min(historical.DiagnosticLimit, active.DiagnosticLimit), Math.Min(historical.ValidationIssueLimit, active.ValidationIssueLimit),
        Math.Min(historical.TextByteLimit, active.TextByteLimit), Math.Min(historical.HeaderByteLimit, active.HeaderByteLimit),
        Math.Min(historical.StoredObjectByteLimit, active.StoredObjectByteLimit), Math.Min(historical.ResponseByteLimit, active.ResponseByteLimit));
}
