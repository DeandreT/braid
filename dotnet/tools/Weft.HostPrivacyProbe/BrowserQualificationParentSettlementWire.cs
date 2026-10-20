namespace Weft.BrowserQualification;

internal sealed partial record QualificationReply
{
    // Meaningful only with a genuine parent closed observation. False is never a
    // passing stop ACK; it lets the parent retain closure truth while failing policy.
    public bool CounterValidation { get; init; }
}
