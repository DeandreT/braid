using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Braid.Identity;

namespace Braid.Tests;

public sealed partial class EntraProtocolHttpsTests
{
    [Fact]
    public void MemoryLogOracleDetectsFormatterExceptionAndNestedAggregateMarkersWithoutRenderingThem()
    {
        foreach (string source in new[] { "formatter", "message", "inner", "aggregate" })
        {
            using var logs = new EntraProtocolMemoryLogs();
            var logger = logs.CreateLogger("Microsoft.AspNetCore.Authentication.OwnedOracleControl");
            Exception? error = source switch
            {
                "message" => new RedactedDiagnosticControl(EntraProtocolFixturePolicy.PrivateMarker),
                "inner" => new RedactedDiagnosticControl("Owned outer diagnostic.", new RedactedDiagnosticControl(EntraProtocolFixturePolicy.PrivateMarker)),
                "aggregate" => new RedactedAggregateControl(new RedactedDiagnosticControl("Owned first branch."),
                    new RedactedDiagnosticControl("Owned nested branch.", new RedactedDiagnosticControl(EntraProtocolFixturePolicy.PrivateMarker))),
                _ => null
            };
            logger.Log(LogLevel.Warning, new EventId(116),
                source == "formatter" ? EntraProtocolFixturePolicy.PrivateMarker : "Owned redacted formatter.", error,
                static (state, _) => state);
            EntraProtocolFixturePolicy.Check(!logs.InspectionIncomplete
                && logs.Snapshot().Any(value => value.Contains(EntraProtocolFixturePolicy.PrivateMarker, StringComparison.Ordinal))
                && logs.ToString() == nameof(EntraProtocolMemoryLogs),
                "The in-memory privacy oracle missed a formatter/message/inner-aggregate positive control.");
        }
    }

    [Fact]
    public void MemoryLogOracleFailsClosedWhenDiagnosticBudgetsAreExceeded()
    {
        foreach (string bound in new[] { "events", "text", "graph" })
        {
            using var logs = new EntraProtocolMemoryLogs();
            var logger = logs.CreateLogger("Braid.Identity.OwnedOracleControl");
            if (bound == "events")
                for (int index = 0; index < 257; index++)
                    logger.Log(LogLevel.Warning, new EventId(116), "Owned diagnostic.", null, static (state, _) => state);
            else if (bound == "text")
                logger.Log(LogLevel.Warning, new EventId(116), new string('x', 16_385) + EntraProtocolFixturePolicy.PrivateMarker,
                    null, static (state, _) => state);
            else
            {
                Exception error = new RedactedDiagnosticControl(EntraProtocolFixturePolicy.PrivateMarker);
                for (int index = 0; index < 33; index++) error = new RedactedDiagnosticControl("Owned diagnostic.", error);
                logger.Log(LogLevel.Warning, new EventId(116), "Owned redacted formatter.", error, static (state, _) => state);
            }
            EntraProtocolFixturePolicy.Check(logs.InspectionIncomplete && logs.Snapshot().Count <= 2048,
                "Exhausting an in-memory diagnostic inspection budget was silently treated as a privacy pass.");
        }
    }

