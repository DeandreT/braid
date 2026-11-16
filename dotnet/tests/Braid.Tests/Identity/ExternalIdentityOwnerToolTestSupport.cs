using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Braid.Application;

namespace Braid.Tests;

/// <summary>Actual copied DatabaseTool child, private binary stdin and bounded categorical output.</summary>
internal sealed class ExternalIdentityOwnerToolTestSupport : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string? connection;
    private readonly string[] arguments;
    private readonly string[] privateValues;
    private bool disposed;

    private ExternalIdentityOwnerToolTestSupport(Process process, string? connection,
        string[] arguments, string[] privateValues)
    {
        this.process = process;
        this.connection = connection;
        this.arguments = arguments;
        this.privateValues = privateValues;
        output = ReadBoundedAsync(process.StandardOutput, lifetime);
        error = ReadBoundedAsync(process.StandardError, lifetime);
    }

    internal static byte[] KeyBytes(ExternalIdentityKey key) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        provider = key.Provider,
        authority = key.Authority,
        subject = key.Subject
    });

    internal static async Task<Result> RunAsync(string? connection, string[] arguments,
        ReadOnlyMemory<byte> input = default, string[]? privateValues = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        await using var child = await StartAsync(connection, arguments, input, privateValues, environment);
        return await child.FinishAsync();
    }

    internal static async Task<ExternalIdentityOwnerToolTestSupport> StartAsync(string? connection,
        string[] arguments, ReadOnlyMemory<byte> input = default, string[]? privateValues = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (input.Length > 8193) throw new InvalidOperationException("The owned stdin fixture exceeds its probe bound.");
        var dll = Path.Combine(AppContext.BaseDirectory, "DatabaseTool", "Braid.DatabaseTool.dll");
        Assert.True(File.Exists(dll), "The copied owner DatabaseTool closure is required.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.ToArray())
        {
            if (name.StartsWith("BRAID_", StringComparison.Ordinal) || name.StartsWith("PG", StringComparison.Ordinal)
                || name.StartsWith("NPGSQL_", StringComparison.Ordinal)
                || name is "DOTNET_STARTUP_HOOKS" or "DOTNET_ADDITIONAL_DEPS" or "DOTNET_SHARED_STORE"
                    or "DOTNET_DiagnosticPorts" or "CORECLR_ENABLE_PROFILING" or "COR_ENABLE_PROFILING")
                start.Environment.Remove(name);
        }
        if (connection is not null) start.Environment["BRAID_MIGRATION_CONNECTION_STRING"] = connection;
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned identity tool did not start.");
        var child = new ExternalIdentityOwnerToolTestSupport(process, connection, arguments, privateValues ?? []);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, child.lifetime.Token);
            await process.StandardInput.BaseStream.FlushAsync(child.lifetime.Token);
            process.StandardInput.Close();
            return child;
        }
        catch
        {
            await child.DisposeAsync();
            throw;
        }
    }

    internal bool HasExited => process.HasExited;

    internal async Task<Result> FinishAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(lifetime.Token), output, error);
        var result = new Result(process.ExitCode, await output, await error);
        Safe(result);
        return result;
    }

    internal async Task InterruptAsync()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The owned SIGINT acceptance requires Linux.");
        Assert.False(process.HasExited, "The owned identity command ended before actual cancellation.");
        var start = new ProcessStartInfo("/bin/kill")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add("INT");
        start.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
        using var signal = Process.Start(start) ?? throw new InvalidOperationException("The owned cancellation signal did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signalOutput = ReadBoundedAsync(signal.StandardOutput, deadline);
        var signalError = ReadBoundedAsync(signal.StandardError, deadline);
        try
        {
            await Task.WhenAll(signal.WaitForExitAsync(deadline.Token), signalOutput, signalError);
            Assert.Equal(0, signal.ExitCode);
            Assert.True(string.Equals(await signalOutput, "", StringComparison.Ordinal)
                && string.Equals(await signalError, "", StringComparison.Ordinal), "The owned cancellation signal was not clean.");
        }
        finally
        {
            if (!signal.HasExited)
            {
                signal.Kill(entireProcessTree: true);
                await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    internal static void Category(Result result, int exitCode, string field, string category)
    {
        Assert.Equal(exitCode, result.ExitCode);
        var expected = "{\"" + field + "\":\"" + category + "\"}" + Environment.NewLine;
        var selected = field == "outcome" ? result.Output : result.Error;
        var other = field == "outcome" ? result.Error : result.Output;
        Assert.True(string.Equals(selected, expected, StringComparison.Ordinal), "The identity command categorical JSON was not exact.");
        Assert.True(string.Equals(other, "", StringComparison.Ordinal), "The identity command wrote an unexpected output channel.");
    }

    private void Safe(Result result)
    {
        var combined = result.Output + result.Error;
        var forbidden = new List<string> { "Npgsql", "PostgresException", "SQLSTATE", "Host=", "Password=", "Username=",
            "SocketException", "TimeoutException", "password authentication failed", "permission denied", "connection refused" };
        if (!string.IsNullOrWhiteSpace(connection))
        {
            forbidden.Add(connection);
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            foreach (var value in new[] { parsed.Password, parsed.Username, parsed.Database, parsed.Host })
                if (!string.IsNullOrWhiteSpace(value)) forbidden.Add(value);
        }
        foreach (var value in arguments)
            if (Guid.TryParse(value, out _)) forbidden.Add(value);
        forbidden.AddRange(privateValues.Where(value => value.Length != 0));
        foreach (var value in forbidden)
            Assert.False(combined.Contains(value, StringComparison.Ordinal), "The identity command exposed private data or provider diagnostics.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var text = new StringBuilder();
        var buffer = new char[256];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        {
            if (text.Length + count > 1024)
            {
                deadline.Cancel();
                throw new InvalidOperationException("The identity command exceeded bounded categorical output.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        finally
        {
            lifetime.Cancel();
            lifetime.Dispose();
            process.Dispose();
        }
    }

    internal sealed class Result(int exitCode, string output, string error)
    {
        internal int ExitCode { get; } = exitCode;
        internal string Output { get; } = output;
        internal string Error { get; } = error;
        public override string ToString() => "ExternalIdentityOwnerToolResult";
    }
}
