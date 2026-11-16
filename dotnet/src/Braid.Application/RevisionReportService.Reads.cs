using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application;

public sealed partial class RevisionReportService
{
    public async Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid? projectionDerivationId = null,
        CancellationToken cancellationToken = default)
    {
        Input(actor, scope, operation, projectionDerivationId, true);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadRequestAsync(async token =>
        {
            var source = await SourceAsync(actor, scope, null, token);
            if (source is null) return HiddenRead();
            if (!_readGate.Wait(0)) return RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.AdmissionBusy);
            try
            {
                var selection = SelectCurrent(source.Source, operation);
                if (selection is null)
                    return RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.ProfileUnavailable);
                var snapshot = await CurrentAsync(actor, selection.Value.Key, token);
                if (snapshot is null) return HiddenRead();
                if (snapshot.SelectedResult is { } selected)
                    return await ReadSelectedAsync(actor, selected, projectionDerivationId, token);
                if (await SourceAsync(actor, scope, snapshot.Key.Source, token) is null) return HiddenRead();
                return RevisionReportReadResult.ForSnapshot(snapshot);
            }
            finally { _readGate.Release(); }
        }, cancellationToken);
    }

    public async Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, Guid? projectionDerivationId = null,
        CancellationToken cancellationToken = default)
    {
        Input(actor, scope, operation, projectionDerivationId, true);
        if (derivationId == Guid.Empty)
            throw new ArgumentException("A retained derivation identifier cannot be empty.", nameof(derivationId));
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadRequestAsync(async token =>
        {
            var source = await SourceAsync(actor, scope, null, token);
            if (source is null) return HiddenRead();
            if (!_readGate.Wait(0)) return RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.AdmissionBusy);
            try
            {
                var retained = await RetainedAsync(actor, scope, operation, derivationId, token);
                if (retained?.Result is not { } selected) return HiddenRead();
                if (retained.Key.Source != source.Source) throw Inconsistent();
                return await ReadSelectedAsync(actor, selected, projectionDerivationId, token);
            }
            finally { _readGate.Release(); }
        }, cancellationToken);
    }

    private static async Task<RevisionReportReadResult> ReadRequestAsync(
        Func<CancellationToken, Task<RevisionReportReadResult>> read, CancellationToken caller)
    {
        using var deadline = Deadline(caller);
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new RevisionReportServiceException(RevisionReportFailureCategory.TimedOut);
        }
    }

    private async Task<RevisionReportReadResult> ReadAdmittedAsync(ActorContext actor,
        RevisionReportResult selected, CancellationToken token)
    {
        // Ensure and acknowledgement delivery use the same singleton read capacity as GET.
        return await ReadRequestAsync(async readToken =>
        {
            if (await SourceAsync(actor, selected.Key.Scope, selected.Key.Source, readToken) is null) return HiddenRead();
            if (!_readGate.Wait(0)) return RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.AdmissionBusy);
            try { return await ReadSelectedAsync(actor, selected, null, readToken); }
            finally { _readGate.Release(); }
        }, token);
    }

    private async Task<RevisionReportReadResult> ReadSelectedAsync(ActorContext actor,
        RevisionReportResult selected, Guid? projectionDerivationId, CancellationToken token)
    {
        var stored = await ReadStoredAsync(actor, selected, token);
        if (stored.Report is null && stored.Failure is null) return HiddenRead();
        var report = stored.Report;
        var failure = stored.Failure;
        if (report is VerifiedValidationReport validation && projectionDerivationId is { } projectionId)
        {
            var projection = await RetainedAsync(actor, selected.Key.Scope, RevisionReportOperation.Projection, projectionId, token);
            if (projection?.Result is not { } projectionMetadata || projectionMetadata.Key.Source != selected.Key.Source ||
                projectionMetadata.Key.Profile != selected.Key.Profile)
                failure = RevisionReportReadOutcome.SelectedProjectionUnavailable;
            else
            {
                var selectedProjection = await ReadStoredAsync(actor, projectionMetadata, token);
                if (selectedProjection.Report is null && selectedProjection.Failure is null) return HiddenRead();
                if (selectedProjection.Report is not VerifiedProjectionReport verifiedProjection)
                    failure = RevisionReportReadOutcome.SelectedProjectionUnavailable;
                else
                {
                    try { report = Link(validation, verifiedProjection, projectionId, token); }
                    catch (RevisionReportValidationException error)
                    {
                        failure = error.Failure == RevisionReportValidationFailure.ResourceLimit
                            ? RevisionReportReadOutcome.ResourceLimit : RevisionReportReadOutcome.SelectedProjectionUnavailable;
                    }
                }
            }
        }
        if (failure is null && report is not null)
        {
            try { BoundResponse(report, token); }
            catch (RevisionReportValidationException error)
            {
                failure = error.Failure == RevisionReportValidationFailure.ResourceLimit
                    ? RevisionReportReadOutcome.ResourceLimit : RevisionReportReadOutcome.StorageIntegrity;
            }
        }
        var finalSource = await SourceAsync(actor, selected.Key.Scope, selected.Key.Source, token);
        if (finalSource is null) return HiddenRead();
        return failure is { } refusal
            ? RevisionReportReadResult.ForReadFailure(finalSource, selected, refusal)
            : RevisionReportReadResult.ForAvailable(finalSource, report ?? throw Inconsistent());
    }

    private async Task<StoredReport> ReadStoredAsync(ActorContext actor, RevisionReportResult selected,
        CancellationToken token)
    {
        try
        {
            _storage.ValidateDerivedReference(selected.Key, selected.DerivedObject, _readCaps);
            if (await SourceAsync(actor, selected.Key.Scope, selected.Key.Source, token) is null) return new(null, null);
            var store = Store();
            // Resolving a lazy filesystem store can itself inspect the owned root. Recheck again
            // before the separate byte read; no observation grants access through both operations.
            if (await SourceAsync(actor, selected.Key.Scope, selected.Key.Source, token) is null) return new(null, null);
            var returned = await ReadObjectAsync(store, selected.DerivedObject, token);
            var caps = RevisionReportWorkflowValue.EffectiveReadCaps(selected.Key, _readCaps);
            var owned = OwnBytes(returned, selected.DerivedObject, caps.StoredObjectByteLimit, token);
            return new(_codec.Decode(selected, owned, _readCaps, token), null);
        }
        catch (PrivateObjectStorageException error) { return new(null, ReadFailure(error)); }
        catch (RevisionReportCodecException error) { return new(null, ReadFailure(error)); }
        catch (RevisionReportValidationException error)
        {
            return new(null, error.Failure == RevisionReportValidationFailure.ResourceLimit
                ? RevisionReportReadOutcome.ResourceLimit : RevisionReportReadOutcome.StorageIntegrity);
        }
    }

    private VerifiedValidationReport Link(VerifiedValidationReport validation, VerifiedProjectionReport projection,
        Guid projectionId, CancellationToken token)
    {
        if (projection.Metadata.DerivationId != projectionId || projection.Metadata.Key.Source != validation.Metadata.Key.Source ||
            projection.Metadata.Key.Profile != validation.Metadata.Key.Profile) throw Inconsistent();
        var links = ProjectionOriginLinks.FromVerifiedReport(projection, _readCaps, token);
        var source = validation.Report.Diagnostics;
        var diagnostics = new ValidationDiagnosticV1[source.Count];
        for (var index = 0; index < diagnostics.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var diagnostic = source[index];
            diagnostics[index] = diagnostic with { ProjectionLocator = links.Find(diagnostic.NativeLocator, token) };
        }
        token.ThrowIfCancellationRequested();
        var linked = validation.Report with { Diagnostics = Array.AsReadOnly(diagnostics) };
        return VerifiedValidationReport.Create(validation.Metadata, linked, _readCaps, projectionId, token);
    }

    private void BoundResponse(VerifiedRevisionReport report, CancellationToken token)
    {
        var maximum = Math.Min(report.Metadata.Key.Policy.ResponseByteLimit, _readCaps.ResponseByteLimit);
        switch (report)
        {
            case VerifiedProjectionReport projection:
                _ = RevisionReportResponseSerializer.Serialize(projection.Report, maximum, token);
                break;
            case VerifiedValidationReport validation:
                _ = RevisionReportResponseSerializer.Serialize(validation.Report, maximum, token);
                break;
            default: throw Inconsistent();
        }
        token.ThrowIfCancellationRequested();
    }

    private sealed record StoredReport(VerifiedRevisionReport? Report, RevisionReportReadOutcome? Failure);
}
