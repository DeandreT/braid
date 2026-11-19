using Braid.Domain;

namespace Braid.Deployment;

/// <summary>
/// The fixed offline compilation of generated mapping sources, shared by the Docker compiler worker and the
/// Ferrule service so both produce the same artifact bytes: the admitted source set, the private directory
/// layout, the compiler arguments and the framed reply.
/// </summary>
public static class BuildCompilation
{
    public const string DotnetRoot = "/usr/share/dotnet";
    public const string Output = "Ferrule.Generated.dll";
    public const string ResponseFile = "compiler.rsp";
    private const string ProjectFile = "Ferrule.Generated.csproj";
    private const string GlobalUsingsFile = "GlobalUsings.cs";
    private const string GlobalUsings = "global using System;global using System.Collections.Generic;global using System.IO;global using System.Linq;global using System.Net.Http;global using System.Threading;global using System.Threading.Tasks;";

    /// <summary>The installed .NET host under a toolchain root.</summary>
    public static string Host(string root) => root + "/dotnet";

    /// <summary>The exact native host library, loaded in the already hardened launcher without exec.</summary>
    public static string HostFxr(string root) => root + "/host/fxr/" + BuildWorkerProtocol.RuntimeVersion + "/libhostfxr.so";

    /// <summary>The pinned Roslyn compiler, invoked directly without MSBuild or a compiler server.</summary>
    public static string Compiler(string root) => root + "/sdk/" + BuildWorkerProtocol.SdkVersion + "/Roslyn/bincore/csc.dll";

    /// <summary>The installed reference assemblies of the pinned runtime.</summary>
    public static string References(string root) => root + "/packs/Microsoft.NETCore.App.Ref/" + BuildWorkerProtocol.RuntimeVersion + "/ref/net10.0";

    /// <summary>The compiler host arguments after the host itself.</summary>
    public static IReadOnlyList<string> CompilerArguments(string root, string directory) =>
        ["exec", Compiler(root), "-noconfig", "@" + Path.Combine(directory, ResponseFile)];

    /// <summary>
    /// Writes only the known generated mapping, target-builder and Runtime C# files of a supported reply, the
    /// fixed global usings, the response file and an empty output file into an existing private directory. The
    /// emitted project file is never written or executed.
    /// </summary>
    public static async Task PrepareAsync(string directory, string root, BuildWorkerProtocol.Reply reply, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < reply.Header.Files.Length; i++)
        {
            var name = reply.Header.Files[i].Path;
            if (name == ProjectFile) continue;
            if (name is not ("GeneratedMapping.cs" or "GeneratedTargetBuilder.cs") &&
                !(reply.Header.Profile is (BuildWorkerProtocol.DocumentProfile or BuildWorkerProtocol.X12InterchangesProfile) && name == "GeneratedMapping.Documents.cs") &&
                !(reply.Header.Profile == BuildWorkerProtocol.CsvProfile && name is ("GeneratedMapping.Csv.cs" or "GeneratedMapping.CsvDocument.cs")) &&
                !(reply.Header.Profile == BuildWorkerProtocol.FlexProfile && name == "GeneratedMapping.FlexTextBytes.cs") &&
                !(reply.Header.Profile == BuildWorkerProtocol.ProtoProfile && name == "GeneratedMapping.Protobuf.cs") &&
                !(reply.Header.Profile == BuildWorkerProtocol.XbrlProfile && name == "GeneratedMapping.Xbrl.cs") &&
                !(name.StartsWith("Runtime/", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))) throw Invalid();
            var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, reply.Contents[i], token).ConfigureAwait(false); names.Add(name);
        }
        if (!names.Contains("GeneratedMapping.cs") || !names.Contains("GeneratedTargetBuilder.cs") || names.Count < 3) throw Invalid();
        await File.WriteAllTextAsync(Path.Combine(directory, GlobalUsingsFile), GlobalUsings, token).ConfigureAwait(false);
        var output = Path.Combine(directory, Output);
        var arguments = new List<string> { "-target:library", "-langversion:14", "-nullable:enable", "-warnaserror+", "-deterministic+", "-unsafe-", "-debug-", "-optimize+", "-nologo", "-nostdlib+", "-pathmap:" + directory + "=/braid/build", "-out:" + output };
        var references = References(root);
        if (!Directory.Exists(references)) throw Invalid();
        arguments.AddRange(Directory.GetFiles(references, "*.dll").Order(StringComparer.Ordinal).Select(path => "-reference:" + path));
        arguments.Add(Path.Combine(directory, GlobalUsingsFile));
        arguments.AddRange(names.Order(StringComparer.Ordinal).Select(name => Path.Combine(directory, name)));
        await File.WriteAllLinesAsync(Path.Combine(directory, ResponseFile), arguments.Select(argument => "\"" + argument + "\""), token).ConfigureAwait(false);
        // The compiler rewrites this file in place, so a confined compiler needs no right to create files.
        await File.WriteAllBytesAsync(output, [], token).ConfigureAwait(false);
    }

    /// <summary>The reply for a refused generation or a capability inspection: the generator's header without files.</summary>
    public static byte[] Refusal(BuildWorkerProtocol.Reply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return BuildWorkerProtocol.EncodeReply(reply.Header with { Files = [] }, []);
    }

    /// <summary>The reply carrying one compiled assembly and its exact provenance.</summary>
    public static byte[] Artifact(BuildWorkerProtocol.Reply reply, byte[] assembly)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.Length is < 1 or > BuildWorkerProtocol.MaximumSourceBytes) throw Invalid();
        var header = reply.Header with { Files = [new(Output, assembly.Length, Sha256Digest.FromBytes(assembly).Value)] };
        return BuildWorkerProtocol.EncodeReply(header, [assembly]);
    }

    private static InvalidOperationException Invalid() => new("Build compilation refused.");
}
