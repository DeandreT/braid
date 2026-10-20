using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>The compiled owner command against actual private directories and standard Data Protection.</summary>
[SupportedOSPlatform("linux")]
public sealed class SessionKeyToolTests
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly Guid Organization = Guid.Parse("6f0c8a52-1d3e-4b7a-9c45-0e2d7b8f1a63");

    [Fact]
    public async Task BootstrapCreatesAPrivateRingThatTheHostProfileReopens()
    {
        using var fixture = new ToolFixture();
        var result = await RunAsync(fixture.Arguments());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Bootstrapped", result.Output.Trim());
        Assert.Equal("", result.Error);
        var files = Directory.GetFiles(fixture.KeyDirectory);
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        using var certificate = X509Certificate2.CreateFromPemFile(fixture.CertificateFile, fixture.KeyFile);
        using var profile = DurableSessionKeyProfile.Open(new(Organization, fixture.Application, fixture.KeyDirectory), certificate, []);
        var plaintext = "session-key-tool-proof"u8.ToArray();
        var protectedBytes = profile.Protect(plaintext);
        Assert.Equal(plaintext, profile.Unprotect(protectedBytes));
        AssertNoPrivateText(result, fixture);
    }

    [Fact]
    public async Task ExistingRingIsRefusedWithoutChangingItsFiles()
    {
        using var fixture = new ToolFixture();
        Assert.Equal(0, (await RunAsync(fixture.Arguments())).ExitCode);
        var before = Snapshot(fixture.KeyDirectory);
        var replay = await RunAsync(fixture.Arguments());
        Assert.Equal(1, replay.ExitCode);
        Assert.Equal("ConfigurationFailure", replay.Error.Trim());
        Assert.Equal("", replay.Output);
        Assert.Equal(before, Snapshot(fixture.KeyDirectory));
        AssertNoPrivateText(replay, fixture);
    }

    [Fact]
    public async Task MissingOrPublicDirectoriesAreNeitherCreatedNorRepaired()
    {
        using var fixture = new ToolFixture();
        var missing = Path.Combine(fixture.Root, "missing");
        var absent = await RunAsync(fixture.Arguments(directory: missing));
        Assert.Equal(1, absent.ExitCode);
        Assert.Equal("ConfigurationFailure", absent.Error.Trim());
        Assert.False(Directory.Exists(missing));
        File.SetUnixFileMode(fixture.KeyDirectory, PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var shared = await RunAsync(fixture.Arguments());
        Assert.Equal(1, shared.ExitCode);
        Assert.Equal("ConfigurationFailure", shared.Error.Trim());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.KeyDirectory));
        Assert.Equal(PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(fixture.KeyDirectory));
        AssertNoPrivateText(absent, fixture);
        AssertNoPrivateText(shared, fixture);
    }

    [Fact]
    public async Task UnusableCertificatesLeaveTheDirectoryEmpty()
    {
        using var fixture = new ToolFixture();
        using (var small = RSA.Create(1024))
            fixture.WriteCertificate(small, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var weak = await RunAsync(fixture.Arguments());
        using (var expired = RSA.Create(2048))
            fixture.WriteCertificate(expired, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var stale = await RunAsync(fixture.Arguments());
        var missing = await RunAsync(fixture.Arguments(certificate: Path.Combine(fixture.Root, "absent.pem")));
        foreach (var result in new[] { weak, stale, missing })
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("CertificateRefused", result.Error.Trim());
            Assert.Equal("", result.Output);
            AssertNoPrivateText(result, fixture);
        }
        Assert.Empty(Directory.GetFileSystemEntries(fixture.KeyDirectory));
    }

    [Fact]
    public async Task InvalidUsageAndValuesFailBeforeAnyKeyIsCreated()
    {
        using var fixture = new ToolFixture();
        var valid = fixture.Arguments();
        string[][] invalid =
        [
            [], ["bootstrap", .. valid[1..]], valid[..^1], [.. valid, "extra"],
            [valid[0], Organization.ToString("D").ToUpperInvariant(), .. valid[2..]],
            [valid[0], Guid.Empty.ToString("D"), .. valid[2..]],
            [.. valid[..^1], "6"], [.. valid[..^1], "366"], [.. valid[..^1], "090"], [.. valid[..^1], "+90"],
            [.. valid[..4], "relative.pem", .. valid[5..]]
        ];
        foreach (var arguments in invalid)
        {
            var result = await RunAsync(arguments);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("", result.Output);
            Assert.True(result.Error.Trim() is "InvalidConfiguration" ||
                result.Error.StartsWith("Usage: Weft.SessionKeyTool bootstrap-empty ", StringComparison.Ordinal));
            AssertNoPrivateText(result, fixture);
        }
        Assert.Empty(Directory.GetFileSystemEntries(fixture.KeyDirectory));
    }

    private static Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void AssertNoPrivateText(ToolResult result, ToolFixture fixture)
    {
        var text = result.Output + result.Error;
        Assert.DoesNotContain(fixture.Root, text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Application, text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
    }

    private static async Task<ToolResult> RunAsync(string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "SessionKeyTool", "Weft.SessionKeyTool.dll");
        Assert.True(File.Exists(dll), "The session key tool and dependencies must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The session key tool did not start.");
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[256];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (text.Length + count > 1024) throw new InvalidOperationException("The session key tool exceeded bounded categorical output.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private sealed class ToolFixture : IDisposable
    {
        internal ToolFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "weft-session-key-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root, PrivateDirectory);
            KeyDirectory = Path.Combine(Root, "ring");
            Directory.CreateDirectory(KeyDirectory, PrivateDirectory);
            CertificateFile = Path.Combine(Root, "session.crt");
            KeyFile = Path.Combine(Root, "session.key");
            Application = "Weft.Session.ToolTests." + Guid.NewGuid().ToString("N");
            using var rsa = RSA.Create(2048);
            WriteCertificate(rsa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(180));
        }

        internal string Root { get; }
        internal string KeyDirectory { get; }
        internal string CertificateFile { get; }
        internal string KeyFile { get; }
        internal string Application { get; }

        internal string[] Arguments(string? directory = null, string? certificate = null) =>
            ["bootstrap-empty", Organization.ToString("D"), Application, directory ?? KeyDirectory,
                certificate ?? CertificateFile, KeyFile, "90"];

        internal void WriteCertificate(RSA rsa, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            var request = new CertificateRequest("CN=Weft Session Tool Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(notBefore, notAfter);
            File.WriteAllText(CertificateFile, certificate.ExportCertificatePem());
            File.WriteAllText(KeyFile, rsa.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
