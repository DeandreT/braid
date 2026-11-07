namespace Weft.Application;

/// <summary>
/// Optional local admission signal for an execution boundary. False defers dispatch before a durable attempt is
/// claimed; it is not remote readiness, authority, or evidence about a previous operation's outcome.
/// </summary>
public interface IExecutionAdmission
{
    bool CanExecute { get; }
}
