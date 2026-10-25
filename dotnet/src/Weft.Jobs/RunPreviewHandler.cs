using Weft.Application;
using Wolverine;

namespace Weft.Jobs;

/// <summary>Identity of this worker process; it owns the leases its attempts take.</summary>
public sealed record PreviewWorkerInstance(Guid Id);

/// <summary>
/// Executes one delivery of <see cref="RunPreview"/>. Redelivery is safe: the run's lease fences
/// every attempt, and a finished run is never executed again.
/// </summary>
public sealed class RunPreviewHandler
{
    public static async Task Handle(RunPreview command, PreviewRunExecutor executor, PreviewWorkerInstance worker,
        PreviewRunOptions options, IMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        // Pause/requeue the existing envelope instead of manufacturing scheduled deliveries for a closed backend.
        // Once admission reopens the normal store claim still observes terminal, missing and expired runs.
        if (!executor.CanExecute) throw new PreviewAdmissionClosedException();
        var outcome = await executor.ExecuteAsync(command.OrganizationId, command.RunId, worker.Id, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PreviewExecutionOutcome.Released:
                await context.ScheduleAsync(command, options.RetryDelay).ConfigureAwait(false);
                break;
            case PreviewExecutionOutcome.Busy:
                // Another attempt holds a live lease, or the run waits for its retry time.
                await context.ScheduleAsync(command, options.RetryDelay > TimeSpan.Zero ? options.RetryDelay : options.Lease).ConfigureAwait(false);
                break;
        }
    }
}

public sealed class PreviewAdmissionClosedException() : Exception("Preview execution admission is closed.");
