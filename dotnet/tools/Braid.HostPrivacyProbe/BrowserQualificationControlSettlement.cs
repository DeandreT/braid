namespace Braid.BrowserQualification;

internal sealed partial class QualificationSocketServer
{
    private readonly object closeSync = new();
    private int failed, failedWrites;
    internal int Failed => Volatile.Read(ref failed);
    internal int FailedWrites => Volatile.Read(ref failedWrites);
    // Not a passing receipt/validation flag. Published only after the ORIGINAL
    // accept task, worker/handler/write tasks and local channel disposal completed.
    internal bool ControlSettled { get; private set; }
    internal Task CloseAsync() { lock (closeSync) return closing ??= CloseCoreAsync(); }
    private async Task CloseCoreAsync()
    {
        await intake.CancelAsync(); listener.Dispose(); await accepting;
        Task[] originals; lock (workers) originals = workers.ToArray();
        await Task.WhenAll(originals);
        admission.Dispose(); intake.Dispose(); File.Delete(path);
        ControlSettled = true;
        // A joined failed request/write remains a qualification failure, not an
        // unjoined task. Never publish positive evidence from a catch/finally.
        QualificationWire.Require(Failed == 0); Settled = true;
    }
}
