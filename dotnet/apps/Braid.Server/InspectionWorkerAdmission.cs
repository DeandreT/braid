using Braid.Application;

namespace Braid.Server;

/// <summary>Observes the host's already-created shared runner without resolving it.</summary>
internal sealed class InspectionWorkerAdmission : IExecutionAdmission
{
    private IExecutionAdmission? known;

    public bool CanExecute => Volatile.Read(ref known) is not { CanExecute: false };

    internal void Bind(IExecutionAdmission runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var previous = Interlocked.CompareExchange(ref known, runner, null);
        if (previous is not null && !ReferenceEquals(previous, runner))
            throw new InvalidOperationException("Inspection admission belongs to one shared runner.");
    }
}
