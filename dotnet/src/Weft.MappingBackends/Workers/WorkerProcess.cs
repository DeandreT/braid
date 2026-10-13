using System.Diagnostics;

namespace Weft.MappingBackends.Workers;

/// <summary>Process seam for lifecycle tests. Implementations must start and kill promptly.</summary>
public interface IWorkerProcessLauncher
{
    IWorkerProcess Start(ProcessStartInfo startInfo);
}

public interface IWorkerProcess : IDisposable
{
    // The caller writes bytes but closes the pipe through its owning process wrapper.
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void CloseStandardInput();
    void Kill();
}

internal sealed class WorkerProcessLauncher : IWorkerProcessLauncher
{
    public IWorkerProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Docker CLI did not start.");
            }

            return new WorkerProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class WorkerProcess(Process process) : IWorkerProcess
{
    private readonly StreamWriter inputWriter = process.StandardInput;
    private int inputClosed;

    public Stream StandardInput => inputWriter.BaseStream;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardError => process.StandardError.BaseStream;
    public int ExitCode => process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public void CloseStandardInput()
    {
        if (Interlocked.Exchange(ref inputClosed, 1) == 0)
        {
            inputWriter.Dispose();
        }
    }

    public void Kill()
    {
        if (!process.HasExited)
        {
            // Only the fixed CLI process is launched. Its daemon-owned container is removed separately.
            process.Kill();
        }
    }

    public void Dispose()
    {
        try
        {
            CloseStandardInput();
        }
        finally
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            process.Dispose();
        }
    }
}
