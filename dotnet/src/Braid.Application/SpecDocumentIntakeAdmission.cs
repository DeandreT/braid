using Braid.Domain;

namespace Braid.Application;

public sealed record SpecIntakeFormat(string Format, int MaximumSourceBytes, int ExtractorVersion, string EvidenceFormat);

/// <summary>Trusted host composition only. An HTTP request cannot supply workers or schema/installation qualification.</summary>
public sealed class SpecDocumentIntakeAdmission
{
    public static SpecDocumentIntakeAdmission Unavailable { get; } = new();
    private SpecDocumentIntakeAdmission() { }
    internal SpecDocumentIntakeAdmission(Func<CancellationToken, Task<bool>> schema46,
        ISpecOfficeExtractionWorker? office, SpecOfficeExtractionLimits officeLimits, Func<bool> officeAvailable,
        ISpecPdfExtractionWorker? pdf, SpecPdfExtractionProfile? pdfProfile, Func<bool> pdfAvailable)
    {
        this.schema46 = schema46; Office = office; OfficeLimits = officeLimits; this.officeAvailable = officeAvailable;
        Pdf = pdf; PdfProfile = pdfProfile; this.pdfAvailable = pdfAvailable;
    }
    private readonly Func<CancellationToken, Task<bool>>? schema46;
    private readonly Func<bool>? officeAvailable, pdfAvailable;
    internal ISpecOfficeExtractionWorker? Office { get; }
    internal SpecOfficeExtractionLimits OfficeLimits { get; } = new();
    internal ISpecPdfExtractionWorker? Pdf { get; }
    internal SpecPdfExtractionProfile? PdfProfile { get; }
    internal async Task<IReadOnlyList<SpecIntakeFormat>> FormatsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var formats = new List<SpecIntakeFormat>
        {
            new("text", SpecCapturedSource.MaximumBytes, SpecSourceExtractor.Version, "braid.spec-evidence.v1"),
            new("json", SpecCapturedSource.MaximumBytes, SpecSourceExtractor.Version, "braid.spec-evidence.v1"),
            new("openapi-json", SpecCapturedSource.MaximumBytes, SpecSourceExtractor.Version, "braid.spec-evidence.v1")
        };
        if (schema46 is null || !await schema46(token).ConfigureAwait(false)) return formats.AsReadOnly();
        if (Office is not null && officeAvailable?.Invoke() == true)
        {
            formats.Add(new("docx", OfficeLimits.MaximumSourceBytes, SpecOfficeExtractionResult.ExtractorVersion, "braid.spec-evidence.v2"));
            formats.Add(new("xlsx", OfficeLimits.MaximumSourceBytes, SpecOfficeExtractionResult.ExtractorVersion, "braid.spec-evidence.v2"));
        }
        if (Pdf is not null && PdfProfile is not null && pdfAvailable?.Invoke() == true)
            formats.Add(new("pdf", SpecPdfWorkerResponse.MaximumSourceBytes, SpecPdfExtractionResult.ExtractorVersion, "braid.spec-evidence.v2"));
        return formats.AsReadOnly();
    }
    internal async Task RequireAsync(SpecSourceFormat format, CancellationToken token)
    {
        var name = format switch { SpecSourceFormat.Docx => "docx", SpecSourceFormat.Xlsx => "xlsx", SpecSourceFormat.Pdf => "pdf", _ => throw new ArgumentException("Structural format required.") };
        if (!(await FormatsAsync(token).ConfigureAwait(false)).Any(item => item.Format == name)) throw new SpecDocumentIntakeUnavailableException();
    }
}

public sealed class SpecDocumentIntakeUnavailableException : Exception
{
    public SpecDocumentIntakeUnavailableException() : base("Qualified document intake is unavailable.") { }
}
