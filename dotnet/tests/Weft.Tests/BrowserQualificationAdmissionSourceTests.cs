using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weft.BrowserQualification;
using Weft.HostPrivacyProbe;

namespace Weft.Tests;

// Authored component tests, not browser/network evidence.
public sealed partial class AuthBrowserHttpsQualificationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("mismatch")]
    [InlineData("stale")]
    [InlineData("preflight")]
    [InlineData("frame")]
    [InlineData("worker")]
    [InlineData("worklet")]
    [InlineData("upgrade")]
    [InlineData("sealed")]
    [InlineData("detached-no-proof")]
    public async Task QualificationHostRefusesBeforeProductDelivery(string vector)
    {
        var input = InertAdmissionInput(); var owner = new BrowserQualificationAdmission(input);
        var request = InertAdmission(); owner.Register(request);
        var context = InertContext(request); int delivered = 0;
        if (vector is "missing" or "detached-no-proof") context.Request.Headers.Remove(BrowserQualificationAdmission.Header);
        if (vector == "duplicate") context.Request.Headers[BrowserQualificationAdmission.Header] = new Microsoft.Extensions.Primitives.StringValues([request.Proof, request.Proof]);
        if (vector == "mismatch") context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/examples";
        if (vector == "stale") { owner = new(input with { Generation = 2 }); }
        if (vector == "preflight") context.Request.Method = "OPTIONS";
        if (vector == "frame") context.Request.Headers["Sec-Fetch-Dest"] = "iframe";
        if (vector == "worker") context.Request.Headers["Sec-Fetch-Dest"] = "worker";
        if (vector == "worklet") context.Request.Headers["Sec-Fetch-Dest"] = "audioworklet";
        if (vector == "upgrade") context.Request.Headers.Upgrade = "websocket";
        if (vector == "sealed") owner.Close();
        await owner.InvokeAsync(context, _ => { delivered++; return Task.CompletedTask; });
        Assert.True(delivered == 0 && context.Response.StatusCode == 403 && !context.Request.Headers.ContainsKey(BrowserQualificationAdmission.Header));
    }
    [Fact]
    public async Task QualificationAdmissionIsStrippedAndSingleUse()
    {
        var owner = new BrowserQualificationAdmission(InertAdmissionInput()); var request = InertAdmission(); owner.Register(request);
        int delivered = 0; bool stripped = false;
        await owner.InvokeAsync(InertContext(request), context => { stripped = !context.Request.Headers.ContainsKey(BrowserQualificationAdmission.Header); delivered++; return Task.CompletedTask; });
        var replay = InertContext(request);
        await owner.InvokeAsync(replay, _ => { delivered++; return Task.CompletedTask; });
        Assert.True(stripped && delivered == 1 && replay.Response.StatusCode == 403);
    }
    [Theory]
    [InlineData("authority")]
    [InlineData("private-ip")]
    [InlineData("ipv6")]
    [InlineData("port")]
    [InlineData("duplicate-host")]
    [InlineData("absolute-http")]
    [InlineData("body")]
    [InlineData("credentials")]
    [InlineData("upgrade")]
    public void QualificationProxyRefusesAuthorityAndFramingBeforeUpstream(string category)
    {
        const string target = "CONNECT 127.0.0.1:43123 HTTP/1.1\r\nHost: 127.0.0.1:43123\r\n";
        string header = category switch
        {
            "authority" => "CONNECT elsewhere.invalid:443 HTTP/1.1\r\nHost: elsewhere.invalid:443\r\n\r\n",
            "private-ip" => "CONNECT 10.0.0.1:43123 HTTP/1.1\r\nHost: 10.0.0.1:43123\r\n\r\n",
            "ipv6" => "CONNECT [::1]:43123 HTTP/1.1\r\nHost: [::1]:43123\r\n\r\n",
            "port" => "CONNECT 127.0.0.1:43124 HTTP/1.1\r\nHost: 127.0.0.1:43124\r\n\r\n",
            "duplicate-host" => target + "Host: 127.0.0.1:43123\r\n\r\n",
            "absolute-http" => "GET http://elsewhere.invalid/ HTTP/1.1\r\nHost: elsewhere.invalid\r\n\r\n",
            "body" => target + "Content-Length: 1\r\n\r\n",
            "credentials" => target + "Proxy-Authorization: inert\r\n\r\n",
            "upgrade" => target + "Upgrade: websocket\r\n\r\n",
            _ => throw new QualificationFailure()
        };
        Assert.True(!BrowserQualificationConnectProxy.Permits(System.Text.Encoding.ASCII.GetBytes(header), 43123));
    }
    [Fact]
    public void QualificationProxyAllowsOnlyItsLiteralConnectAuthority()
    {
        Assert.True(BrowserQualificationConnectProxy.Permits("CONNECT 127.0.0.1:43123 HTTP/1.1\r\nHost: 127.0.0.1:43123\r\n\r\n"u8, 43123));
    }
    [Theory]
    [InlineData("external")]
    [InlineData("wrongPort")]
    [InlineData("relativeAPI")]
    [InlineData("duplicate")]
    [InlineData("refresh")]
    [InlineData("upgrade")]
    [InlineData("not303")]
    [InlineData("innerMutates")]
    public async Task QualificationResponseGateInspectsFinalInnerHeaders(string vector)
    {
        var input = InertAdmissionInput(); var owner = new BrowserQualificationAdmission(input);
        const string path = "/api/auth/callback/entra?code=inert-source-code&state=inert-source-state";
        owner.ArmCallback(input.Origin + path);
        var request = InertAdmission() with { RawTarget = path, Site = "cross-site" }; owner.Register(request);
        var context = InertContext(request); var feature = new StartingFeature(); var lifetime = new LifetimeFeature();
        context.Features.Set<IHttpResponseFeature>(feature); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        await owner.InvokeAsync(context, inner =>
        {
            inner.Response.StatusCode = 303; inner.Response.Headers.Location = "/";
            if (vector == "external") inner.Response.Headers.Location = "https://elsewhere.invalid/";
            if (vector == "wrongPort") inner.Response.Headers.Location = "https://127.0.0.1:43124/";
            if (vector == "relativeAPI") inner.Response.Headers.Location = "/api/auth/session";
            if (vector == "duplicate") inner.Response.Headers.Location = new Microsoft.Extensions.Primitives.StringValues(["/", "/"]);
            if (vector == "refresh") inner.Response.Headers["Refresh"] = "0;url=/";
            if (vector == "upgrade") inner.Response.Headers.Upgrade = "websocket";
            if (vector == "not303") inner.Response.StatusCode = 307;
            if (vector == "innerMutates") inner.Response.OnStarting(() => { inner.Response.Headers.Location = "/api/auth/logout"; return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        bool refused = false; try { await feature.FireAsync(); } catch (QualificationFailure) { refused = true; }
        Assert.True(refused && lifetime.Aborted && !feature.HasStarted);
    }
    [Fact]
    public async Task QualificationResponseGateKeepsGenuineRootRedirectAndCookieUnmodified()
    {
        var input = InertAdmissionInput(); var owner = new BrowserQualificationAdmission(input);
        const string path = "/api/auth/callback/entra?code=inert-source-code&state=inert-source-state";
        owner.ArmCallback(input.Origin + path);
        var request = InertAdmission() with { RawTarget = path, Site = "cross-site" }; owner.Register(request);
        var context = InertContext(request); var feature = new StartingFeature(); context.Features.Set<IHttpResponseFeature>(feature);
        await owner.InvokeAsync(context, inner =>
        {
            inner.Response.StatusCode = 303; inner.Response.Headers.Location = "/";
            inner.Response.Headers.SetCookie = "InertSource=example; Secure; HttpOnly"; return Task.CompletedTask;
        });
        await feature.FireAsync();
        Assert.True(context.Response.StatusCode == 303 && context.Response.Headers.Location == "/"
            && context.Response.Headers.SetCookie == "InertSource=example; Secure; HttpOnly" && feature.HasStarted);
    }
    [Theory]
    [InlineData(null, "no-store")]
    [InlineData("no-cache, no-store", "no-cache, no-store")]
    public async Task QualificationResponseGateKeepsProductCachePolicyAndOtherwiseDisablesStorage(string? product, string expected)
    {
        var owner = new BrowserQualificationAdmission(InertAdmissionInput()); var request = InertAdmission(); owner.Register(request);
        var context = InertContext(request); var feature = new StartingFeature(); context.Features.Set<IHttpResponseFeature>(feature);
        await owner.InvokeAsync(context, inner =>
        {
            if (product is not null) inner.Response.Headers.CacheControl = product;
            return Task.CompletedTask;
        });
        await feature.FireAsync();
        Assert.True(feature.HasStarted && context.Response.Headers.CacheControl == expected);
    }
    // Component harness only: the real Kestrel LIFO implementation is separately
    // retained as proof-dir/HttpProtocol.cs; these callbacks perform no HTTP I/O.
    private sealed class StartingFeature : IHttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> callbacks = new();
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }
        public void OnStarting(Func<object, Task> callback, object state) => callbacks.Push((callback, state));
        public void OnCompleted(Func<object, Task> callback, object state) { }
        internal async Task FireAsync() { while (callbacks.TryPop(out var entry)) await entry.Callback(entry.State); HasStarted = true; }
    }
    private sealed class LifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        internal bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
    private static QualificationHostInput InertAdmissionInput() => new()
    {
        Version = 1,
        Generation = 1,
        Root = "inert",
        SocketPath = "inert",
        Origin = "https://127.0.0.1:43123",
        WebRoot = "inert",
        Assets = new() { ["index.html"] = "inert" },
        Organization = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ApplicationName = "inert",
        KeyDirectory = "inert",
        DataCertificateFile = "inert",
        DataKeyFile = "inert",
        TlsCertificateFile = "inert",
        TlsKeyFile = "inert",
        ReaderConnection = "inert",
        WriterConnection = "inert",
        IdentityConnection = "inert",
        CatalogConnection = "inert"
    };
    private static TransportAdmission InertAdmission() => new()
    {
        Proof = new string('a', 64),
        Generation = 1,
        RawTarget = "/auth-qualification",
        Method = "GET",
        Destination = "document",
        Mode = "navigate",
        Site = "none",
        Origin = null,
        Negative = null
    };
    private static DefaultHttpContext InertContext(TransportAdmission request)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new("127.0.0.1", 43123);
        context.Request.Method = request.Method; context.Request.Path = request.RawTarget;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = request.RawTarget;
        context.Request.Headers[BrowserQualificationAdmission.Header] = request.Proof;
        context.Request.Headers["Sec-Fetch-Dest"] = request.Destination; context.Request.Headers["Sec-Fetch-Mode"] = request.Mode;
        context.Request.Headers["Sec-Fetch-Site"] = request.Site;
        if (request.Origin is not null) context.Request.Headers.Origin = request.Origin;
        return context;
    }
}
