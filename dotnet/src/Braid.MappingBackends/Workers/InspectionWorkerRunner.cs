namespace Braid.MappingBackends.Workers;

/// <summary>A byte-only transport seam; mapping envelopes belong to the backend adapter.</summary>
public interface IInspectionWorkerRunner
{
    string BuildIdentity { get; }
    Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken);
}

public enum WorkerProcessFailure
{
    None,
    Canceled,
    TimedOut,
    StartFailed,
    WorkerFailed,
    OutputLimitExceeded,
    CleanupFailed,
    SandboxUnavailable
}

public sealed record WorkerProcessResult(byte[] Output, WorkerProcessFailure Failure)
{
    internal ChildAdmissionFailure AdmissionFailure => ChildAdmissionDiagnostics.GetFailure(this);
}
