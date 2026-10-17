using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

internal sealed class AuthHttpFailure(string category) : Exception(category)
{ public override string ToString() => nameof(AuthHttpFailure); }

internal sealed class AuthHttpGate
{
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<Task> Original = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool IgnoreCancellation;
    internal async Task HoldAsync(CancellationToken token)
    {
        using var registration = token.Register(() => CancellationObserved.TrySetResult());
        Entered.TrySetResult();
        if (IgnoreCancellation) await Release.Task;
        else await Release.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }
    public override string ToString() => nameof(AuthHttpGate);
}

internal enum AuthHttpDirect { None, Read, Begin, Complete, Logout, Forget, BorrowedObserve, BorrowedBegin, BorrowedIdentity }
internal enum AuthHttpTaskRole { Unspecified, ActualReader, GatedReader, PublicAdmission, Module, HttpSend }
internal enum AuthHttpFinalDrainPhase { Login, PrimeStatus, Configure, Send, BoundaryWait, BoundaryAssert, CallerCancel, CallerAssert, DisposalBegin, DisposalAssert, AdmissionJoin, ResponseJoin, DisposalJoin, FinalAssert, CookieAssert }
internal sealed class AuthHttpRecord
{
    internal int Id, BodyReads, FormReads, SessionReads, Resolves, BindingReads, Renews, Revokes, Creates, CatalogReads, MetadataWrites;
    internal int ReaderGateAtRead;
    internal bool BorrowedIdentityResolved, ExpectedDirectFailure;
    internal Exception? ClosedError;
    internal bool Entered, Canceled, Unexpected, CaptureIdentity, LoseRevokeAck, LoseCreateAck, StartHeadersAfterCreate;
    internal bool MissingBodyDetector, PositiveBodyDetector, BreakProfileAtActionCookie, PriorDeniedRevoke, PriorObserve, PoisonAmbient;
    internal bool ReaderUnavailable, ReaderIntegrity, BindingUnavailable;
    internal AuthHttpDirect Direct;
    internal AuthHttpGate? ReaderGate, RevokeGate, CreateGate, BindingGate, CatalogGate;
    internal HttpContext? Context;
    internal CancellationTokenSource? CallerCancellation, PriorAdmissionCancellation;
    internal Task? ModuleOperation;
    internal Task? ActualReaderOperation, GatedReaderOperation;
    internal Task<ServerSessionRequestAdmission>? PriorAdmissionOperation;
    internal TaskStatus? AdmissionAtRequestEnd;
    internal readonly TaskCompletionSource ModuleStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource RequestCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ConcurrentQueue<string> Order = new();
    internal readonly ConcurrentQueue<ClaimsPrincipal> ActionCookieUsers = new();
    internal ServerSessionRequestIdentity? Identity;
    internal ServerSessionKey? CreatedKey;
    internal ServerSessionFacts? CreatedFacts;
    internal ExternalIdentityKey? ResolvedKey;
    internal BrowserAuthenticationSession? SessionResult;
    internal EntraLoginChallenge? ChallengeResult;
    internal ServerSessionIdentityFailureKind? PriorFailure, Failure, InjectedReaderFailure;
    internal ServerSessionRequestAdmission? PriorObservation;
    internal BrowserAuthenticationFailureKind? AuthenticationFailure;
    internal void ReleaseAll()
    { ReaderGate?.Release.TrySetResult(); RevokeGate?.Release.TrySetResult(); CreateGate?.Release.TrySetResult(); BindingGate?.Release.TrySetResult(); CatalogGate?.Release.TrySetResult(); }
    public override string ToString() => nameof(AuthHttpRecord);
}

internal sealed class AuthHttpOwnedTask(Task task, bool cancellation, bool failure, AuthHttpTaskRole role)
{
    internal Task Task { get; } = task;
    internal bool AllowsCancellation { get; } = cancellation;
    internal bool AllowsFailure { get; } = failure;
    internal AuthHttpTaskRole Role { get; } = role;
    public override string ToString() => nameof(AuthHttpOwnedTask);
}

