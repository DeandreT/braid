namespace Braid.Application;

/// <summary>Execution/admission failure, not an extraction outcome. No evidence may be published for this failure.</summary>
public sealed class SpecOfficeWorkerUnavailableException : Exception
{
    public SpecOfficeWorkerUnavailableException() : base("The qualified Office worker is unavailable.") { }
}
