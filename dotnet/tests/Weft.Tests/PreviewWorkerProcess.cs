using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Weft.Tests;

/// <summary>The ordinary Weft.Worker build, started with only its explicit configuration.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PreviewWorkerProcess
{
    private readonly Process process;
    private readonly StringBuilder output = new();

    private PreviewWorkerProcess(Process process) => this.process = process;

    internal bool HasExited => process.HasExited;

    internal static PreviewWorkerProcess Start(PreviewRunTestSupport test, PrivateStorageTestRoot storage)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "Weft.Worker.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The preview worker build must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!
        };
        start.ArgumentList.Add(dll);
        start.Environment["WEFT_PREVIEW_WRITER_CONNECTION_STRING"] = test.Case.PreviewWriterConnectionString;
        start.Environment["WEFT_PRIVATE_STORAGE_ROOT"] = storage.RootPath;
        start.Environment["WEFT_FERRULE_WORKER_IMAGE"] = Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE");
        start.Environment["WEFT_PREVIEW_LEASE_SECONDS"] = "35";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        var started = Process.Start(start) ?? throw new InvalidOperationException("The preview worker did not start.");
        var worker = new PreviewWorkerProcess(started);
        started.OutputDataReceived += (_, line) => worker.Append(line.Data);
        started.ErrorDataReceived += (_, line) => worker.Append(line.Data);
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        return worker;
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (output)
        {
            if (output.Length < 16 * 1024) output.AppendLine(line);
        }
    }

    internal string Diagnostics()
    {
        lock (output) return output.ToString();
    }

    internal void Kill()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
    }
}
