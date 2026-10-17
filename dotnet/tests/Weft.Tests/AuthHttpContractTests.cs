using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Contracts.Auth;
using Weft.Contracts.Catalog;
using Weft.Identity;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class AuthHttpContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Fingerprint = "v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void AuthWireUnionsAreClosedCoherentBoundedAndRedacted()
    {
        foreach (string state in new[] { "anonymous", "authenticationRequired", "authenticated" })
        {
            bool positive = state == "authenticated";
            var value = new AuthSessionV1(1, "session", state, "AA", positive ? "AQ" : null, positive ? Fingerprint : null);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
            AuthHttpFixture.Check(document.RootElement.EnumerateObject().Count() == 6 && value.ToString() == nameof(AuthSessionV1), "Auth status wire shape or diagnostic text changed.");
        }
        foreach (string mode in new[] { "development", "disabled" })
        {
            var value = new AuthModeStatusV1(1, mode); using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
            AuthHttpFixture.Check(document.RootElement.EnumerateObject().Count() == 2 && value.ToString() == nameof(AuthModeStatusV1), "Non-session mode acquired token/identity fields.");
        }
        Refuses(() => new AuthSessionV1(2, "session", "anonymous", "AA", null, null));
        Refuses(() => new AuthSessionV1(1, "disabled", "anonymous", "AA", null, null));
        Refuses(() => new AuthSessionV1(1, "session", "authenticated", "AA", null, Fingerprint));
        Refuses(() => new AuthSessionV1(1, "session", "anonymous", "AA", "AQ", Fingerprint));
        Refuses(() => new AuthSessionV1(1, "session", "authenticated", "AA", "AQ", "v1.invalid"));
        Refuses(() => new AuthSessionV1(1, "session", "anonymous", "not+canonical", null, null));
        Refuses(() => new AuthSessionV1(1, "session", "anonymous", new string('A', 4097), null, null));
        Refuses(() => new AuthModeStatusV1(1, "session")); Refuses(() => new AuthErrorV1(1, "private-provider-error"));
        Refuses(() => new AuthForgetLocalV1(1, "revoked")); Refuses(() => new AuthLoginV1(1, "http://login.microsoftonline.com/authorize"));
        Refuses(() => new AuthLoginV1(1, "https://user@login.microsoftonline.com/authorize")); Refuses(() => new AuthLoginV1(1, "https://login.microsoftonline.com/authorize#private"));
        Refuses(() => JsonSerializer.Deserialize<AuthModeStatusV1>("{\"version\":1,\"mode\":\"disabled\",\"identityContext\":\"private\"}", Json));
        Refuses(() => JsonSerializer.Deserialize<AuthSessionV1>("{\"version\":1,\"mode\":\"session\",\"state\":\"anonymous\",\"authActionRequestToken\":\"AA\",\"requestToken\":null,\"identityContext\":null,\"provider\":\"private\"}", Json));
        AuthHttpFixture.Check(typeof(BrowserAuthenticationSession).GetConstructors().Length == 0 && typeof(BrowserAuthenticationException).GetConstructors().Length == 0, "Public callers can construct private auth results/failures.");
    }

    [Fact]
    public void CatalogSessionRequiresExplicitFingerprintWhileDevelopmentJsonStaysCompatible()
    {
        string actor = "33333333-3333-3333-3333-333333333333", organization = "11111111-1111-1111-1111-111111111111";
        var development = new CatalogContextV1("development", actor, organization, "Owned workspace", CatalogFeaturesV1.ReadOnly, "AA");
        AuthHttpFixture.Check(!JsonSerializer.Serialize(development, Json).Contains("identityContext", StringComparison.Ordinal), "Development gained a serialized session fingerprint.");
        Refuses(() => new CatalogContextV1("session", actor, organization, "Owned workspace", CatalogFeaturesV1.ReadOnly, "AA"));
        Refuses(() => new CatalogContextV1("session", actor, organization, "Owned workspace", CatalogFeaturesV1.ReadOnly, CatalogOrganizationActionsV1.None, "AA", null));
        var session = new CatalogContextV1("session", actor, organization, "Owned workspace", CatalogFeaturesV1.ReadOnly, CatalogOrganizationActionsV1.None, "AA", Fingerprint);
        AuthHttpFixture.Check(session.IdentityContext == Fingerprint, "The explicit session context lost its fingerprint.");
        Refuses(() => ServerSessionIdentityContext.Derive(null!));
    }
    private static void Refuses(Action action)
    {
        bool refused = false;
        try { action(); } catch (ArgumentException) { refused = true; } catch (JsonException) { refused = true; }
        AuthHttpFixture.Check(refused, "An invalid closed auth/catalog wire value was accepted.");
    }
}
