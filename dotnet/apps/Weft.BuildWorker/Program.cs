using System.Diagnostics;
using System.Runtime.Versioning;
using Weft.Deployment;

if (!OperatingSystem.IsLinux()) return 1;
return await CompilerWorker.RunAsync();

[SupportedOSPlatform("linux")]
internal static class CompilerWorker
{
    internal static async Task<int> RunAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        string? root = null;
        var stage = "request";
        try
        {
            var input = await BoundedAsync(Console.OpenStandardInput(), BuildWorkerProtocol.MaximumNativeBytes + 16388, stop.Token);
            var request = BuildWorkerProtocol.DecodeRequest(input).Header;
            stage = "generation";
            var generated = await ProcessAsync("/ferrule-host", [], input, "/", stop.Token);
            var reply = BuildWorkerProtocol.DecodeReply(generated, request);
            if (reply.Header.Status != "supported" || request.Operation == "inspect")
            {
                await Console.OpenStandardOutput().WriteAsync(BuildCompilation.Refusal(reply), stop.Token);
                return 0;
            }
            root = Directory.CreateTempSubdirectory("weft-compile-").FullName;
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await BuildCompilation.PrepareAsync(root, BuildCompilation.DotnetRoot, reply, stop.Token);
            stage = "compilation";
            await ProcessAsync(BuildCompilation.Host(BuildCompilation.DotnetRoot), [.. BuildCompilation.CompilerArguments(BuildCompilation.DotnetRoot, root)], null, root, stop.Token);
            stage = "artifact";
            var dll = await File.ReadAllBytesAsync(Path.Combine(root, BuildCompilation.Output), stop.Token);
            await Console.OpenStandardOutput().WriteAsync(BuildCompilation.Artifact(reply, dll), stop.Token);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { Console.Error.WriteLine("WeftBuildWorkerFailed:" + stage + ":" + error.GetType().Name); return 1; }
        finally { if (root is not null) try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static async Task<byte[]> ProcessAsync(string executable, string[] arguments, byte[]? input, string directory, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/share/dotnet:/usr/bin:/bin";
        start.Environment["HOME"] = directory == "/" ? "/tmp" : directory;
        start.Environment["TMPDIR"] = "/tmp";
        start.Environment["DOTNET_ROOT"] = "/usr/share/dotnet";
        start.Environment["DOTNET_CLI_HOME"] = start.Environment["HOME"];
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true";
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["NUGET_PACKAGES"] = "/tmp/nuget";
        start.Environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/nuget-http";
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        try
        {
            var output = BoundedAsync(process.StandardOutput.BaseStream, BuildWorkerProtocol.MaximumReplyBytes, token);
            var error = BoundedAsync(process.StandardError.BaseStream, 65536, token);
            var writing = InputAsync(process, input, token);
            await Task.WhenAll(output, error, writing, process.WaitForExitAsync(token));
            if (process.ExitCode != 0) throw new InvalidOperationException();
            return await output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(reap.Token);
        }
    }
    private static async Task InputAsync(Process process, byte[]? input, CancellationToken token)
    {
        if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, token);
        process.StandardInput.Close();
    }
    private static async Task<byte[]> BoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        { if (count > maximum - bytes.Length) throw new InvalidOperationException(); bytes.Write(buffer, 0, count); }
        return bytes.ToArray();
    }
}
