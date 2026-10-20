using System.Net.Sockets;
using Weft.BrowserQualification;

namespace Weft.Tests;

public sealed partial class AuthBrowserHttpsQualificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPrivateControlFailedWriteKeepsClosureAndFailsQualification(bool combinedFailures)
    {
        ControlFailureCase? item = null; bool primary = false, cleanup = false;
        try
        {
            item = new(combinedFailures); item.Open(); await item.SendStopAsync(); await item.HandlerReadyAsync();
            QualificationWire.Require(item.Parent.KnownSettled && item.DisconnectBeforeReply());
            Task confirmation = item.Own(item.Parent.ConfirmControlAsync(item.Server));
            QualificationWire.Require(!confirmation.IsCompleted && !item.Server.ControlSettled);
            item.ReleaseReply(); await confirmation.WaitAsync(item.Budget);
            // A disconnect alone is NOT an exercised vector: demand the original
            // actual server write's categorical failure and the exact one-request count.
            QualificationWire.Require(item.Server.ControlSettled && !item.Server.Settled && item.Server.Failed == 1 && item.Server.FailedWrites == 1
                && item.Parent.ControlFailed && item.Parent.KnownSettled);
            int releases = 0;
            Task release = item.Own(item.Parent.ReleaseAsync(() => item.Parent.ConfirmControlAsync(item.Server), () =>
            {
                releases++; return combinedFailures ? Task.FromException(new QualificationFailure()) : Task.CompletedTask;
            }));
            bool releaseFailed = false;
            try { await release.WaitAsync(item.Budget); } catch { releaseFailed = true; }
            QualificationWire.Require(release.IsCompleted && releaseFailed == combinedFailures && releases == 1
                && ReferenceEquals(release, item.Parent.ReleaseAsync(() => throw new QualificationFailure(), () => throw new QualificationFailure())));
            var result = item.Parent.Outcome(combinedFailures, releaseFailed);
            QualificationWire.Require(result.Failed && result.ControlFailed && result.PrimaryFailed == combinedFailures
                && result.CounterFailed == combinedFailures && result.CleanupFailed == combinedFailures);
            // Close validation still fails; no new close/reply owner or retry exists.
            QualificationWire.Require(item.Server.CloseAsync().IsFaulted && ReferenceEquals(item.Server.CloseAsync(), item.Server.CloseAsync()));
        }
        catch { primary = true; }
        finally
        {
            if (item is not null)
            {
                try { await item.CloseAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                catch { cleanup = true; ControlFailureCase.Retain(item); }
            }
        }
        QualificationWire.Require(!primary && !cleanup);
    }

    [Fact]
    public async Task PendingOriginalControlWorkerDoesNotAuthorizeDependentCleanup()
    {
        ControlFailureCase? item = null; bool primary = false, cleanup = false;
        try
        {
            item = new(false); item.Open(); await item.SendStopAsync(); await item.HandlerReadyAsync();
            Task confirmation = item.Own(item.Parent.ConfirmControlAsync(item.Server)); int releases = 0;
            Task release = item.Own(item.Parent.ReleaseAsync(() => item.Parent.ConfirmControlAsync(item.Server),
                () => { releases++; return Task.CompletedTask; }));
            QualificationWire.Require(item.Parent.KnownSettled && !item.Server.ControlSettled && !confirmation.IsCompleted && !release.IsCompleted && releases == 0);
            // Cancel only an observation of the still-owned original. It must not
            // convert that observation into positive channel/task settlement.
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool observationCanceled = false;
            Task observedWait = item.Own(confirmation.WaitAsync(canceled.Token));
            try { await observedWait; } catch (OperationCanceledException) { observationCanceled = true; }
            QualificationWire.Require(observationCanceled && !item.Server.ControlSettled && !release.IsCompleted && releases == 0);
            item.ReleaseReply(); await release.WaitAsync(item.Budget);
            QualificationWire.Require(item.Server.ControlSettled && item.Server.Settled && item.Server.Failed == 0 && item.Server.FailedWrites == 0 && releases == 1);
        }
        catch { primary = true; }
        finally
        {
            if (item is not null)
            {
                try { await item.CloseAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                catch { cleanup = true; ControlFailureCase.Retain(item); }
            }
        }
        QualificationWire.Require(!primary && !cleanup);
    }

    // Real Unix IPC component fixture using the ACTUAL source-linked private server.
    // Only the proxy/probe StopTransport owner inputs are inert; no HTTPS, provider,
    // process, DB, key ring or NSS resource is claimed to have been qualified here.
    private sealed class ControlFailureCase
    {
        private static readonly List<ControlFailureCase> retained = [];
        private string root = "", path = "";
        private readonly bool combined;
        private readonly CancellationTokenSource deadline = new();
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> originals = [];
        private readonly List<Task<bool>> observations = [];
        private Socket? client;
        private NetworkStream? stream;
        private Task? close;
        private bool disposedClient, clientDisposalFailed, rootAttempted;
        private QualificationSocketServer? server;
        internal QualificationSocketServer Server => server ?? throw new QualificationFailure();
        internal QualificationParentStopOwner Parent { get; } = new();
        internal CancellationToken Budget => deadline.Token;
        internal ControlFailureCase(bool combined)
        {
            this.combined = combined; Own(ready.Task); Own(reply.Task);
        }
        internal void Open()
        {
            QualificationWire.Require(server is null && !rootAttempted); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            root = Path.Combine(Path.GetTempPath(), "wq-" + Guid.NewGuid().ToString("N")[..8]); path = Path.Combine(root, "c.sock");
            QualificationWire.Require(path.Length < 100); rootAttempted = true; Directory.CreateDirectory(root, ServerHostFiles.PrivateDirectory);
            server = new(root, path, async request =>
            {
                QualificationWire.Require(request.Operation == "stop");
                var result = await Parent.StopAsync(() => Task.CompletedTask, () => true,
                    () => Task.FromResult<QualificationReply?>(QualificationReply.Ok() with { ClosedObservation = true, ProcessGeneration = 1, AdmissionRefusals = combined ? 1 : 0 }),
                    () => true, () => QualificationBackstopPolicy.Validate(null, combined ? 1 : 0, 0));
                QualificationWire.Require(result.KnownSettled && result.Report is not null && result.CounterValidation == !combined);
                ready.TrySetResult(); await reply.Task;
                return result.Report! with { CounterValidation = result.CounterValidation };
            });
        }
        internal T Own<T>(T original) where T : Task
        {
            originals.Add(original); observations.Add(Observe(original)); return original;
        }
        private static async Task<bool> Observe(Task original) { try { await original; return true; } catch { return false; } }
        internal async Task SendStopAsync()
        {
            client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await Own(client.ConnectAsync(new UnixDomainSocketEndPoint(path), Budget).AsTask());
            stream = new(client, ownsSocket: false);
            await Own(QualificationWire.WriteAsync(stream, QualificationRequest.Control("stop"), Budget));
            client.Shutdown(SocketShutdown.Send);
        }
        internal Task HandlerReadyAsync() => Own(ready.Task.WaitAsync(Budget));
        internal bool DisconnectBeforeReply()
        {
            bool shutdown = true;
            try { QualificationWire.Require(client is not null); client.Shutdown(SocketShutdown.Both); }
            catch { shutdown = false; }
            DisposeClient(); return shutdown && !clientDisposalFailed;
        }
        private void DisposeClient()
        {
            if (disposedClient) return;
            disposedClient = true;
            try { stream?.Dispose(); } catch { clientDisposalFailed = true; }
            try { client?.Dispose(); } catch { clientDisposalFailed = true; }
        }
        internal void ReleaseReply() => reply.TrySetResult();
        internal Task CloseAsync() => close ??= CloseCoreAsync();
        private async Task CloseCoreAsync()
        {
            // Always release/cancel the ORIGINAL gates. Never synthesize ready=true.
            reply.TrySetResult(); ready.TrySetCanceled(); DisposeClient();
            if (server is null)
            {
                // An uncertain constructor/root attempt is retained, not adopted as
                // an absent/settled server. The already allocated fixture owns it.
                QualificationWire.Require(!rootAttempted && !clientDisposalFailed);
                await Task.WhenAll(observations); deadline.Dispose(); return;
            }
            Task serverClose = Own(Server.CloseAsync());
            await Observe(serverClose); // failure is retained by server + parent categories
            int index = 0;
            while (index < observations.Count)
            {
                var batch = observations.Skip(index).ToArray(); index += batch.Length; await Task.WhenAll(batch);
            }
            // No deletion on an observation timeout, canceled/failed original join,
            // or incomplete owner. Failed validation is not itself an unjoined task.
            QualificationWire.Require(Server.ControlSettled && serverClose.IsCompleted && originals.All(original => original.IsCompleted) && !clientDisposalFailed);
            deadline.Dispose(); Directory.Delete(root, recursive: true);
        }
        internal static void Retain(ControlFailureCase owner) { lock (retained) retained.Add(owner); }
    }
}
