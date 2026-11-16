using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.WebUtilities;
using Braid.Identity;

namespace Braid.Tests;

public sealed class EntraAdmissionTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    public Task MalformedCallbackQueriesAreDeniedBeforeAdditionalProtocolSendsOrFlowConsumption() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var flow = await test.FlowAsync();
        var query = QueryHelpers.ParseQuery(new Uri(test.Origin, flow.Callback).Query);
        string code = Uri.EscapeDataString(query["code"].ToString());
        string state = Uri.EscapeDataString(query["state"].ToString());
        string callback = EntraProtocolFixturePolicy.Callback;
        string valid = "?code=" + code + "&state=" + state;
        string error = "?error=access_denied&state=" + state;
        string oversized = valid + "&session_state=" + new string('x', 16_385);
        string[] malformed =
        [
            valid + "&code=" + code,
            valid + "&code=other-code",
            valid + "&state=" + state,
            valid + "&state=other-state",
            error + "&error=access_denied",
            error + "&error=other-error",
            valid + "&error=access_denied",
            "?code=" + code,
            "?error=access_denied",
            "?state=" + state,
            "",
            valid + "&unknown=unadmitted",
            valid + "&Code=unadmitted",
            oversized
        ];

        int sends = test.Ledger().Count;
        int wireRequests = test.Issuer.Snapshot().Count;
        int tokenResponses = test.Issuer.TokenResponses;
        EntraProtocolFixturePolicy.Check(sends > 0 && tokenResponses == 0,
            "The callback admission control did not begin with a real unredeemed protocol flow.");
        foreach (string malformedQuery in malformed)
        {
            var denied = await test.SendAsync(callback + malformedQuery, flow.Browser);
            test.Denied(denied);
            test.CheckPrivacy(denied);
            EntraProtocolFixturePolicy.Check(denied.Status == HttpStatusCode.BadRequest && denied.Request.Entered
                && denied.Request.Challenge is null && !denied.Headers.ContainsKey("Set-Cookie")
                && test.Ledger().Count == sends && test.Issuer.Snapshot().Count == wireRequests
                && test.Issuer.TokenResponses == tokenResponses,
                "Malformed callback query admission changed flow cookies, entered provider HTTP or escaped closed denial.");
        }

        var completed = await test.CompleteAsync(flow);
        test.Success(completed);
        test.CheckPrivacy(completed);
        EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
        EntraProtocolFixturePolicy.Check(test.Ledger().Count > sends && test.Issuer.TokenResponses == tokenResponses + 1,
            "Malformed callback rejection consumed the original flow or disabled its real redemption control.");
    });

    [Fact]
    public void PublicOptionsConstructorRejectsEmptyAndConsumerTenantGuidsWithClosedConfigurationFailures()
    {
        var tenant = EntraProtocolFixturePolicy.Tenant;
        var client = EntraProtocolFixturePolicy.Client;
        var consumerTenant = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");
        (Guid Tenant, Guid Client)[] invalid =
        [
            (Guid.Empty, client),
            (tenant, Guid.Empty),
            (Guid.Empty, Guid.Empty),
            (consumerTenant, client),
            (consumerTenant, Guid.Empty)
        ];
        foreach (var value in invalid)
            ExpectClosedConfigurationFailure(() => _ = new EntraClientOptions(value.Tenant, value.Client, "https://app.braid.test"));
    }

    [Fact]
    public void PublicOptionsConstructorAdmitsOnlyCanonicalHttpsRootOriginsWithClosedConfigurationFailures()
    {
        foreach (string origin in new[] { "https://app.braid.test", "https://app.braid.test:8443", "https://127.0.0.1:8443", "https://[::1]:8443" })
        {
            var options = new EntraClientOptions(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, origin);
            EntraProtocolFixturePolicy.Check(options.TenantId == EntraProtocolFixturePolicy.Tenant
                && options.ClientId == EntraProtocolFixturePolicy.Client && options.PublicOrigin == origin,
                "Canonical fixed options were rejected or silently rewritten.");
        }

        string?[] invalid =
        [
            null,
            "",
            " ",
            "/relative",
            "app.braid.test",
            "http://app.braid.test",
            "ftp://app.braid.test",
            "HTTPS://app.braid.test",
            "https://APP.braid.test",
            " https://app.braid.test",
            "https://app.braid.test ",
            "https://app.braid.test/",
            "https://app.braid.test/path",
            "https://app.braid.test/../",
            "https://app.braid.test/%2f",
            "https://app.braid.test?query=value",
            "https://app.braid.test#fragment",
            "https://user@app.braid.test",
            "https://user:password@app.braid.test",
            "https://app.braid.test:443",
            "https://app.braid.test:0",
            "https://app.braid.test:65536",
            "https://app.braid.test:invalid",
            "https://",
            "https://" + new string('x', 2049)
        ];
        foreach (string? origin in invalid)
            ExpectClosedConfigurationFailure(() => _ = new EntraClientOptions(EntraProtocolFixturePolicy.Tenant,
                EntraProtocolFixturePolicy.Client, origin!));
    }

    private static void ExpectClosedConfigurationFailure(Action construct)
    {
        bool closed = false;
        try { construct(); }
        catch (Exception error)
        {
            closed = error is ServerSessionIdentityException failure
                && failure.Kind == ServerSessionIdentityFailureKind.ConfigurationFailure
                && failure.InnerException is null && failure.Data.Count == 0
                && failure.Message == "The server session operation failed."
                && failure.ToString() == nameof(ServerSessionIdentityException);
        }
        EntraProtocolFixturePolicy.Check(closed, "Invalid fixed options escaped the closed configuration-failure boundary.");
    }
}
