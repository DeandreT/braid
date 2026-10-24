using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Weft.Identity;

namespace Weft.Tests;

internal static class EntraProtocolFixturePolicy
{
    internal static readonly Guid Tenant = Guid.Parse("a67cb0f3-6f11-4e5e-921a-71c3b894fb32");
    internal static readonly Guid Client = Guid.Parse("594c2f40-9399-4adf-89e5-c8527b1c5de0");
    internal static readonly Guid Organization = Guid.Parse("4ab650f8-9e70-43c3-83fb-47eccbe33436");
    internal static string Issuer => $"https://login.microsoftonline.com/{Tenant:D}/v2.0";
    internal static string Discovery => $"/{Tenant:D}/v2.0/.well-known/openid-configuration";
    internal static string Authorize => $"/{Tenant:D}/oauth2/v2.0/authorize";
    internal static string Token => $"/{Tenant:D}/oauth2/v2.0/token";
    internal static string Keys => $"/{Tenant:D}/discovery/v2.0/keys";
    internal const string CommonKeys = "/common/discovery/v2.0/keys";
    internal static string TokenAddress => "https://login.microsoftonline.com" + Token;
    internal const string Callback = "/api/auth/callback/entra";
    internal const string Subject = " opaque:fixture-subject\u00e9 ";
    // Another application's pairwise subject for the same user: same tid and oid, different sub.
    internal const string AlternateSubject = "opaque:another-application-subject";
    // The signed-in user's tenant-stable object ID; the binding key is (Tenant, ObjectId), never Subject.
    internal static readonly Guid ObjectId = Guid.Parse("3d1f6b2e-8c4a-4f7e-9b5d-2a6c8e0f1b37");
    internal static Weft.Application.ExternalIdentityKey UserKey => Weft.Application.EntraUserIdentity.Key(Tenant, ObjectId);
    internal const string PrivateMarker = "weft-owned-private-protocol-marker";
    internal const string TraceHeader = "X-Weft-116-Request";
    internal static void Check(bool condition, string category)
    { if (!condition) throw new EntraProtocolFixtureFailure(category); }
}

internal sealed class EntraProtocolFixtureFailure(string category) : Exception(category)
{ public override string ToString() => nameof(EntraProtocolFixtureFailure); }

internal enum EntraProtocolTokenCase
{
    Good, WrongIssuer, WrongAudience, WrongTenant, Unsigned, BadSignature, SymmetricAlgorithm, UnapprovedRsaAlgorithm,
    MissingSubject, MissingNbf, MissingExp, DuplicateIssuer, DuplicateAudience, DuplicateTenant,
    DuplicateSubject, DuplicateExp, Expired, NotYetValid, BadNonce, ExcludedKey,
    MissingObjectId, NoncanonicalObjectId, EmptyObjectId, DuplicateObjectId, ControlSubject, OversizedSubject, AlternateSubject
}
internal enum EntraProtocolMetadataCase
{
    Good, ExplicitFalsePar, WrongIssuer, TemplateIssuer, ForeignKeys, WrongKeysPath, KeysQuery,
    KeysPort, ForeignToken, ForeignAuthorize, Redirect, ParEndpoint, RequirePar, MalformedPar
}
internal enum EntraProtocolKeyCase { Exact, Template, MixedForeign, ForeignOnly, MissingIssuer, MalformedIssuer }
internal enum EntraProtocolOperation { Begin, BeginTwice, Callback, Reuse, Ambient, Overlap, ForeignOwner, StartedHeaders }

internal sealed class EntraProtocolGate
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Canceled;
    internal int PrefixBytes, DeclaredBytes;
    public override string ToString() => nameof(EntraProtocolGate);
}

