using System.Security.Cryptography;
using System.Text;

namespace Weft.MappingBackends.Workers;

/// <summary>A compile transport's complete backend identity, separate from its native inspection identity.</summary>
public interface ICompilationIdentitySource
{
    string? CompilationBuildIdentity { get; }
}

public static class CompileWorkerIdentity
{
    public static string Combine(string nativeIdentity, string toolchainIdentity)
    {
        var native = InspectionTransport.RequireImage(nativeIdentity);
        var toolchain = InspectionTransport.RequireImage(toolchainIdentity);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "weft.compiler-identity.v1\nnative=" + native + "\ntoolchain=" + toolchain + "\n")));
    }
}
