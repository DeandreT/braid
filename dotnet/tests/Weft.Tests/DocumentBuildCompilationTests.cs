using System.Text;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class DocumentBuildCompilationTests
{
    [Theory]
    [InlineData(BuildWorkerProtocol.DocumentProfile, "GeneratedMapping.Documents.cs", true)]
    [InlineData(BuildWorkerProtocol.X12InterchangesProfile, "GeneratedMapping.Documents.cs", true)]
    [InlineData(BuildWorkerProtocol.Profile, "GeneratedMapping.Documents.cs", false)]
    [InlineData(BuildWorkerProtocol.DocumentProfile, "Arbitrary.cs", false)]
    public async Task Only_the_document_profile_admits_the_fixed_document_companion(string profile, string companion, bool admitted)
    {
        var root = Directory.CreateTempSubdirectory("weft-document-compilation-").FullName;
        try
        {
            var toolchain = Path.Combine(root, "toolchain");
            Directory.CreateDirectory(BuildCompilation.References(toolchain));
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            var sources = new[] { "GeneratedMapping.cs", "GeneratedTargetBuilder.cs", "Runtime/Value.cs", companion };
            var contents = sources.Select(_ => Encoding.UTF8.GetBytes("// fixture source\n")).ToArray();
            var files = sources.Select((path, index) => new BuildWorkerProtocol.File(path, contents[index].Length, Sha256Digest.FromBytes(contents[index]).Value)).ToArray();
            var reply = new BuildWorkerProtocol.Reply(new(BuildWorkerProtocol.Protocol, null, null, null, null,
                BuildWorkerProtocol.Engine, profile, "supported", [], files), contents);
            if (!admitted)
                await Assert.ThrowsAsync<InvalidOperationException>(() => BuildCompilation.PrepareAsync(output, toolchain, reply, default));
            else
            {
                await BuildCompilation.PrepareAsync(output, toolchain, reply, default);
                Assert.True(File.Exists(Path.Combine(output, companion)));
                Assert.Contains(Path.Combine(output, companion), await File.ReadAllTextAsync(Path.Combine(output, BuildCompilation.ResponseFile)));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