internal sealed class EntraProtocolRequest
{
    internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Id;
    internal EntraProtocolOperation Operation;
    internal EntraLoginChallenge? Challenge;
    internal EntraVerifiedLogin? Verified;
    internal ServerSessionIdentityFailureKind? Failure, SecondFailure;
    internal Exception? ClosedFailure;
    internal EntraProtocolHandlerPolicy? Policy;
    internal Task? OwnedWork, ConfigurationObservation;
    internal HttpContext? Context;
    internal bool Entered, Canceled, CancellationWasOriginal, UnexpectedFailure, UserChanged, TamperDefault = false;
    internal int EvidencePublished;
    internal readonly TaskCompletionSource OwnedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource OverlapCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource? OriginalCancellation;
    public override string ToString() => nameof(EntraProtocolRequest);
}

internal sealed class EntraProtocolReply(HttpStatusCode status, IReadOnlyDictionary<string, string[]> headers,
    byte[] body, EntraProtocolRequest request)
{
    internal HttpStatusCode Status { get; } = status;
    internal IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
    internal byte[] Body { get; } = body;
    internal EntraProtocolRequest Request { get; } = request;
    public override string ToString() => nameof(EntraProtocolReply);
}

internal sealed class EntraProtocolBrowser
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    internal string Header => string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value));
    internal IReadOnlyList<string> Names => values.Keys.ToArray();
    internal EntraProtocolBrowser Copy()
    { var copy = new EntraProtocolBrowser(); foreach (var pair in values) copy.values.Add(pair.Key, pair.Value); return copy; }
    internal void Set(string name, string value) => values[name] = value;
    internal void Remove(string name) => values.Remove(name);
    internal void Apply(EntraProtocolReply reply)
    {
        if (!reply.Headers.TryGetValue("Set-Cookie", out var headers)) return;
        foreach (var cookie in Microsoft.Net.Http.Headers.SetCookieHeaderValue.ParseList(headers.ToList()))
        {
            string name = cookie.Name.ToString(), value = cookie.Value.ToString();
            if (value.Length == 0 || (cookie.MaxAge is { } age && age <= TimeSpan.Zero)) values.Remove(name);
            else values[name] = value;
        }
    }
    public override string ToString() => nameof(EntraProtocolBrowser);
}

internal sealed class EntraProtocolCode(string nonce, string state, string challenge, string redirect)
{
    internal string Nonce { get; } = nonce;
    internal string State { get; } = state;
    internal string Challenge { get; } = challenge;
    internal string Redirect { get; } = redirect;
    internal DateTimeOffset ExpiresUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
    internal int Consumed;
    public override string ToString() => nameof(EntraProtocolCode);
}

internal sealed class EntraProtocolFlow(EntraProtocolBrowser browser, EntraProtocolReply begin, string callback)
{
    internal EntraProtocolBrowser Browser { get; } = browser;
    internal EntraProtocolReply Begin { get; } = begin;
    internal string Callback { get; } = callback;
    public override string ToString() => nameof(EntraProtocolFlow);
}

internal sealed class EntraProtocolIssuerSend(string method, string path, string connection)
{
    internal string Method { get; } = method;
    internal string Path { get; } = path;
    internal string Connection { get; } = connection;
    public override string ToString() => nameof(EntraProtocolIssuerSend);
}

internal sealed class EntraProtocolHandlerPolicy(OpenIdConnectOptions options)
{
    internal Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>? Manager { get; } = options.ConfigurationManager;
    internal string CorrelationPrefix { get; } = options.CorrelationCookie.Name!;
    internal string NoncePrefix { get; } = options.NonceCookie.Name!;
    internal bool Fixed { get; } = options.ResponseType == "code" && options.ResponseMode == "query" && options.UsePkce && options.DisableTelemetry
        && options.Scope.SequenceEqual(["openid", "profile"]) && !options.SaveTokens && !options.GetClaimsFromUserInfoEndpoint && !options.MapInboundClaims
        && options.PushedAuthorizationBehavior == Microsoft.AspNetCore.Authentication.OpenIdConnect.PushedAuthorizationBehavior.Disable
        && options.ProtocolValidator.NonceLifetime == TimeSpan.FromMinutes(10) && options.RemoteAuthenticationTimeout == TimeSpan.FromMinutes(10);
    public override string ToString() => nameof(EntraProtocolHandlerPolicy);
}
