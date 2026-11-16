using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Braid.Application;
using Braid.Domain;
using Braid.Identity;

namespace Braid.Tests;

public sealed class ServerSessionTicketStoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task AllEightContextlessOverloadsRefuseBeforePorts(int operation)
    {
        using var h = new SessionStoreTestHarness();
        var ticket = new AuthenticationTicket(new(), ServerSessionSchemes.Session);
        ITicketStore store = h.Store;
        var failure = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => operation switch
        {
            0 => store.StoreAsync(ticket),
            1 => store.StoreAsync(ticket, CancellationToken.None),
            2 => store.RetrieveAsync("malformed"),
            3 => store.RetrieveAsync("malformed", CancellationToken.None),
            4 => store.RenewAsync("malformed", ticket),
            5 => store.RenewAsync("malformed", ticket, CancellationToken.None),
            6 => store.RemoveAsync("malformed"),
            _ => store.RemoveAsync("malformed", CancellationToken.None)
        });
        Assert.Equal(ServerSessionIdentityFailureKind.ContextRequired, failure.Kind);
        h.AssertNoPorts();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TokenContextlessOverloadsPreserveCanceledCause(int operation)
    {
        using var h = new SessionStoreTestHarness();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var ticket = new AuthenticationTicket(new(), ServerSessionSchemes.Session);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            0 => h.Store.StoreAsync(ticket, source.Token),
            1 => h.Store.RetrieveAsync("malformed", source.Token),
            2 => h.Store.RenewAsync("malformed", ticket, source.Token),
            _ => h.Store.RemoveAsync("malformed", source.Token)
        });
        Assert.True(failure.CancellationToken == source.Token, "Cancellation cause changed.");
        h.AssertNoPorts();
    }

    [Fact]
    public async Task FourContextualOperationsUseExactDurableKeyAndFreshCanonicalTickets()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var issue = scope.CopySignInTicket();
        var key = await h.Store.StoreAsync(issue, context, default);
        var first = await h.Store.RetrieveAsync(key, context, default);
        var second = await h.Store.RetrieveAsync(key, context, default);
        Assert.True(first is not null && second is not null && !ReferenceEquals(first, second),
            "Retrieval did not return fresh canonical graphs.");
        Assert.True(issue.Properties.IssuedUtc == ServerSessionCanonicalTicket.ToFrameworkTime(h.Ports.Row!.Lifetime.IssuedAtUtc),
            "Created framework issue time was not the database projection.");
        Assert.True(first!.Properties.ExpiresUtc == ServerSessionCanonicalTicket.ToFrameworkTime(h.Ports.Row!.Lifetime.AbsoluteExpiresAtUtc),
            "Framework expiry projection changed.");
        await h.Store.RenewAsync(key, first, context, default);
        await h.Store.RemoveAsync(key, context, default);
        Assert.True(h.Ports.Creates == 1 && h.Ports.Renews == 1 && h.Ports.Revokes == 1 && h.Ports.Reads == 3,
            "Unexpected port dispatch count.");
        Assert.True(h.Ports.Row!.RevokedAtUtc is not null, "Revocation did not retain its tombstone.");
    }

    [Fact]
    public async Task MalformedHandlesNeverReachPortsAndRenewRefuses()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        Assert.True((await h.Store.RetrieveAsync(" bs1.invalid ", context, default)) is null,
            "Malformed key produced identity.");
        await h.Store.RemoveAsync("bs1.invalid", context, default);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() =>
            h.Store.RenewAsync("bs1.invalid", new(new(), ServerSessionSchemes.Session), context, default));
        Assert.Equal(ServerSessionIdentityFailureKind.UnsupportedFormat, error.Kind);
        h.AssertNoPorts();
    }

    [Fact]
    public async Task FailedAndDisposedScopesRemainSpentForTheRequest()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        h.Ports.Create = (_, _) => throw new InvalidOperationException("private-store-detail");
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() =>
            h.Store.StoreAsync(scope.CopySignInTicket(), context, default));
        Assert.Equal(ServerSessionIdentityFailureKind.Unavailable, error.Kind);
        Assert.True(error.InnerException is null && !error.Message.Contains("private-store-detail", StringComparison.Ordinal),
            "Private failure detail escaped.");
        scope.Dispose();
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
        Assert.Throws<ServerSessionIdentityException>(() => scope.CopySignInTicket());
        Assert.True(h.Ports.Creates == 1, "Failed issuance was retried.");
    }

    [Fact]
    public async Task PreCanceledStoreSpendsItsScopeWithoutWriting()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var ticket = scope.CopySignInTicket();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Store.StoreAsync(ticket, context, source.Token));
        Assert.True(error.CancellationToken == source.Token, "Caller cancellation cause changed.");
        await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Store.StoreAsync(ticket, context, default));
        h.AssertNoPorts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingScopeCanceledStorePreservesOriginalCauseAndKeepsRefusal(bool requestCancellation)
    {
        using var h = new SessionStoreTestHarness();
        using var source = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        if (requestCancellation)
            context.RequestAborted = source.Token;
        var before = SnapshotKeyFiles(h.KeyDirectory);
        (string Name, byte[] Bytes)[] after = [];
        try
        {
            source.Cancel();
            var ticket = new AuthenticationTicket(new(), ServerSessionSchemes.Issue);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                h.Store.StoreAsync(ticket, context, requestCancellation ? default : source.Token));
            Assert.True(error.CancellationToken == source.Token, "Missing-scope cancellation lost original cause.");
            h.AssertNoPorts();
            Assert.True(h.Ports.Row is null, "A missing scope allocated session metadata.");
            var refusal = Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
            Assert.Equal(ServerSessionIdentityFailureKind.UntrustedIssuance, refusal.Kind);
            after = SnapshotKeyFiles(h.KeyDirectory);
            Assert.True(before.Length == after.Length && before.Zip(after).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
                pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes)),
                "Canceled missing-scope call changed durable key material.");
            h.AssertNoPorts();
        }
        finally
        {
            foreach (var row in before) CryptographicOperations.ZeroMemory(row.Bytes);
            foreach (var row in after) CryptographicOperations.ZeroMemory(row.Bytes);
        }
    }

    private static (string Name, byte[] Bytes)[] SnapshotKeyFiles(string directory) =>
        Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path))).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LinkedCancellationReturnsOriginalCallerRequestOrIssuanceToken(int cause)
    {
        using var h = new SessionStoreTestHarness();
        using var caller = new CancellationTokenSource();
        using var request = new CancellationTokenSource();
        using var issuance = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = request.Token };
        using var scope = h.Begin(context, issuance.Token);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ports.Create = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable fixture branch.");
        };
        var pending = h.Store.StoreAsync(scope.CopySignInTicket(), context, caller.Token);
        var checkedOutcome = false;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var selected = cause == 0 ? caller : cause == 1 ? request : issuance;
            selected.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(error.CancellationToken == selected.Token, "Linked cancellation lost original cause.");
            Assert.True(h.Ports.Creates == 1, "Cancellation caused a retry.");
            checkedOutcome = true;
        }
        finally
        {
            caller.Cancel();
            request.Cancel();
            issuance.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch when (!checkedOutcome) { } // Retain the primary assertion while settling the owned operation.
        }
    }

    [Fact]
    public async Task ConcurrentFirstReservationsHaveOneWinnerAndDoNotTouchPorts()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var start = new ManualResetEventSlim();
        async Task<TrustedSessionIssuance?> Attempt() => await Task.Run(() =>
        {
            start.Wait();
            try { return h.Begin(context); }
            catch (ServerSessionIdentityException error) when (error.Kind == ServerSessionIdentityFailureKind.UntrustedIssuance)
            { return null; }
        });
        var first = Attempt();
        var second = Attempt();
        start.Set();
        var scopes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.True(scopes.Count(x => x is not null) == 1, "Concurrent issuance had multiple winners."); }
        finally { foreach (var scope in scopes) scope?.Dispose(); }
        h.AssertNoPorts();
    }

    [Fact]
    public async Task DifferentStoresShareFeatureInstallationWithoutLosingEntries()
    {
        using var h = new SessionStoreTestHarness();
        var other = new ServerSessionTicketStore(h.Profile, h.Ports, h.Ports);
        var context = new DefaultHttpContext();
        using var start = new ManualResetEventSlim();
        var a = Task.Run(() => { start.Wait(); return h.Begin(context); });
        var b = Task.Run(() =>
        {
            start.Wait(); return other.BeginTrustedIssuance(context,
            SessionStoreTestHarness.Binding, 1, SessionStoreTestHarness.Owner, SessionStoreTestHarness.Deadline, default);
        });
        start.Set();
        var scopes = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(scopes.All(x => x.CopySignInTicket().AuthenticationScheme == ServerSessionSchemes.Issue),
                "A store lost its request reservation.");
            Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
            Assert.Throws<ServerSessionIdentityException>(() => other.BeginTrustedIssuance(context,
                SessionStoreTestHarness.Binding, 1, SessionStoreTestHarness.Owner, SessionStoreTestHarness.Deadline, default));
        }
        finally { foreach (var scope in scopes) scope.Dispose(); }
        h.AssertNoPorts();
    }

    [Fact]
    public void ReinitializedContextAdmitsNewRequestButNeverTheStaleScope()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var old = h.Begin(context);
        context.Uninitialize();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        context.Initialize(features);
        Assert.Throws<ServerSessionIdentityException>(() => old.CopySignInTicket());
        using var next = h.Begin(context);
        Assert.True(next.CopySignInTicket().AuthenticationScheme == ServerSessionSchemes.Issue,
            "Fresh feature collection did not reset request admission.");
        old.Dispose();
        Assert.True(next.CopySignInTicket().AuthenticationScheme == ServerSessionSchemes.Issue,
            "Old disposal changed a later request.");
        h.AssertNoPorts();
    }

    [Fact]
    public void ClearedPrivateFeatureStartsAnotherRequestWithoutAContextIdentityRegistry()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var old = h.Begin(context);
        var extras = context.Features.Where(x => x.Key.Assembly == typeof(ServerSessionTicketStore).Assembly)
            .Select(x => x.Key).ToArray();
        Assert.True(extras.Length == 1, "Private request feature was not installed.");
        foreach (var key in extras) context.Features[key] = null;
        Assert.Throws<ServerSessionIdentityException>(() => old.CopySignInTicket());
        using var next = h.Begin(context);
        Assert.True(next.CopySignInTicket().AuthenticationScheme == ServerSessionSchemes.Issue,
            "Cleared request state still used a context-lifetime registry.");
        h.AssertNoPorts();
    }

    [Fact]
    public async Task CopiedTicketCannotCarryIssuanceToAnotherContext()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var ticket = scope.CopySignInTicket();
        var foreign = new DefaultHttpContext();
        foreign.Items["trusted-session"] = scope;
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Store.StoreAsync(ticket, foreign, default));
        Assert.Equal(ServerSessionIdentityFailureKind.UntrustedIssuance, error.Kind);
        h.AssertNoPorts();
        await h.Store.StoreAsync(ticket, context, default);
        Assert.True(h.Ports.Creates == 1, "Foreign context spent or copied original authority.");
    }

    [Fact]
    public async Task UnsupportedClaimIsRejectedBeforeAnyWriteAndSpendsScope()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var ticket = scope.CopySignInTicket();
        ((System.Security.Claims.ClaimsIdentity)ticket.Principal.Identity!).AddClaim(new("role", "private-role"));
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Store.StoreAsync(ticket, context, default));
        Assert.Equal(ServerSessionIdentityFailureKind.UnsupportedFormat, error.Kind);
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
        h.AssertNoPorts();
    }

    [Fact]
    public async Task OrdinarySchemeCannotMintEvenInsideTrustedScope()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var issue = scope.CopySignInTicket();
        var ordinary = new AuthenticationTicket(issue.Principal, issue.Properties, ServerSessionSchemes.Session);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Store.StoreAsync(ordinary, context, default));
        Assert.Equal(ServerSessionIdentityFailureKind.UntrustedIssuance, error.Kind);
        h.AssertNoPorts();
    }

    [Fact]
    public async Task CallerTicketMutationDuringWriteCannotReturnAnIssuedHandle()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        using var scope = h.Begin(context);
        var issue = scope.CopySignInTicket();
        h.Ports.Create = (intent, _) =>
        {
            issue.Properties.Items["private-provider-item"] = "private-provider-value";
            h.Ports.Row = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                ServerSessionPolicy.InitialLifetime(SessionStoreTestHarness.Now, intent.VerifiedLoginDeadlineUtc)!,
                1, intent.CopyProtectedTicket());
            return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, h.Ports.Row));
        };
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Store.StoreAsync(issue, context, default));
        Assert.Equal(ServerSessionIdentityFailureKind.UnsupportedFormat, error.Kind);
        Assert.True(h.Ports.Creates == 1 && h.Ports.Row is not null, "Post-dispatch refusal invented rollback/retry.");
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
    }

    [Fact]
    public void CleanDisposalRetainsTheRequestTombstoneAndRedactsDefaultDiagnostics()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        var scope = h.Begin(context);
        Assert.True(string.Equals(scope.ToString(), nameof(TrustedSessionIssuance), StringComparison.Ordinal),
            "Scope diagnostic was not redacted.");
        Assert.True(string.Equals(System.Text.Json.JsonSerializer.Serialize(scope), "{}", StringComparison.Ordinal),
            "Scope default serialization exposed state.");
        scope.Dispose();
        Assert.Throws<ServerSessionIdentityException>(() => scope.CopySignInTicket());
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
        h.AssertNoPorts();
    }

    [Fact]
    public async Task TinyDatabaseLifetimeMayHaveEqualFrameworkDatesWithoutExtension()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        var deadline = SessionStoreTestHarness.Now.AddTicks(10);
        using var scope = h.Store.BeginTrustedIssuance(context, SessionStoreTestHarness.Binding, 1,
            SessionStoreTestHarness.Owner, deadline, default);
        var issue = scope.CopySignInTicket();
        var key = await h.Store.StoreAsync(issue, context, default);
        var ticket = await h.Store.RetrieveAsync(key, context, default);
        Assert.True(ticket is not null && ticket.Properties.IssuedUtc == ticket.Properties.ExpiresUtc,
            "Tiny lifetime projection was extended or rejected.");
        Assert.True(h.Ports.Row!.Lifetime.AbsoluteExpiresAtUtc == deadline &&
            h.Ports.Row.Lifetime.IssuedAtUtc == SessionStoreTestHarness.Now,
            "Framework projection changed private database precision.");
        h.Ports.Renew = (_, _, _) => Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(
            ServerSessionWriteOutcome.Unchanged, h.Ports.Row));
        await h.Store.RenewAsync(key, ticket!, context, default);
        Assert.True(h.Ports.Renews == 1, "Projected equality prevented coherent no-op renewal.");
    }

    [Fact]
    public void StartedHeadersRefuseAndLatchIssuance()
    {
        using var h = new SessionStoreTestHarness();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
        context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        Assert.Throws<ServerSessionIdentityException>(() => h.Begin(context));
        h.AssertNoPorts();
    }

    private sealed class StartedResponse : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}