    [Fact]
    public Task CertificateAssertionOracleRequiresExactSha256CertificateHeader() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        string exact = Base64UrlEncoder.Encode(SHA256.HashData(test.Crypto.ClientCertificate.RawData));
        string otherCertificate = Base64UrlEncoder.Encode(SHA256.HashData(test.Crypto.Root.RawData));
        EntraProtocolFixturePolicy.Check(await test.Crypto.ValidateAssertionAsync(test.Crypto.AssertionControl(exact)),
            "The exact PS256 certificate SHA-256 assertion control was not accepted.");
        EntraProtocolFixturePolicy.Check(!await test.Crypto.ValidateAssertionAsync(test.Crypto.AssertionControl(null)),
            "A validly signed assertion without the SHA-256 certificate header was accepted.");
        EntraProtocolFixturePolicy.Check(!await test.Crypto.ValidateAssertionAsync(test.Crypto.AssertionControl(otherCertificate)),
            "A validly signed assertion naming another certificate digest was accepted.");
        EntraProtocolFixturePolicy.Check(!await test.Crypto.ValidateAssertionAsync(test.Crypto.AssertionControl(exact + "=")),
            "A non-exact padded certificate header was accepted.");
        // A fresh exact assertion after the refusals proves the negative controls did not
        // merely poison the replay oracle or dispose its certificate.
        EntraProtocolFixturePolicy.Check(await test.Crypto.ValidateAssertionAsync(test.Crypto.AssertionControl(exact)),
            "The certificate assertion oracle lost its fresh positive control after header refusals.");
    });

    [Fact]
    public Task TokenHttpStatusesAreClassifiedWithoutDescriptionsRedirectsOrRedemptionRetries() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        int callbacks = 0;
        foreach (int status in new[] { 300, 301, 302, 303, 304, 307, 308, 399, 401, 403, 408, 429, 500, 502, 503, 504, 599, 400 })
            foreach (string? description in new[] { null, EntraProtocolFixturePolicy.PrivateMarker + " invalid_grant",
                EntraProtocolFixturePolicy.PrivateMarker + " server_error temporarily_unavailable invalid_client HTTP 503" })
            {
                var flow = await test.FlowAsync();
                test.Issuer.TokenFailureStatus = (HttpStatusCode)status; test.Issuer.TokenFailureDescription = description;
                int before = test.Ledger().Count, wireBefore = test.Issuer.Snapshot().Count;
                var reply = await test.CompleteAsync(flow);
                test.Denied(reply, status == 400 ? ServerSessionIdentityFailureKind.Denied : ServerSessionIdentityFailureKind.Unavailable);
                test.CheckPrivacy(reply); EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
                var attempts = test.Ledger().Skip(before).ToArray(); var wire = test.Issuer.Snapshot().Skip(wireBefore).ToArray();
                EntraProtocolFixturePolicy.Check(attempts.Length == 1 && attempts[0].Method == "POST"
                    && attempts[0].LogicalPath == EntraProtocolFixturePolicy.Token && wire.Length == 1
                    && wire[0].Method == "POST" && wire[0].Path == EntraProtocolFixturePolicy.Token
                    && test.Issuer.TokenResponses == 0 && test.Issuer.UnexpectedRequests == 0 && test.Issuer.UnexpectedFailures == 0,
                    "An HTTP token refusal retried redemption, followed a redirect or performed another outbound send.");
                callbacks++;
            }
        test.Issuer.TokenFailureStatus = null; test.Issuer.TokenFailureDescription = null;
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
        await test.Facade.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        EntraProtocolFixturePolicy.Check(test.Ledger().Count(value => value.LogicalPath == EntraProtocolFixturePolicy.Token) == callbacks + 1
            && test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Token) == callbacks + 1
            && test.Issuer.AssertionAccepted == callbacks + 1 && test.Issuer.AssertionRejected == 0 && test.Issuer.PkceAccepted == callbacks + 1,
            "Settling the runtime revealed a delayed resend or a refusal that bypassed genuine assertion/PKCE validation.");
    });

    [Fact]
    public Task CorruptedStateCannotClearCookiesOrRedeemAnyIndependentOutstandingFlow() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var browser = new EntraProtocolBrowser(); var flows = new List<EntraProtocolFlow>();
        for (int index = 0; index < 4; index++) flows.Add(await test.FlowAsync(browser));
        string originalCookies = browser.Header;
        var query = QueryHelpers.ParseQuery(new Uri(test.Origin, flows[0].Callback).Query);
        string state = query["state"].ToString(); int middle = state.Length / 2;
        string corrupt = state[..middle] + (state[middle] == 'A' ? "B" : "A") + state[(middle + 1)..];
        string callback = QueryHelpers.AddQueryString(EntraProtocolFixturePolicy.Callback,
            new Dictionary<string, string?> { ["code"] = query["code"], ["state"] = corrupt });
        int before = test.Ledger().Count, wireBefore = test.Issuer.Snapshot().Count;
        var refused = await test.CompleteAsync(new EntraProtocolFlow(browser, flows[0].Begin, callback));
        test.Denied(refused); test.CheckPrivacy(refused);
        EntraProtocolFixturePolicy.Check(!refused.Headers.ContainsKey("Set-Cookie") && browser.Header == originalCookies
            && browser.Names.Count == 8 && test.Ledger().Count == before && test.Issuer.Snapshot().Count == wireBefore,
            "Unauthenticated corrupted state changed a flow cookie or entered outbound protocol work.");
        for (int index = 0; index < flows.Count; index++)
        {
            var completed = await test.CompleteAsync(flows[index]); test.Success(completed); test.CheckPrivacy(completed);
            EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flows[index]);
            EntraProtocolFixturePolicy.Check(browser.Names.Count == 6 - 2 * index,
                "Completing an original outstanding flow after corrupted state affected another flow's cookie pair.");
        }
        EntraProtocolFixturePolicy.Check(test.Issuer.TokenResponses == 4 && test.Issuer.PkceAccepted == 4,
            "The corrupted-state refusal consumed a code or prevented an original independent flow from completing.");
    });

    private sealed class RedactedAggregateControl(params Exception[] inner) : AggregateException("Owned aggregate diagnostic.", inner)
    {
        public override string Message => "Owned redacted aggregate diagnostic.";
        public override string ToString() => nameof(RedactedAggregateControl);
    }

    private sealed class RedactedDiagnosticControl(string message, Exception? inner = null) : Exception(message, inner)
    { public override string ToString() => nameof(RedactedDiagnosticControl); }
}
