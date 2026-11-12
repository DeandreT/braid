using Weft.Domain;

namespace Weft.Application;

public sealed partial class SpecBundleService
{
    private SpecDocumentIntakeAdmission documents = SpecDocumentIntakeAdmission.Unavailable;

    /// <summary>Returns a new actor-bound service with trusted host admission, never request-supplied configuration.</summary>
    public SpecBundleService WithDocumentIntake(SpecDocumentIntakeAdmission admission) => new(lifecycle, objects)
    { documents = admission ?? throw new ArgumentNullException(nameof(admission)) };

    public async Task<IReadOnlyList<SpecIntakeFormat>> IntakeFormatsAsync(SpecBundleScope scope, CancellationToken token = default)
    {
        await lifecycle.AuthorizeAsync(scope, false, token).ConfigureAwait(false);
        return await documents.FormatsAsync(token).ConfigureAwait(false);
    }

    private async Task<SpecIntakeResult> IntakeDocumentAsync(SpecSourceIdentity source, SpecSourceFormat format, Guid originalId,
        Guid uploadKey, Guid extractionId, Guid extractionKey, long expectedVersion, long extractionExpectedVersion, Stream input, CancellationToken token)
    {
        await lifecycle.AuthorizeAsync(source.Bundle, true, token).ConfigureAwait(false);
        await documents.RequireAsync(format, token).ConfigureAwait(false);
        // Check the admitted format's cap before reserving the original, including a tighter trusted Office profile.
        var maximum = format == SpecSourceFormat.Pdf ? SpecPdfWorkerResponse.MaximumSourceBytes : documents.OfficeLimits.MaximumSourceBytes;
        var captured = await SpecSourceCapture.ReadAsync(source, format, input, maximum, token).ConfigureAwait(false);
        if (captured.Source is null) throw new SpecStorageException(SpecStorageError.Limit);
        await documents.RequireAsync(format, token).ConfigureAwait(false);
        using var bytes = new MemoryStream(captured.Source.CopyBytes(), false);
        await UploadAsync(source, format, originalId, uploadKey, expectedVersion, bytes, token).ConfigureAwait(false);
        await documents.RequireAsync(format, token).ConfigureAwait(false);
        if (format is SpecSourceFormat.Docx or SpecSourceFormat.Xlsx)
        {
            var result = await ExtractOfficeAndPublishAsync(source, format, originalId, extractionId, extractionKey, extractionExpectedVersion,
                new AdmittedOfficeWorker(documents), documents.OfficeLimits, token).ConfigureAwait(false);
            return new(originalId, extractionId, result.Publication.BundleVersion, result.Extraction.Outcome);
        }
        var original = await lifecycle.FindAsync(source.Bundle, originalId, token).ConfigureAwait(false);
        if (original.Id.Kind != SpecObjectKind.Original || original.Id.Source != source) throw new SpecStorageException(SpecStorageError.Integrity);
        var stored = new SpecCapturedSource(source, SpecSourceFormat.Pdf, await objects.ReadAsync(source.Bundle, original, token).ConfigureAwait(false));
        if (stored.Digest != original.Digest || stored.ByteCount != original.ByteCount) throw new SpecStorageException(SpecStorageError.Integrity);
        var pdf = await documents.Pdf!.ExtractAsync(stored, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (pdf.Identity.Source != source || pdf.Identity.SourceDigest != stored.Digest || pdf.Identity.Format != SpecSourceFormat.Pdf ||
            pdf.Identity.ProfileDigest != documents.PdfProfile!.Digest || pdf.Identity.ExtractorVersion != SpecPdfExtractionResult.ExtractorVersion)
            throw new SpecStorageException(SpecStorageError.Integrity);
        await documents.RequireAsync(format, token).ConfigureAwait(false);
        using var evidence = new MemoryStream(pdf.CopyEvidenceBytes(), false);
        var publication = await PublishExtractionAsync(pdf.Identity, extractionId, extractionKey, extractionExpectedVersion, evidence, token).ConfigureAwait(false);
        return new(originalId, extractionId, publication.BundleVersion, pdf.Outcome);
    }

    private sealed class AdmittedOfficeWorker(SpecDocumentIntakeAdmission admission) : ISpecOfficeExtractionWorker
    {
        public async Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken token)
        {
            var result = await admission.Office!.ExtractAsync(source, limits, token).ConfigureAwait(false);
            await admission.RequireAsync(source.Format, token).ConfigureAwait(false);
            return result;
        }
    }
}
