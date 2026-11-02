using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Weft.BrowserQualification;

namespace Weft.HostPrivacyProbe;

// Earliest nonshipped probe-only transport gate. Never establishes identity/session.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial class BrowserQualificationAdmission
{
    internal const string Header = "X-Weft-Qualification-Admission";
    internal const string Csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    private readonly object sync = new();
    private readonly Dictionary<string, TransportAdmission> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    private readonly QualificationHostInput input;
    private bool closed;
    private string? callback, replay;
    private bool returnArmed;
    private string callbackReturn = "/";
    private int deliveries, refusals;
    internal BrowserQualificationAdmission(QualificationHostInput input) { this.input = input; QualificationWire.Require(input.Generation > 0); }
    internal void ArmCallback(string value)
    {
        var url = new Uri(value);
        QualificationWire.Require(url.GetLeftPart(UriPartial.Authority) == input.Origin && url.AbsolutePath == "/api/auth/callback/entra" && url.Fragment.Length == 0);
        lock (sync) { QualificationWire.Require(!closed && callback is null && !returnArmed); callback = url.PathAndQuery; replay = callback; }
    }
    internal void ArmReplay()
    { lock (sync) { QualificationWire.Require(!closed && replay is not null && callback is null && !returnArmed); callback = replay; } }
    internal void Register(TransportAdmission request)
    {
        request.Validate();
        lock (sync)
        {
            QualificationWire.Require(!closed && request.Generation == input.Generation && pending.Count < 64 && used.Count < 1024
                && !used.Contains(request.Proof) && !pending.ContainsKey(request.Proof) && Allowed(request));
            pending.Add(request.Proof, request);
        }
    }
    private bool Allowed(TransportAdmission item)
    {
        string path = item.RawTarget;
        if (item.Destination == "document") return item.Method == "GET" && item.Mode == "navigate" && item.Origin is null && item.Negative is null
            && (path == callback || path == callbackReturn && returnArmed || new[] { "/auth-qualification", "/sign-in", "/", "/catalog", $"/catalog/organizations/{input.Organization:D}/projects" }.Contains(path));
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (item.Destination != "empty" || item.Mode is not ("cors" or "same-origin") || item.Site != "same-origin") return false;
            if (item.Method == "POST") return item.Origin == input.Origin && path is "/api/auth/login" or "/api/auth/logout" && (item.Negative is null || path == "/api/auth/login");
            return item.Method == "GET" && item.Origin is null && item.Negative is null && new[] { "/api/auth/session", "/api/v1/catalog/context", "/api/v1/health", $"/api/v1/organizations/{input.Organization:D}/projects?limit=10" }.Contains(path);
        }
        // Module scripts and fonts are CORS-mode loads; Chromium sends the page Origin even same-origin.
        bool cors = item.Destination is "script" or "font";
        return item.Method == "GET" && item.Negative is null && item.Site == "same-origin" && input.Assets.ContainsKey(path[1..])
            && item.Destination is "script" or "style" or "image" or "font"
            && item.Mode == (cors ? "cors" : "no-cors") && item.Origin == (cors ? input.Origin : null);
    }
    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Remove even malformed/multiple/stale header values before any downstream code.
        StringValues values = context.Request.Headers[Header]; context.Request.Headers.Remove(Header);
        TransportAdmission? item = null;
        lock (sync)
        {
            if (!closed && values.Count == 1 && values[0] is { } proof && pending.Remove(proof, out item)) used.Add(proof);
        }
        bool valid = item is not null && item.Generation == input.Generation && item.RawTarget == context.Features.Get<IHttpRequestFeature>()?.RawTarget
            && item.Method == context.Request.Method && context.Request.IsHttps && "https://" + context.Request.Host.Value == input.Origin
            && !context.Request.PathBase.HasValue && context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding")
            && !context.Request.Headers.ContainsKey("Upgrade") && !context.Request.Headers.ContainsKey("Content-Type")
            && !context.Request.Headers.ContainsKey("Authorization") && !context.Request.Headers.ContainsKey("Proxy-Authorization")
            && !context.Request.Headers.ContainsKey("Refresh") && context.Request.Headers["Sec-Fetch-Dest"].Count == 1
            && context.Request.Headers["Sec-Fetch-Mode"].Count == 1 && context.Request.Headers["Sec-Fetch-Site"].Count == 1
            && context.Request.Headers["Sec-Fetch-Dest"] == item.Destination && context.Request.Headers["Sec-Fetch-Mode"] == item.Mode
            && context.Request.Headers["Sec-Fetch-Site"] == item.Site
            && (item.Origin is null ? !context.Request.Headers.ContainsKey("Origin") : context.Request.Headers.Origin.Count == 1 && context.Request.Headers.Origin == item.Origin)
            && !context.Request.Headers.Keys.Any(name => name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase));
        string? requestedReturn = null;
        if (context.Request.Headers.TryGetValue("X-Weft-Return-To", out var returnHeader))
        {
            requestedReturn = returnHeader.Count == 1 ? returnHeader[0] : null;
            valid &= item is { Method: "POST", RawTarget: "/api/auth/login" } && requestedReturn is not null
                && IsReturnTarget(requestedReturn, input.Organization);
        }
        lock (sync)
        {
            valid &= !closed && item is not null && Allowed(item);
            if (valid && item!.RawTarget == callback) callback = null;
            if (valid && item!.RawTarget == callbackReturn && returnArmed) returnArmed = false;
            if (valid) deliveries++; else refusals++;
        }
        if (!valid) { context.Response.StatusCode = 403; return; }
        var admitted = item ?? throw new QualificationFailure();
        // Registered FIRST: OnStarting is LIFO, so inspect FINAL inner headers.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            bool redirect = context.Response.StatusCode is >= 300 and <= 399;
            bool ok = !headers.ContainsKey(Header) && !headers.ContainsKey("Refresh") && !headers.ContainsKey("Upgrade") && context.Response.StatusCode != 101;
            if (admitted.Method == "POST" && admitted.RawTarget == "/api/auth/login" && context.Response.StatusCode == 200)
                lock (sync) callbackReturn = requestedReturn ?? "/";
            if (redirect)
            {
                ok &= admitted.RawTarget.StartsWith("/api/auth/callback/entra?", StringComparison.Ordinal) && admitted.Destination == "document"
                    && context.Response.StatusCode == 303 && headers.Location.Count == 1 && headers.Location[0] == callbackReturn;
                if (ok) lock (sync) { ok = !closed && !returnArmed; if (ok) returnArmed = true; }
            }
            else ok &= !headers.ContainsKey("Location");
            if (!ok) { NetworkAbort(); context.Abort(); throw new QualificationFailure(); }
            headers["Content-Security-Policy"] = Csp; headers["Referrer-Policy"] = "no-referrer";
            headers["X-Content-Type-Options"] = "nosniff";
            // Reloads then re-enter admission instead of sending unadmitted cache validators.
            if (!headers.ContainsKey("Cache-Control")) headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        NetworkResponse(context, admitted);
        await next(context);
    }
    internal static bool IsReturnTarget(string value, Guid organization) =>
        value == "/catalog" || value == $"/catalog/organizations/{organization:D}/projects";
    internal void Close() { lock (sync) { closed = true; pending.Clear(); callback = null; returnArmed = false; } }
    internal QualificationReply Counts(QualificationReply reply)
    { lock (sync) return NetworkCounts(reply with { ProductDeliveries = deliveries, AdmissionRefusals = refusals, ProofStripped = true }); }
}