// Synthetic ports establish adapter dispatch/coherence only, never PostgreSQL acknowledgement.
internal sealed class SessionStoreTestHarness : IDisposable
{
    internal static readonly Guid Organization = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid Binding = Guid.Parse("20000000-0000-0000-0000-000000000002");
    internal static readonly PrincipalId Owner = new(Guid.Parse("30000000-0000-0000-0000-000000000003"));
    internal static readonly DateTimeOffset Now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);

    /// <summary>The current UTC time as a PostgreSQL instant: whole microseconds. Session facts and verified
    /// deadlines refuse sub-microsecond clock ticks, so recording session ports must never pass them.</summary>
    internal static DateTimeOffset DatabaseNow()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % 10));
    }
    internal static readonly DateTimeOffset Deadline = Now.AddMinutes(60);
    private readonly string directory;
    private readonly RSA rsa;
    private readonly X509Certificate2 certificate;

    internal SessionStoreTestHarness()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The selected durable test profile requires Linux.");
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache",
            "braid-session-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=braid-session-unit", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificate = request.CreateSelfSigned(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Profile = DurableSessionKeyProfile.BootstrapEmpty(new(Organization, "braid-session-unit", directory),
            certificate, Array.Empty<X509Certificate2>(), new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Ports = new();
        Store = new(Profile, Ports, Ports);
        Codec = new(Profile);
        Lifecycle = new(Codec, Ports, Ports);
    }

    internal string KeyDirectory => directory;
    internal DurableSessionKeyProfile Profile { get; }
    internal CountingSessionPorts Ports { get; }
    internal ServerSessionTicketStore Store { get; }
    internal ServerSessionTicketCodec Codec { get; }
    internal ServerSessionLifecycle Lifecycle { get; }

    internal TrustedSessionIssuance Begin(HttpContext context, CancellationToken token = default) =>
        Store.BeginTrustedIssuance(context, Binding, 1, Owner, Deadline, token);

    internal async Task<(ServerSessionHandle Handle, ServerSessionEnvelope Envelope, ServerSessionFacts Facts)> Create()
    {
        var handle = ServerSessionHandle.Create();
        var envelope = new ServerSessionEnvelope(new(Organization, handle.Digest), Binding, 1, Owner,
            Guid.Parse("40000000-0000-0000-0000-000000000004"), Deadline);
        var facts = await Lifecycle.CreateAsync(handle, envelope,
            ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, envelope.Epoch), default);
        return (handle, envelope, facts);
    }

    internal void AssertNoPorts() => Assert.True(Ports.Reads == 0 && Ports.Creates == 0 && Ports.Renews == 0 && Ports.Revokes == 0,
        "An inadmissible call reached a persistence port.");

    public void Dispose()
    {
        Profile.Dispose();
        certificate.Dispose();
        rsa.Dispose();
        Directory.Delete(directory, true);
    }

    internal sealed class CountingSessionPorts : IServerSessionReader, IServerSessionWriter
    {
        private int reads, creates, renews, revokes;
        internal int Reads => reads;
        internal int Creates => creates;
        internal int Renews => renews;
        internal int Revokes => revokes;
        internal ServerSessionFacts? Row { get; set; }
        internal bool BindingEnabled { get; set; } = true;
        internal bool PrincipalActive { get; set; } = true;
        internal Func<ServerSessionCreateIntent, CancellationToken, Task<ServerSessionWriteResult>>? Create { get; set; }
        internal Func<ServerSessionKey, long, CancellationToken, Task<ServerSessionWriteResult>>? Renew { get; set; }
        internal Func<ServerSessionKey, CancellationToken, Task<ServerSessionWriteResult>>? Revoke { get; set; }
        internal Func<ServerSessionKey, CancellationToken, Task<CurrentServerSession?>>? Observe { get; set; }

        public Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            cancellationToken.ThrowIfCancellationRequested();
            if (Observe is { } observe) return observe(key, cancellationToken);
            return Task.FromResult(ServerSessionPolicy.ObserveCurrent(key, Now, Row, Row is null ? null :
                new(Row.BindingId, Row.BindingVersion, Owner, BindingEnabled, PrincipalActive)));
        }

        public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref creates);
            cancellationToken.ThrowIfCancellationRequested();
            if (Create is { } create) return create(intent, cancellationToken);
            Row = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                ServerSessionPolicy.InitialLifetime(Now, intent.VerifiedLoginDeadlineUtc)!, 1, intent.CopyProtectedTicket());
            return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, Row));
        }

        public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref renews);
            cancellationToken.ThrowIfCancellationRequested();
            if (Renew is { } renew) return renew(key, expectedVersion, cancellationToken);
            var row = Row!;
            Row = new(key, row.BindingId, row.BindingVersion, row.LoginEpoch,
                new(row.Lifetime.IssuedAtUtc, row.Lifetime.IdleExpiresAtUtc.AddMinutes(1), row.Lifetime.AbsoluteExpiresAtUtc),
                expectedVersion + 1, row.CopyProtectedTicket());
            return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Renewed, Row));
        }

        public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref revokes);
            cancellationToken.ThrowIfCancellationRequested();
            if (Revoke is { } revoke) return revoke(key, cancellationToken);
            if (Row is not { } row) return Task.FromResult(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.Unchanged));
            Row = new(key, row.BindingId, row.BindingVersion, row.LoginEpoch, row.Lifetime,
                row.Version + 1, row.CopyProtectedTicket(), Now.AddMinutes(1));
            return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Revoked, Row));
        }
    }
}
