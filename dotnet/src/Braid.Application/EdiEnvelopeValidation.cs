using Braid.Application.Regression;

namespace Braid.Application;

public enum EdiExecutionValidation { Valid, Invalid }

/// <summary>Bounded envelope admission shared by compiled execution and regression comparison.</summary>
public static class EdiEnvelopeValidation
{
    /// <summary>Validate each interchange with its own declared X12 syntax before execution.</summary>
    public static EdiExecutionValidation ValidateExecution(ReadOnlySpan<byte> bytes, string mediaType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mediaType is not ("application/edi-x12" or "application/edifact"))
            throw new ArgumentException("An explicit EDI media type is required.", nameof(mediaType));
        return EdiSegmentComparison.ValidateExecution(bytes, mediaType, cancellationToken);
    }

    public static bool IsValid(ReadOnlySpan<byte> bytes, string mediaType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mediaType is not ("application/edi-x12" or "application/edifact"))
            throw new ArgumentException("An explicit EDI media type is required.", nameof(mediaType));
        return EdiSegmentComparison.IsValidEnvelope(bytes, mediaType, cancellationToken);
    }
}
