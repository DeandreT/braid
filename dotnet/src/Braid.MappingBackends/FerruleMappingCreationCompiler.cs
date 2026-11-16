using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.MappingBackends;

/// <summary>Adapts only verified authoring checkpoints into application generation evidence.
/// The snapshot binding is private application provenance, not a compiler claim about source documents.</summary>
public sealed class FerruleMappingCreationCompiler(FerruleInspectionBackend backend) : IMappingCreationCompiler
{
    public async Task<CreationCompilationResult> CompileAsync(ProjectScope project, Guid authoringId, MappingCreationPlan plan,
        SpecBundleSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(snapshot);
        if (authoringId == Guid.Empty || snapshot.Bundle.Project != project)
            throw new ArgumentException("Authoring requires the exact snapshot project and a nonempty identity.");
        var bytes = plan.CopyCompilerPlanBytes();
        if (Sha256Digest.FromBytes(bytes) != plan.CompilerDigest) return Failure("creationPlanMismatch", IngestionBackendStatus.ProtocolFailure);
        var result = await backend.AuthorAsync(project, authoringId, bytes, ct).ConfigureAwait(false);
        if (result is null || (result.Checkpoint is null) == (result.Failure is null)) return Failure("creationResultMismatch", IngestionBackendStatus.ProtocolFailure);
        if (result.Failure is not null) return new(null, result.Failure);
        var checkpoint = result.Checkpoint!;
        if (checkpoint.Project != project || checkpoint.AuthoringId != authoringId || checkpoint.PlanDigest != plan.CompilerDigest ||
            checkpoint.EngineRevision != FerruleInspectionBackend.EngineRevision)
            return Failure("creationIdentityMismatch", IngestionBackendStatus.ProtocolFailure);
        var native = checkpoint.CopyNativeBytes();
        if (native.Length != checkpoint.NativeByteCount || Sha256Digest.FromBytes(native) != checkpoint.NativeDigest)
            return Failure("creationNativeMismatch", IngestionBackendStatus.ProtocolFailure);
        try
        {
            // Serialize only the actual verified typed reports, never caller/model report JSON.
            var validation = Report(checkpoint.Validation);
            var projection = Report(checkpoint.Projection);
            return new(new(project, authoringId, checkpoint.PlanDigest, snapshot.Digest, checkpoint.EngineRevision,
                checkpoint.WorkerBuild, native, validation, projection), null);
        }
        catch (ReportLimitException) { return Failure("creationEvidenceLimit", IngestionBackendStatus.ResourceLimit); }
    }

    private static byte[] Report<T>(T report)
    {
        using var stream = new BoundedReport();
        JsonSerializer.Serialize(stream, report);
        return stream.ToArray();
    }

    private static CreationCompilationResult Failure(string code, IngestionBackendStatus status) =>
        new(null, new(code, "Creation compiler evidence was refused.", false, null, status));

    private sealed class ReportLimitException : Exception;
    private sealed class BoundedReport : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > 8 * 1024 * 1024) throw new ReportLimitException();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > 8 * 1024 * 1024) throw new ReportLimitException();
            base.Write(buffer);
        }
    }
}
