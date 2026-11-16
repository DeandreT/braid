using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Pure manually authored report bytes and synthetic metadata; no store, database or native worker.</summary>
internal static class RevisionReportCodecTestSupport
{
    internal static readonly RevisionScope Scope = new(
        Guid.Parse("12345678-1234-5678-9abc-def012345678"), Guid.Parse("23456789-2345-6789-abcd-ef0123456789"),
        Guid.Parse("3456789a-3456-789a-bcde-f0123456789a"), Guid.Parse("456789ab-4567-89ab-cdef-0123456789ab"));
    internal static readonly RevisionReportSource Source = new(Scope,
        new(PrivateObjectId.Restore(Scope, Guid.Parse("56789abc-5678-9abc-def0-123456789abc"), PrivateObjectClass.NativeArtifact),
            Sha256Digest.Parse(new string('1', 64)), 3), "a/../logical~1.json", "manifest-α", "neutral");
    internal static readonly RevisionReportBackendProfile Profile = new("neutral", "neutral-v1", "build-neutral", "adapter-neutral");
    internal static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    internal static ProjectionCacheKey ProjectionKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "braid.mapping-projection.v1", "neutral.project.v1", "braid.revision-report-codec.v1", policy ?? new());
    internal static ValidationCacheKey ValidationKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "braid.mapping-validation.v1", "neutral.validate.v1", "braid.revision-report-codec.v1", policy ?? new());
    internal static RevisionReportClaim Claim(RevisionReportKey key, long generation = 1) => new(
        Guid.Parse("6789abcd-6789-abcd-ef01-23456789abcd"), Guid.Parse("789abcde-789a-bcde-f012-3456789abcde"), key,
        new(Guid.Parse("89abcdef-89ab-cdef-0123-456789abcdef")), Guid.Parse("9abcdef0-9abc-def0-1234-56789abcdef0"),
        Guid.Parse("abcdef01-abcd-ef01-2345-6789abcdef01"), generation, Start, Start.AddSeconds(60),
        PrivateObjectId.Restore(key.Scope, Guid.Parse("bcdef012-bcde-f012-3456-789abcdef012"), PrivateObjectClass.DerivedArtifact));

    internal static string ScopeJson(RevisionScope scope) => $$"""
        {"organizationId":"{{scope.OrganizationId:D}}","projectId":"{{scope.ProjectId:D}}","mappingId":"{{scope.MappingId:D}}","revisionId":"{{scope.RevisionId:D}}"}
        """;
    internal static string PolicyJson(RevisionReportPolicy policy) => $$"""
        {"schemaVersion":"braid.revision-report-policy.v1","nativeByteLimit":{{policy.NativeByteLimit}},"workerResponseByteLimit":{{policy.WorkerResponseByteLimit}},"itemLimit":{{policy.ItemLimit}},"responseItemLimit":{{policy.ResponseItemLimit}},"jsonDepthLimit":{{policy.JsonDepthLimit}},"detailDepthLimit":{{policy.DetailDepthLimit}},"diagnosticLimit":{{policy.DiagnosticLimit}},"validationIssueLimit":{{policy.ValidationIssueLimit}},"textByteLimit":{{policy.TextByteLimit}},"headerByteLimit":{{policy.HeaderByteLimit}},"storedObjectByteLimit":{{policy.StoredObjectByteLimit}},"responseByteLimit":{{policy.ResponseByteLimit}}}
        """;
    internal static string KeyJson(RevisionReportKey key)
    {
        var policy = PolicyJson(key.Policy);
        // This oracle spells the wire order/fields independently and hashes its own literal policy bytes.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policy)));
        return $$"""
            {"schemaVersion":"braid.revision-report-key.v1","operation":"{{Operation(key)}}","scope":{{ScopeJson(key.Scope)}},"nativeObject":{"objectId":"{{key.Source.NativeObject.Id.ObjectId:D}}","class":"native","digest":"{{key.NativeDigest.Value}}","byteCount":{{key.Source.NativeObject.ByteCount}}},"logicalDocument":{{Quote(key.Source.LogicalDocument)}},"dependencyIdentity":{{Quote(key.Source.DependencyIdentity)}},"sourceBackendId":{{Quote(key.Source.SourceBackendId)}},"profile":{"backendId":{{Quote(key.BackendId)}},"backendRevision":{{Quote(key.BackendRevision)}},"backendBuild":{{Quote(key.BackendBuild)}},"adapterBuild":{{Quote(key.AdapterBuild)}}},"reportSchemaVersion":{{Quote(key.SchemaVersion)}},"algorithmVersion":{{Quote(key.AlgorithmVersion)}},"codecVersion":{{Quote(key.CodecVersion)}},"policy":{{policy}},"policyFingerprint":"{{fingerprint}}"}
            """;
    }

    internal static string OperationProvenanceJson(RevisionReportKey key) => $$"""
        {"revision":{{ScopeJson(key.Scope)}},"nativeArtifactDigest":{"algorithm":"sha256","value":"{{key.NativeDigest.Value}}"},"backend":{"id":{{Quote(key.BackendId)}},"revision":{{Quote(key.BackendRevision)}},"buildId":{{Quote(key.BackendBuild)}}},"adapterVersion":{{Quote(key.AdapterBuild)}},"confirmed":true}
        """;
    internal static string DocumentProvenanceJson(RevisionReportKey key) => $$"""
        {"revision":{{ScopeJson(key.Scope)}},"nativeArtifactDigest":{"algorithm":"sha256","value":"{{key.NativeDigest.Value}}"},"backend":{"id":{{Quote(key.BackendId)}},"revision":{{Quote(key.BackendRevision)}},"buildId":{{Quote(key.BackendBuild)}}},"adapterVersion":{{Quote(key.AdapterBuild)}},"projectionVersion":{{Quote(key.AlgorithmVersion)}}}
        """;
    internal static string ProjectionJson(ProjectionCacheKey key, string parameters = "[]", string fields = "[]", string boundaries = "[]") => $$"""
        {"status":"succeeded","provenance":{{OperationProvenanceJson(key)}},"document":{"provenance":{{DocumentProvenanceJson(key)}},"coverage":{"status":"complete","gaps":[]},"boundaries":{{boundaries}},"fields":{{fields}},"parameters":{{parameters}},"rules":[],"controls":[],"relationships":[],"diagnostics":[],"capabilities":[],"schemaVersion":"braid.mapping-projection.v1"},"diagnostics":[]}
        """;
    internal static string ValidationJson(ValidationCacheKey key, bool valid = true)
    {
        var diagnostics = valid ? "[]" : """
            [{"code":"invalid","severity":"error","message":"mapping","messageTruncated":false,"nativeLocator":null,"owner":{"kind":"failureRule","index":"0"},"location":null,"locationTruncated":false,"projectionLocator":null}]
            """;
        return $$"""
            {"status":"{{(valid ? "succeeded" : "invalidMapping")}}","provenance":{{OperationProvenanceJson(key)}},"isValid":{{(valid ? "true" : "false")}},"totalIssueCount":{{(valid ? "0" : "1")}},"emittedIssueCount":{{(valid ? "0" : "1")}},"truncated":false,"diagnostics":{{diagnostics}}}
            """;
    }
    internal static string Envelope(RevisionReportClaim claim, string report) => $$"""
        {"schemaVersion":"braid.revision-report-envelope.v1","codecVersion":"braid.revision-report-codec.v1","operation":"{{Operation(claim.Key)}}","key":{{KeyJson(claim.Key)}},"cacheEntryId":"{{claim.CacheEntryId:D}}","derivationId":"{{claim.DerivationId:D}}","generation":{{claim.Generation.ToString(CultureInfo.InvariantCulture)}},"candidateObjectId":"{{claim.CandidateObjectId.ObjectId:D}}","report":{{report}}}
        """;

    internal static RevisionReportObservation ProjectionObservation(RevisionReportKey key, int topLevelItems = 0) => new(
        RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded,
        new(key.Scope, key.NativeDigest, key.Profile, true), new(RevisionReportCoverage.Complete, topLevelItems, 0, 0, 0), null);
    internal static RevisionReportObservation ValidationObservation(RevisionReportKey key, bool valid = true) => new(
        RevisionReportOperation.Validation, valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping,
        new(key.Scope, key.NativeDigest, key.Profile, true), null, new(valid, valid ? 0 : 1, valid ? 0 : 1, false, valid ? 0 : 1));
    internal static RevisionReportResult Metadata(RevisionReportClaim claim, byte[] bytes, RevisionReportObservation? observation = null,
        PrivateObjectId? referenceId = null) => new(claim.DerivationId, claim.CacheEntryId, claim.Key, claim.Generation,
        claim.OriginalActor, claim.StartedAtUtc, claim.StartedAtUtc.AddSeconds(1),
        new(referenceId ?? claim.CandidateObjectId, Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.LongLength),
        observation ?? (claim.Key.Operation == RevisionReportOperation.Projection ? ProjectionObservation(claim.Key) : ValidationObservation(claim.Key)));
    internal static RevisionReportBackendResult Backend(ProjectionCacheKey key, string rawReport, int topLevelItems = 0) =>
        RevisionReportBackendResult.ForProjection(key,
            JsonSerializer.Deserialize<ProjectionReportV1>(rawReport, Braid.Contracts.Projections.ProjectionJson.CreateOptions())!,
            ProjectionObservation(key, topLevelItems));
    internal static RevisionReportBackendResult Backend(ValidationCacheKey key, string rawReport, bool valid = true) =>
        RevisionReportBackendResult.ForValidation(key,
            JsonSerializer.Deserialize<ValidationReportV1>(rawReport, Braid.Contracts.Projections.ProjectionJson.CreateOptions())!,
            ValidationObservation(key, valid));

    internal static string Parameter(string literal, string scalarType = "string") => $$$"""
        {"id":"p","name":"exact","dataType":{"kind":"scalar","scalarTypes":["{{{scalarType}}}"],"nativeName":null},"requirement":"optional","defaultValue":{{{literal}}},"nativeLocator":{"kind":"document","documentId":{{{Quote(Source.LogicalDocument)}}}}}
        """;
    internal static string Quote(string value) => JsonSerializer.Serialize(value);
    internal static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    internal static string Operation(RevisionReportKey key) => key.Operation == RevisionReportOperation.Projection ? "projection" : "validation";

    internal sealed class StoredOnlyRegistry(bool supported = true) : IRevisionReportProfileRegistry, IRevisionReportFormatValidator
    {
        internal int StoredLookups { get; private set; }
        internal int Validations { get; private set; }
        internal Action? OnValidate { get; set; }
        public RevisionReportRegistration? FindCurrent(string sourceBackendId, RevisionReportOperation operation) =>
            throw new InvalidOperationException("A pure stored codec must not resolve current backend selection.");
        public IRevisionReportFormatValidator? FindStored(RevisionReportFormat format)
        {
            StoredLookups++;
            return supported ? this : null;
        }
        public void Validate(RevisionReportKey key, ProjectionReportV1 report, CancellationToken token = default) => Check(token);
        public void Validate(RevisionReportKey key, ValidationReportV1 report, CancellationToken token = default) => Check(token);
        private void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Validations++;
            OnValidate?.Invoke();
            token.ThrowIfCancellationRequested();
        }
    }
}
