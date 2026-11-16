using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleInterchangeTests
{
    [Fact]
    public void PackageProfileDoesNotWidenInspection()
    {
        Assert.Equal(8 * 1024 * 1024 + 4, DockerWorkerProfile.Inspection.MaximumRequestBytes);
        Assert.Equal(268_435_456, DockerWorkerProfile.Inspection.MemoryBytes);
        Assert.True(DockerWorkerProfile.Interchange.MaximumRequestBytes > 16 * 1024 * 1024);
        Assert.Equal(DockerWorkerProfile.Interchange, FerruleServiceProtocol.Profile("interchange"));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("engine")]
    [InlineData("count")]
    [InlineData("warningOverflow")]
    [InlineData("trailing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("longWarning")]
    [InlineData("warningAlias")]
    [InlineData("warningCaseOnly")]
    [InlineData("statusAlias")]
    public async Task AdapterRefusesMalformedWorkerOutput(string mutation)
    {
        var backend = new FerruleInterchangeBackend(new Reply(mutation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ImportAsync(
            new("design.mfd", [new("design.mfd", "self-authored"u8)]), CancellationToken.None));
    }

    [Fact]
    public async Task AdapterPreservesEveryCanonicalWarning()
    {
        var backend = new FerruleInterchangeBackend(new Reply("none"));
        var result = await backend.ImportAsync(new("design.mfd", [new("design.mfd", "self-authored"u8)]), CancellationToken.None);
        var warning = Assert.Single(result.Report.Warnings);
        Assert.Equal("unsupported", warning.Code);
        Assert.Equal("Keep this complete warning", warning.Message);
    }

    [ServiceWorkerFact]
    public async Task RealSandboxRoundtripRetainsAssetsAndRefusesMissingSibling()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            await using var runner = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(root, DockerWorkerProfile.Interchange));
            await Roundtrip(new(runner));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [ServiceWorkerFact]
    public async Task PackagedServiceExposesTheSeparateInterchangeProfile()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        using var server = InspectionTransportTests.TransportCase.Certificate("CN=MFD service");
        using var client = InspectionTransportTests.TransportCase.Certificate("CN=MFD client");
        try
        {
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(server),
                [InspectionTransportTests.TransportCase.Pin(client)], root);
            var connector = new PinnedServiceConnector(new(
                new Uri($"https://127.0.0.1:{service.Port}"), InspectionTransportTests.TransportCase.Pin(server), client));
            await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InterchangeProfile,
                PackagedFerruleService.Identity));
            await Roundtrip(new(runner));
            await runner.StopAsync();
            Assert.Equal(0, await service.StopAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task Roundtrip(FerruleInterchangeBackend backend)
    {
        var native = FerruleServiceTestSupport.Artifact("valid-basic");
        var exported = await backend.ExportAsync(native, "maps/design.mfd", CancellationToken.None);
        Assert.True(exported.Package.Files.Count > 1);
        var imported = await backend.ImportAsync(exported.Package, CancellationToken.None);
        Assert.Equal(exported.Package.Digest, imported.PackageDigest);
        Assert.Equal(backend.Profile, imported.Profile);
        var again = await backend.ExportAsync(NativeArtifact.Admit(native.Scope, imported.Native, 4 * 1024 * 1024),
            "maps/design.mfd", CancellationToken.None);
        Assert.Equal(exported.Package.Digest, again.Package.Digest);
        var design = exported.Package.Files.Single(file => file.Path == exported.Package.DesignPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ImportAsync(
            new(exported.Package.DesignPath, [design]), CancellationToken.None));
    }

    private sealed class Reply(string mutation) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(requestFrame.Span);
            var request = JsonNode.Parse(requestFrame.Slice(4, size).Span)!;
            var native = "native"u8.ToArray();
            var response = new JsonObject
            {
                ["protocolVersion"] = FerruleInterchangeBackend.Protocol,
                ["requestId"] = request["requestId"]!.DeepClone(),
                ["operation"] = "import",
                ["inputDigest"] = request["inputDigest"]!.DeepClone(),
                ["designPath"] = request["designPath"]!.DeepClone(),
                ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
                ["status"] = "succeeded",
                ["payloadDigest"] = Sha256Digest.FromBytes(native).Value,
                ["payloadBytes"] = native.Length,
                ["summary"] = "Synthetic report",
                ["warnings"] = new JsonArray(new JsonObject { ["code"] = "unsupported", ["message"] = "Keep this complete warning" })
            };
            switch (mutation)
            {
                case "digest": response["payloadDigest"] = new string('0', 64); break;
                case "engine": response["engineRevision"] = "wrong"; break;
                case "count": response["payloadBytes"] = native.Length + 1; break;
                case "unknown": response["unknown"] = true; break;
                case "warningAlias": response["Warnings"] = new JsonArray(); break;
                case "warningCaseOnly": response["Warnings"] = response["warnings"]!.DeepClone(); response.Remove("warnings"); break;
                case "statusAlias": response["Status"] = "succeeded"; break;
                case "longWarning":
                    response["warnings"] = new JsonArray(new JsonObject
                    {
                        ["code"] = "warning",
                        ["message"] = new string('x', 2049)
                    }); break;
                case "warningOverflow":
                    response["warnings"] = new JsonArray(Enumerable.Range(0, 257)
                        .Select(_ => (JsonNode)new JsonObject { ["code"] = "warning", ["message"] = "message" }).ToArray()); break;
            }
            var header = JsonSerializer.SerializeToUtf8Bytes(response);
            if (mutation == "duplicate")
                header = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(header)
                    .Replace("\"status\":\"succeeded\"", "\"status\":\"succeeded\",\"status\":\"succeeded\"", StringComparison.Ordinal));
            var output = new byte[header.Length + native.Length + 8 + (mutation == "trailing" ? 1 : 0)];
            BinaryPrimitives.WriteInt32BigEndian(output, header.Length);
            header.CopyTo(output, 4);
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(header.Length + 4), native.Length);
            native.CopyTo(output, header.Length + 8);
            return Task.FromResult(new WorkerProcessResult(output, WorkerProcessFailure.None));
        }
    }
}
