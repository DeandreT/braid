using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Braid.Server;

/// <summary>Admits the configured socket/proxy transport before static, auth, API, or body work.</summary>
internal static class SessionHostTransport
{
    internal static void ValidateConfiguration(IConfiguration configuration, Func<string, string?> read)
    {
        if (configuration.GetSection("Kestrel").GetChildren().Any() ||
            configuration["urls"] is not null || configuration["http_ports"] is not null ||
            configuration["https_ports"] is not null || configuration["preferHostingUrls"] is not null ||
            configuration["ForwardedHeaders_Enabled"] is not (null or "false") ||
            new[] { "ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
                "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS" }.Any(name => read(name) is not null) ||
            read("ASPNETCORE_FORWARDEDHEADERS_ENABLED") is not (null or "false"))
            throw ServerHostOptions.Failure();
    }

    internal static void Configure(KestrelServerOptions server, ServerHostOptions options, SessionHostResources resources)
    {
        server.Configure();
        server.AddServerHeader = false;
        server.Limits.MaxRequestLineSize = 65_536;
        server.Listen(options.Listener.Address, options.Listener.Port, listener =>
        {
            listener.Protocols = HttpProtocols.Http1;
            if (!options.Proxy) listener.UseHttps(HandshakeOptions(
                resources.TlsCertificateContext ?? throw ServerHostOptions.Failure()));
        });
    }

    internal static TlsHandshakeCallbackOptions HandshakeOptions(SslStreamCertificateContext certificate) => new()
    {
        // The ordinary certificate overload constructs another context with offline:false before
        // OnAuthenticate. Only this callback overload accepts our already-built offline context.
        // Leave Kestrel's handshake timeout and HTTP/1 ALPN handling intact. No client identity
        // is requested, and no permissive certificate-validation callback is installed.
        OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = certificate,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        })
    };

    internal static void Use(IApplicationBuilder app, ServerHostOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (!RawAllowed(context, options)) { Reject(context, 403); return; }
            await next(context);
        });
        if (options.Proxy)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
                ForwardLimit = 1,
                RequireHeaderSymmetry = true
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            forwarded.KnownProxies.Add(options.KnownProxy!);
            forwarded.AllowedHosts.Clear();
            var publicHost = new Uri(options.Origin).Host;
            forwarded.AllowedHosts.Add(publicHost.Contains(':') && !publicHost.StartsWith('[')
                ? "[" + publicHost + "]" : publicHost);
            app.UseForwardedHeaders(forwarded);
        }
        app.Use(async (context, next) =>
        {
            if (context.Request.Scheme != "https" || context.Request.Host.Value != new Uri(options.Origin).Authority ||
                context.Request.PathBase.HasValue) { Reject(context, 403); return; }
            context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
    }

    private static bool RawAllowed(HttpContext context, ServerHostOptions options)
    {
        if (context.Request.Protocol is not ("HTTP/1.1" or "HTTP/1.0") || context.Request.PathBase.HasValue ||
            context.Connection.LocalIpAddress is not { } local || !local.Equals(options.Listener.Address) ||
            context.Connection.LocalPort != options.Listener.Port) return false;
        var expectedAuthority = new Uri(options.Origin).Authority;
        foreach (var header in context.Request.Headers.Keys)
            if (header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) &&
                    (!options.Proxy || !header.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase) &&
                        !header.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase) &&
                        !header.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase)))
                return false;
        if (context.Request.Headers.Host.Count != 1 || context.Request.Host.Value != expectedAuthority) return false;
        if (!options.Proxy) return context.Request.IsHttps;
        if (context.Request.IsHttps || context.Connection.RemoteIpAddress is not { } peer ||
            !peer.Equals(options.KnownProxy)) return false;
        var forValue = context.Request.Headers["X-Forwarded-For"];
        var proto = context.Request.Headers["X-Forwarded-Proto"];
        var host = context.Request.Headers["X-Forwarded-Host"];
        return forValue.Count == 1 && forValue[0] is { } text && IPAddress.TryParse(text, out var client) &&
            text == client.ToString() && proto.Count == 1 && proto[0] == "https" &&
            host.Count == 1 && host[0] == expectedAuthority;
    }

    internal static void Reject(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentLength = 0;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
