using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Weft.Ai.Wire;

namespace Weft.Ai;

public sealed class AiRoundClient : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly HashSet<AiTurnLease> _turns = new HashSet<AiTurnLease>();
    private readonly HashSet<AiTurnLease> _capacity = new HashSet<AiTurnLease>();
    private readonly AiProfileCatalog _profiles;
    private readonly IAiCredentialResolver _credentials;
    private readonly TimeProvider _clock;
    private bool _disposed;
    private Task? _disposal;
    public AiRoundClient(AiProfileCatalog profiles, IAiCredentialResolver credentials, TimeProvider timeProvider)
    { _profiles = profiles ?? throw AiGuard.Invalid(); _credentials = credentials ?? throw AiGuard.Invalid(); _clock = timeProvider ?? throw AiGuard.Invalid(); }
    public AiTurnOpenResult OpenTurn(AiProfileKey profile, AiTurnLimits limits, CancellationToken ct)
    {
        if (limits is null) throw AiGuard.Invalid();
        AiTurnLease turn;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AiRoundClient));
            if (ct.IsCancellationRequested) return RejectTurn(AiFailureCode.Cancelled);
            if (!profile.Valid || !_profiles.TryGet(profile, out AiProfileSpec? found) || found is null) return RejectTurn(AiFailureCode.ProfileUnavailable);
            if (!found.MappingMetadataAllowed) return RejectTurn(AiFailureCode.MetadataDisallowed);
            if (_capacity.Count >= 32) return RejectTurn(AiFailureCode.Busy);
            turn = new AiTurnLease(this, found, limits, _clock);
            _turns.Add(turn); _capacity.Add(turn);
        }
        // Initialization, upstream callbacks, and rejected-turn disposal must
        // never run while holding the client gate. Shutdown already owns the
        // published lease and joins its initialization if it races this stage.
        try { turn.Initialize(ct); }
        catch { turn.DisposeUnopened(); throw; }
        bool rejected;
        lock (_gate) rejected = _disposed || ct.IsCancellationRequested || turn.Cancelled;
        if (rejected) { turn.DisposeUnopened(); return RejectTurn(AiFailureCode.Cancelled); }
        return new TurnOpened(turn);
    }
    private static TurnRejected RejectTurn(AiFailureCode code) => new TurnRejected(AiFailure.Create(code, AiDispatchState.NotAttempted));
    internal void CompleteDisposal(AiTurnLease turn, TaskCompletionSource completion)
    {
        lock (_gate)
        {
            _capacity.Remove(turn); _turns.Remove(turn);
            // A shutdown snapshot cannot observe a missing lease while its
            // original disposal task is still pending, and an awaiting caller
            // cannot race the corresponding capacity bookkeeping.
            completion.TrySetResult();
        }
    }
    public ValueTask<AiRoundOpenResult> OpenRoundAsync(AiTurnLease turn, AiRoundRequest request, CancellationToken ct)
    {
        if (turn is null || request is null || !turn.OwnedBy(this)) throw AiGuard.Invalid();
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(AiRoundClient));
        AiProfileSpec profile = turn.Profile;
        if (ct.IsCancellationRequested || turn.Cancelled || turn.Remaining <= TimeSpan.Zero)
            return new ValueTask<AiRoundOpenResult>(RejectRound(turn, turn.Remaining <= TimeSpan.Zero ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled, false));
        if (request.UsesTools && (profile.Capabilities & AiCapability.FunctionTools) == 0)
            return new ValueTask<AiRoundOpenResult>(RejectRound(turn, AiFailureCode.CapabilityUnavailable, false));
        byte[] bytes;
        try { bytes = AiRequestWriter.Write(profile, request); }
        catch (AiProtocolException exception) { return new ValueTask<AiRoundOpenResult>(RejectRound(turn, exception.Code, false)); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return new ValueTask<AiRoundOpenResult>(RejectRound(turn, AiFailureCode.InvalidRequest, false)); }
        int ordinal; AiFailureCode refused; AiTurnLease.Opening? opening;
        try
        {
            if (!turn.TryBegin(request.RoundId, bytes.Length, out ordinal, out refused, out opening))
            { Array.Clear(bytes, 0, bytes.Length); return new ValueTask<AiRoundOpenResult>(RejectRound(turn, refused, false)); }
        }
        catch { Array.Clear(bytes, 0, bytes.Length); throw; }
        Task<AiRoundOpenResult> original = OpenOwnedAsync(turn, request, profile, bytes, ordinal, ct, opening!);
        turn.PublishOpening(opening!, original);
        return new ValueTask<AiRoundOpenResult>(original);
    }
    private async Task<AiRoundOpenResult> OpenOwnedAsync(AiTurnLease turn, AiRoundRequest request, AiProfileSpec profile,
        byte[] bytes, int ordinal, CancellationToken ct, AiTurnLease.Opening opening)
    {
        AiCancellation? cancellation = null, headerCancellation = null;
        AiBearerSecret? secret = null;
        HttpClient? client = null;
        SocketsHttpHandler? handler = null;
        HttpRequestMessage? message = null;
        HttpResponseMessage? response = null;
        AiRoundStream? stream = null;
        bool attempted = false, handedOff = false, headersComplete = false;
        try
        {
            cancellation = new AiCancellation(turn.DisposalSettled);
            cancellation.Arm(turn.Token, ct, clock: _clock, idleDeadline: true);
            CancellationToken token = cancellation.Token; token.ThrowIfCancellationRequested();
            if (profile.Authentication == AiAuthentication.Bearer)
            {
                AiCredentialResult credential = await ResolveCredentialAsync(profile.CredentialReference!, turn, token, opening!).ConfigureAwait(false);
                if (credential is not CredentialFound found) return RejectRound(turn, AiFailureCode.CredentialUnavailable, false);
                secret = found.Secret; opening!.ClaimCredential();
            }
            token.ThrowIfCancellationRequested();
            TimeSpan remaining = turn.Remaining;
            if (remaining <= TimeSpan.Zero) return RejectRound(turn, AiFailureCode.DeadlineExceeded, false);
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                Credentials = null,
                PreAuthenticate = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.Zero,
                ConnectTimeout = Min(remaining, TimeSpan.FromSeconds(5)),
                MaxConnectionsPerServer = 1,
                MaxResponseHeadersLength = 16,
                MaxResponseDrainSize = 0,
                ResponseDrainTimeout = TimeSpan.Zero,
                ActivityHeadersPropagator = null,
                ConnectCallback = new AiConnectionGuard(profile.Endpoint).ConnectAsync
            };
            client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            message = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(bytes) };
            message.Headers.ConnectionClose = true;
            message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(profile.Dialect == AiDialect.ChatCompletionsSseV1 ? "text/event-stream" : "application/x-ndjson"));
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (secret is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.HeaderValue());
            headerCancellation = new AiCancellation(turn.DisposalSettled);
            headerCancellation.Arm(token, duration: Min(remaining, TimeSpan.FromSeconds(15)), clock: _clock);
            attempted = true;
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerCancellation.Token).ConfigureAwait(false);
            headersComplete = true;
            if (response.StatusCode != HttpStatusCode.OK) return RejectRound(turn, StatusFailure(response.StatusCode), true);
            string expected = profile.Dialect == AiDialect.ChatCompletionsSseV1 ? "text/event-stream" : "application/x-ndjson";
            if (!StringComparer.OrdinalIgnoreCase.Equals(response.Content.Headers.ContentType?.MediaType, expected)
                || !IdentityEncoding(response))
                return RejectRound(turn, AiFailureCode.UnsupportedResponse, true);
            string? charset = response.Content.Headers.ContentType?.CharSet;
            if (charset is not null && !StringComparer.OrdinalIgnoreCase.Equals(charset.Trim('"'), "utf-8"))
                return RejectRound(turn, AiFailureCode.UnsupportedResponse, true);
            if (response.Content.Headers.ContentLength is > AiCeilings.RoundWireBytes)
                return RejectRound(turn, AiFailureCode.BudgetExceeded, true);
            stream = new AiRoundStream(turn, request.RoundId, client, message, response, secret, cancellation, bytes);
            if (!turn.Attach(stream)) return RejectRound(turn, turn.CancellationCode, true);
            handedOff = true;
            return new RoundOpened(stream, ordinal);
        }
        catch (TimeoutException)
        { return RejectRound(turn, AiFailureCode.DeadlineExceeded, attempted); }
        catch (OperationCanceledException)
        { return RejectRound(turn, turn.Cancelled || ct.IsCancellationRequested ? turn.CancellationCode : AiFailureCode.DeadlineExceeded, attempted); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return RejectRound(turn, attempted ? AiFailureCode.TransportFailure : AiFailureCode.CredentialUnavailable, attempted); }
        finally
        {
            if (stream is not null && !handedOff)
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { opening!.Retain(stream); }
            }
            else if (stream is null)
            {
                opening!.Dispose(response); opening.Dispose(message); opening.Dispose(client);
                if (client is null) opening.Dispose(handler);
                opening.Dispose(secret);
                Array.Clear(bytes, 0, bytes.Length);
            }
            // A resolver may still register against its token until its original
            // task settles. Keep the cancellation owner alive through that join.
            await opening!.SettleCredentialAsync().ConfigureAwait(false);
            if (stream is null) await opening.DisposeCancellationAsync(cancellation).ConfigureAwait(false);
            await opening!.DisposeCancellationAsync(headerCancellation, cancel: !headersComplete).ConfigureAwait(false);
            turn.EndOpening(opening);
            if (!opening.Cleaned) { turn.Close(); throw AiTurnLease.CleanupFailure(); }
        }
    }
    private async ValueTask<AiCredentialResult> ResolveCredentialAsync(string reference, AiTurnLease turn, CancellationToken ct, AiTurnLease.Opening opening)
    {
        Task<AiCredentialResult> pending = _credentials.ResolveAsync(reference, ct).AsTask();
        opening.TrackCredential(pending);
        return await pending.WaitAsync(turn.Remaining, _clock, ct).ConfigureAwait(false);
    }
    private static bool IdentityEncoding(HttpResponseMessage response)
    {
        foreach (string encoding in response.Content.Headers.ContentEncoding)
            if (!StringComparer.OrdinalIgnoreCase.Equals(encoding, "identity")) return false;
        return true;
    }
    internal static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static AiFailureCode StatusFailure(HttpStatusCode status)
    {
        int code = (int)status;
        return code is 401 or 403 ? AiFailureCode.ProviderAuthentication : code == 429 ? AiFailureCode.ProviderRateLimited
            : code is >= 500 and <= 599 ? AiFailureCode.ProviderUnavailable : AiFailureCode.ProviderRejected;
    }
    private static RoundRejected RejectRound(AiTurnLease turn, AiFailureCode code, bool attempted)
    { turn.Close(); return new RoundRejected(AiFailure.Create(code, attempted ? AiDispatchState.Attempted : AiDispatchState.NotAttempted)); }
    public ValueTask DisposeAsync()
    {
        AiTurnLease[] turns; TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            _disposed = true; turns = new AiTurnLease[_turns.Count]; _turns.CopyTo(turns);
        }
        _ = DisposeCoreAsync(turns, completion);
        return new ValueTask(completion.Task);
    }
    private static async Task DisposeCoreAsync(AiTurnLease[] turns, TaskCompletionSource completion)
    {
        bool failed = false;
        foreach (AiTurnLease turn in turns)
        {
            try { await turn.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        }
        if (failed) completion.TrySetException(AiTurnLease.CleanupFailure());
        else completion.TrySetResult();
    }
    public override string ToString() => nameof(AiRoundClient);
}