internal sealed class AuthHttpJar
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    internal IReadOnlyList<string> Names => values.Keys.ToArray();
    internal string Header => string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value));
    internal string? Get(string key) => values.GetValueOrDefault(key);
    internal void Set(string key, string? value) { if (value is null) values.Remove(key); else values[key] = value; }
    internal AuthHttpJar Copy() { var copy = new AuthHttpJar(); foreach (var pair in values) copy.values.Add(pair.Key, pair.Value); return copy; }
    internal void Apply(AuthHttpReply reply)
    {
        if (!reply.Headers.TryGetValue("Set-Cookie", out var headers)) return;
        foreach (var cookie in SetCookieHeaderValue.ParseList(headers.ToList()))
        {
            var name = cookie.Name.ToString(); var value = cookie.Value.ToString();
            if (value.Length == 0 || cookie.MaxAge is { } age && age <= TimeSpan.Zero) values.Remove(name);
            else values[name] = value;
        }
    }
    public override string ToString() => nameof(AuthHttpJar);
}

internal sealed class AuthHttpReply(HttpStatusCode status, IReadOnlyDictionary<string, string[]> headers, byte[] body, AuthHttpRecord record)
{
    internal HttpStatusCode Status { get; } = status;
    internal IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
    internal byte[] Body { get; } = body;
    internal AuthHttpRecord Record { get; } = record;
    internal JsonElement Json() { using var document = JsonDocument.Parse(Body); return document.RootElement.Clone(); }
    public override string ToString() => nameof(AuthHttpReply);
}

internal sealed class AuthHttpStatus(AuthHttpReply reply)
{
    internal AuthHttpReply Reply { get; } = reply;
    internal string Action => Reply.Json().GetProperty("authActionRequestToken").GetString()!;
    internal string? Epoch => Reply.Json().GetProperty("requestToken").GetString();
    internal string? Fingerprint => Reply.Json().GetProperty("identityContext").GetString();
    internal string State => Reply.Json().GetProperty("state").GetString()!;
    public override string ToString() => nameof(AuthHttpStatus);
}

internal sealed class AuthHttpFlow(AuthHttpJar jar, string callback)
{
    internal AuthHttpJar Jar { get; } = jar;
    internal string Callback { get; } = callback;
    public override string ToString() => nameof(AuthHttpFlow);
}

internal sealed class AuthHttpBody(Stream inner, AuthHttpRecord record) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { Interlocked.Increment(ref record.BodyReads); return await inner.ReadAsync(buffer, token); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) { Interlocked.Increment(ref record.BodyReads); throw new AuthHttpFailure("The auth boundary attempted a synchronous body read."); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class AuthHttpForm(AuthHttpRecord record) : IFormFeature
{
    public bool HasFormContentType => false;
    public IFormCollection? Form { get { Interlocked.Increment(ref record.FormReads); throw new AuthHttpFailure("The auth boundary accessed a form."); } set => throw new AuthHttpFailure("The auth boundary replaced a form."); }
    public IFormCollection ReadForm() { Interlocked.Increment(ref record.FormReads); throw new AuthHttpFailure("The auth boundary read a form."); }
    public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
    { Interlocked.Increment(ref record.FormReads); return Task.FromException<IFormCollection>(new AuthHttpFailure("The auth boundary read a form.")); }
}
internal sealed class AuthHttpBodyDetector : IHttpRequestBodyDetectionFeature
{ public bool CanHaveBody => true; }

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpCookieFeature(IRequestCookiesFeature inner, HttpContext context, AuthHttpRecord record, DurableSessionKeyProfile profile) : IRequestCookiesFeature
{
    public IRequestCookieCollection Cookies { get => new AuthHttpCookies(inner.Cookies, context, record, profile); set => inner.Cookies = value; }
}
[SupportedOSPlatform("linux")]
internal sealed class AuthHttpCookies(IRequestCookieCollection inner, HttpContext context, AuthHttpRecord record, DurableSessionKeyProfile profile) : IRequestCookieCollection
{
    public int Count => inner.Count;
    public ICollection<string> Keys => inner.Keys;
    public string? this[string key]
    {
        get
        {
            if (key == AuthHttpFixture.ActionCookie)
            {
                record.ActionCookieUsers.Enqueue(context.User);
                if (record.BreakProfileAtActionCookie) { record.BreakProfileAtActionCookie = false; profile.Dispose(); }
            }
            return inner[key];
        }
    }
    public bool ContainsKey(string key) => inner.ContainsKey(key);
    public bool TryGetValue(string key, [NotNullWhen(true)] out string? value) { value = this[key]!; return inner.ContainsKey(key); }
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
