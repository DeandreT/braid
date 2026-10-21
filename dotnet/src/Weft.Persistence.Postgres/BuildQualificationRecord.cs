using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Reconstructs retained proof from one owner-authorized database row; no current runtime or separate credential lookup.</summary>
internal static class BuildQualificationRecord
{
    internal static BuildQualification Read(NpgsqlDataReader rows, int offset, ActorContext actor, MappingScope mapping, Guid qualificationId, CancellationToken token)
    {
        var proof = rows.GetFieldValue<byte[]>(offset); var completion = rows.GetFieldValue<byte[]>(offset + 1);
        var intentBytes = rows.GetFieldValue<byte[]>(offset + 2); var caseBytes = rows.GetFieldValue<byte[]>(offset + 10);
        var evidenceBytes = rows.GetFieldValue<byte[]>(offset + 13);
        if (proof.Length is < 1 or > 16384 || completion.Length is < 1 or > RegressionRunResultCodec.MaximumBytes ||
            intentBytes.Length is < 1 or > 1048576 || caseBytes.Length is < 1 or > RegressionCaseEnvelopeCodec.MaximumEnvelopeBytes ||
            evidenceBytes.Length is < 1 or > RegressionRunResultCodec.MaximumBytes) throw Invalid();
        using var document = JsonDocument.Parse(proof);
        var metadata = document.RootElement;
        var intent = BuildIntentCodec.Decode(intentBytes);
        if (intent.Revision.OrganizationId != mapping.OrganizationId || intent.Revision.ProjectId != mapping.ProjectId ||
            intent.Revision.MappingId != mapping.MappingId || metadata.GetProperty("qualificationId").GetGuid() != qualificationId ||
            metadata.GetProperty("actorId").GetGuid() != actor.PrincipalId.Value) throw Invalid();
        var artifact = BuildArtifactReference.Restore(intent, Sha256Digest.Parse(rows.GetString(offset + 3)), rows.GetInt32(offset + 4));
        var build = new BuildRunSnapshot(actor.PrincipalId, intent, BuildRunState.Succeeded, rows.GetInt32(offset + 5),
            new(rows.GetDateTime(offset + 6)), (BuildExecutionOutcome)rows.GetInt32(offset + 7), artifact);
        var reference = new RegressionCaseReference(metadata.GetProperty("caseId").GetGuid(), metadata.GetProperty("caseVersion").GetInt64(),
            new(rows.GetGuid(offset + 8)), intent.Revision, intent.NativeDigest, Sha256Digest.Parse(metadata.GetProperty("caseDigest").GetString()!), rows.GetInt32(offset + 9));
        var sourceCase = new RegressionCaseEnvelopeCodec().Decode(reference, caseBytes, token);
        var evidence = new RegressionRunSnapshot(intent.RegressionEvidenceId, actor.PrincipalId, reference, RegressionRunState.Completed,
            rows.GetInt32(offset + 11), new(rows.GetDateTime(offset + 12)), evidenceBytes, Sha256Digest.FromBytes(evidenceBytes));
        return BuildQualification.Restore(qualificationId, actor.PrincipalId, build, evidence, sourceCase,
            metadata.GetProperty("runtimeImage").GetString()!, proof, completion, token);
    }
    private static InvalidOperationException Invalid() => new("Qualification persistence refused.");
}
