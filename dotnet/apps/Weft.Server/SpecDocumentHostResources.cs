using Weft.Application;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Server;

/// <summary>One host-owned, measured worker pair. A mode setting requests admission; it never proves qualification.</summary>
internal sealed class SpecDocumentHostResources : IHostedService, IAsyncDisposable
{
    private readonly SpecDocumentHostSettings? settings;
    private readonly Func<SpecDocumentHostSettings, CancellationToken, Task<Workers?>> open;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private volatile Workers? workers;
    private readonly List<IAsyncDisposable> owned = [];
    private bool started;
    private volatile bool stopped;
    internal string Status { get; private set; } = "disabled";

    internal SpecDocumentHostResources(SpecDocumentHostSettings? settings)
    { this.settings = settings; open = OpenAsync; }
    // Controlled fixture port only; production DI always selects the constructor above.
    internal SpecDocumentHostResources(SpecDocumentHostSettings? settings, Func<SpecDocumentHostSettings, CancellationToken, Task<Workers?>> open)
    { this.settings = settings; this.open = open; }
    internal SpecDocumentHostResources(SpecDocumentHostSettings? settings,
        Func<SpecDocumentHostSettings, Action<IAsyncDisposable>, CancellationToken, Task<Workers?>> open)
    { this.settings = settings; this.open = (value, token) => open(value, owned.Add, token); }

    public async Task StartAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (started || stopped) return;
            started = true;
            if (settings is null) return;
            Status = "unavailable";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            Workers? candidate = null;
            try
            {
                candidate = await open(settings, deadline.Token).ConfigureAwait(false);
                if (candidate is not null) owned.Add(candidate);
                deadline.Token.ThrowIfCancellationRequested();
                if (candidate is null || !candidate.Available) return;
                workers = candidate; candidate = null; Status = "admitted";
            }
            catch (WorkerDirectoryProbe.CleanupFailure failure)
            {
                // The probe may fail before any runner can be returned or registered.
                owned.Add(failure);
                throw;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SpecDocumentInstallation.AdmissionRefusedException or PlatformNotSupportedException or
                DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
            { /* Optional workers remain unavailable; diagnostics never contain paths or native messages. */ }
            // Cleanup is outside the optional-admission catches. Failed owners remain retained for every later join.
            finally { if (workers is null) await DisposeOwnedAsync().ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }

    internal SpecDocumentIntakeAdmission Bind(PostgresSpecBundleSession session) => Bind(session.AdmitDocumentEvidenceAsync);
    internal SpecDocumentIntakeAdmission Bind(Func<CancellationToken, Task<bool>> schema46)
    {
        var current = workers;
        if (settings is null || current is null || !current.Available || stopped) return SpecDocumentIntakeAdmission.Unavailable;
        bool Available() => !stopped && ReferenceEquals(workers, current) && current.Available;
        return new(schema46, current.Office, settings.OfficeLimits, Available, current.Pdf, settings.PdfProfile, Available);
    }

    public async Task StopAsync(CancellationToken token)
    {
        stopped = true;
        // Cancellation stops startup and active operations through the owned runners' disposal paths.
        await shutdown.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            stopped = true; Status = "stopped";
            await DisposeOwnedAsync().ConfigureAwait(false);
            workers = null;
        }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task DisposeOwnedAsync()
    {
        List<Exception> failures = [];
        for (var index = owned.Count - 1; index >= 0; index--)
        {
            try
            {
                await owned[index].DisposeAsync().ConfigureAwait(false);
                owned.RemoveAt(index);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0)
            throw new AggregateException("Document worker cleanup was not confirmed.", failures);
    }

    private async Task<Workers?> OpenAsync(SpecDocumentHostSettings settings, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return null;
        SpecDocumentInstallation.Validate(settings, token);
        var office = await QualifiedOfficeProcessWorker.CreateOwnedAsync(settings.OfficeAssembly, settings.OfficeBuild, settings.OfficeRuntime,
            settings.OfficeLauncher, settings.OfficeWorkRoot, settings.DotnetRoot, owned.Add, token).ConfigureAwait(false);
        if (!office.Available) return null;
        var pdf = await QualifiedPdfProcessWorker.CreateForQualificationAsync(settings.PdfExecutable, settings.PdfWorkRoot,
            settings.PdfProfile, settings.PdfProfile.Digest, token, owned.Add).ConfigureAwait(false);
        if (!pdf.Available) return null;
        // Runners were registered before their first probe and remain individually owned by this host.
        return new Workers(office, pdf, () => office.Available && pdf.Available, () => ValueTask.CompletedTask);
    }

    internal sealed class Workers(ISpecOfficeExtractionWorker office, ISpecPdfExtractionWorker pdf, Func<bool> available, Func<ValueTask> dispose) : IAsyncDisposable
    {
        internal ISpecOfficeExtractionWorker Office { get; } = office;
        internal ISpecPdfExtractionWorker Pdf { get; } = pdf;
        internal bool Available => available();
        public ValueTask DisposeAsync() => dispose();
    }
}
