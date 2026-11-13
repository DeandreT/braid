using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class MappingEditBackendTests
{
    private static readonly RevisionScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ExactBaseAndClosedOperationsAreSeparateFramesAndFailureIndexIsChecked()
    {
        var native = Encoding.UTF8.GetBytes("unparsed by C# 9007199254740993");
        var script = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        var runner = new TranscriptRunner((frame, _) =>
        {
            var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(frame);
            var request = JsonNode.Parse(frame.AsSpan(4, headerLength))!;
            var nativeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4 + headerLength));
            Assert.Equal(native, frame.AsSpan(8 + headerLength, nativeLength).ToArray());
            var opsOffset = 8 + headerLength + nativeLength;
            Assert.Equal(script.CopyUtf8().Length, (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(opsOffset)));
            Assert.Equal(script.CopyUtf8(), frame[(opsOffset + 4)..]);
            return Reply(request, "operationRejected", "0");
        });
        var result = await new FerruleInspectionBackend(runner).ReplayAsync(NativeArtifact.Admit(Scope, native, native.Length), script);
        Assert.Null(result.Checkpoint);
        Assert.Equal("0", result.Failure!.OperationIndex);
        Assert.Equal(IngestionBackendStatus.InvalidMapping, result.Failure.Status);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("00")]
    [InlineData(null)]
    public async Task InvalidFailureIndexesAreProtocolFailures(string? index)
    {
        var runner = new TranscriptRunner((frame, _) => Reply(JsonNode.Parse(frame.AsSpan(4, (int)BinaryPrimitives.ReadUInt32BigEndian(frame)))!, "operationRejected", index));
        var result = await new FerruleInspectionBackend(runner).ReplayAsync(NativeArtifact.Admit(Scope, "{}"u8, 2), MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)));
        Assert.Equal(IngestionBackendStatus.ProtocolFailure, result.Failure!.Status);
    }

    [Theory]
    [InlineData(WorkerProcessFailure.TimedOut, IngestionBackendStatus.TimedOut)]
    [InlineData(WorkerProcessFailure.CleanupFailed, IngestionBackendStatus.IsolationFailure)]
    [InlineData(WorkerProcessFailure.SandboxUnavailable, IngestionBackendStatus.BackendUnavailable)]
    [InlineData(WorkerProcessFailure.WorkerFailed, IngestionBackendStatus.BackendFailure)]
    public async Task WorkerFailuresKeepTheirDistinctCategories(WorkerProcessFailure failure, IngestionBackendStatus expected)
    {
        var result = await new FerruleInspectionBackend(new TranscriptRunner((_, _) => new([], failure)))
            .ReplayAsync(NativeArtifact.Admit(Scope, "{}"u8, 2), MappingEditScript.Empty);
        Assert.Equal(expected, result.Failure!.Status);
    }

    [NativeEditFact]
    public async Task ActualNativeWorkerAdmitsTheCheckpointAndEvidenceWithoutCSharpDecodingNativeJson()
    {
        var native = await File.ReadAllBytesAsync(Path.Combine(FindRoot(), "engines/ferrule-host/tests/fixtures/valid-basic.json"));
        var immutableBase = NativeArtifact.Admit(Scope, native, MappingEditPolicy.MaximumNativeBytes);
        var backend = new FerruleInspectionBackend(new NativeRunner());
        var result = await backend.ReplayAsync(immutableBase, MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)));
        Assert.Null(result.Failure);
        var checkpoint = Assert.IsType<MappingEditCheckpoint>(result.Checkpoint);
        Assert.True(checkpoint.Validation.IsValid);
        Assert.NotNull(checkpoint.Projection.Document);
        Assert.Equal(checkpoint.Artifact.Digest, checkpoint.Evidence.Facts.Provenance.NativeDigest);
        Assert.Equal(checkpoint.Artifact.Digest.Value, checkpoint.Projection.Document.Provenance.NativeArtifactDigest.Value);
        Assert.Equal(native, immutableBase.CopyBytes());
    }

    [NativeEditFact]
    public async Task ServiceSandboxChildAgreesWithTheNativeWorkerOnExactBytesAndReports()
    {
        var native = await File.ReadAllBytesAsync(Path.Combine(FindRoot(), "engines/ferrule-host/tests/fixtures/valid-basic.json"));
        var artifact = NativeArtifact.Admit(Scope, native, MappingEditPolicy.MaximumNativeBytes);
        var script = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        var direct = await new FerruleInspectionBackend(new NativeRunner()).ReplayAsync(artifact, script);
        var temporary = Path.Combine(Path.GetTempPath(), "weft-edit-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await using var runner = new ProcessWorkerRunner(new ProcessWorkerOptions(
                Environment.GetEnvironmentVariable("WEFT_EDIT_TEST_EXECUTABLE")!, DockerWorkerProfile.Edit, temporary));
            var isolated = await new FerruleInspectionBackend(runner).ReplayAsync(artifact, script);
            Assert.Null(isolated.Failure);
            Assert.Equal(direct.Checkpoint!.Artifact.CopyBytes(), isolated.Checkpoint!.Artifact.CopyBytes());
            // Provenance correctly differs by observed runner identity; mapping facts do not.
            Assert.Equal(direct.Checkpoint.Validation.IsValid, isolated.Checkpoint.Validation.IsValid);
            var json = ProjectionJson.CreateOptions();
            Assert.Equal(JsonSerializer.Serialize(direct.Checkpoint.Validation with { Provenance = null }, json),
                JsonSerializer.Serialize(isolated.Checkpoint.Validation with { Provenance = null }, json));
            Assert.Equal(JsonSerializer.Serialize(direct.Checkpoint.Projection.Document! with { Provenance = isolated.Checkpoint.Projection.Document!.Provenance }, json),
                JsonSerializer.Serialize(isolated.Checkpoint.Projection.Document, json));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporary));
        }
        finally { Directory.Delete(temporary); }
    }

    private static WorkerProcessResult Reply(JsonNode request, string code, string? index)
    {
        var json = new JsonObject
        {
            ["protocolVersion"] = FerruleInspectionBackend.EditProtocolVersion,
            ["request"] = request,
            ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
            ["result"] = null,
            ["failure"] = new JsonObject { ["code"] = code, ["message"] = "Rejected", ["messageTruncated"] = false, ["operationIndex"] = index }
        };
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)bytes.Length); bytes.CopyTo(frame, 4);
        return new(frame, WorkerProcessFailure.None);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "engines/ferrule-host"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }

    private sealed class TranscriptRunner(Func<byte[], CancellationToken, WorkerProcessResult> reply) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken) => Task.FromResult(reply(requestFrame.ToArray(), cancellationToken));
    }

    private sealed class NativeRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "native-edit-test";
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("WEFT_EDIT_TEST_EXECUTABLE")!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment.Clear();
            using var process = Process.Start(start)!;
            await process.StandardInput.BaseStream.WriteAsync(requestFrame, cancellationToken);
            process.StandardInput.Close();
            using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await process.StandardError.ReadToEndAsync(cancellationToken));
            return new(output.ToArray(), WorkerProcessFailure.None);
        }
    }

    public sealed class NativeEditFactAttribute : FactAttribute
    {
        public NativeEditFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEFT_EDIT_TEST_EXECUTABLE") is null) Skip = "Set WEFT_EDIT_TEST_EXECUTABLE for actual native edit protocol qualification (not isolation).";
        }
    }
}
