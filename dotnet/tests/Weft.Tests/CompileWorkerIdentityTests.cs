using Weft.Deployment;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class CompileWorkerIdentityTests
{
    [Fact]
    public void NativeAndToolchainIdentitiesBothBindTheCompilationIdentity()
    {
        var native = "sha256:" + new string('1', 64);
        var toolchain = "sha256:" + new string('2', 64);
        var first = CompileWorkerIdentity.Combine(native, toolchain);
        Assert.Equal(first, CompileWorkerIdentity.Combine(native, toolchain));
        Assert.NotEqual(first, CompileWorkerIdentity.Combine("sha256:" + new string('3', 64), toolchain));
        Assert.NotEqual(first, CompileWorkerIdentity.Combine(native, "sha256:" + new string('3', 64)));
        Assert.NotEqual(native, first);
        Assert.Throws<ArgumentException>(() => CompileWorkerIdentity.Combine(native, "latest"));
    }

    [Fact]
    public void HostRuntimeCompilerReferencesAndLauncherAllAffectTheToolchainIdentity()
    {
        var root = Directory.CreateTempSubdirectory("weft-toolchain-identity-").FullName;
        try
        {
            var compiler = Path.Combine(root, "compiler");
            var references = Path.Combine(root, "references");
            var paths = new[]
            {
                Path.Combine(root, "dotnet"),
                Path.Combine(root, "host/fxr/version/hostfxr"),
                Path.Combine(root, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion, "runtime"),
                Path.Combine(compiler, "csc.dll"),
                Path.Combine(references, "System.Runtime.dll"),
                Path.Combine(root, "launcher")
            };
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "original");
            }
            string Identity() => ProcessCompileRunner.ComputeIdentity(root, compiler, references, paths[^1]);
            var original = Identity();
            foreach (var path in paths)
            {
                File.WriteAllText(path, "changed");
                Assert.NotEqual(original, Identity());
                File.WriteAllText(path, "original");
                Assert.Equal(original, Identity());
            }
            File.WriteAllText(Path.Combine(compiler, "additional.dll"), "original");
            Assert.NotEqual(original, Identity());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
