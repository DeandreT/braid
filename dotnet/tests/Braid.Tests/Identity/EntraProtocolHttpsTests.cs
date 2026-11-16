using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>Independent real-HTTPS/standard-protocol source oracle; no production candidate read before its freeze.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class EntraProtocolHttpsTests
{
    [Fact]
    public Task ExplicitRegistrationAndOpaqueFacadeDoNotInstallFallbackOrTrustAmbientOptions() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var schemes = test.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var all = (await schemes.GetAllSchemesAsync()).ToArray();
        EntraProtocolFixturePolicy.Check(all.Length == 2 && all.Count(value => value.Name == "Braid.Entra") == 1
            && all.All(value => !typeof(CookieAuthenticationHandler).IsAssignableFrom(value.HandlerType))
            && all.Single(value => value.Name == "Braid.Entra").HandlerType.IsSubclassOf(typeof(OpenIdConnectHandler))
            && await schemes.GetDefaultAuthenticateSchemeAsync() is null && await schemes.GetDefaultChallengeSchemeAsync() is null
            && await schemes.GetDefaultSignInSchemeAsync() is null && await schemes.GetDefaultSignOutSchemeAsync() is null,
            "The adapter registered an ambient/default or stock-cookie authentication path.");
        var options = test.NamedOptions;
        foreach (var type in new[] { typeof(EntraLoginChallenge), typeof(EntraVerifiedLogin) })
            EntraProtocolFixturePolicy.Check(type.IsSealed && type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0
                && type.GetProperties().All(property => property.SetMethod is null || !property.SetMethod.IsPublic),
                "Private protocol evidence gained public construction or mutation.");
        EntraProtocolFixturePolicy.Check(JsonSerializer.Serialize(test.Options) == "{}" && test.Options.ToString() == nameof(EntraClientOptions),
            "Fixed options leaked private operands through default diagnostics.");
        var normal = new ServiceCollection(); normal.AddLogging();
        EntraClientRegistration.AddBraidEntraClient(normal, test.Options, test.Profile, test.Crypto.ClientCertificate);
        EntraProtocolFixturePolicy.Check(normal.All(value => value.ServiceType != typeof(EntraHttpsFixtureTransport)) && test.Ledger().Count == 0,
            "Normal registration installed a fixture ledger or started protocol HTTP.");

        var conflict = test.NewRequest(EntraProtocolOperation.Begin); conflict.TamperDefault = true;
        int conflictBefore = test.Ledger().Count;
        var refusedDefault = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin, record: conflict);
        test.Denied(refusedDefault, ServerSessionIdentityFailureKind.ConfigurationFailure);
        EntraProtocolFixturePolicy.Check(test.Ledger().Count == conflictBefore && conflict.Challenge is null,
            "A conflicting effective default entered protocol effects before refusal.");
        var flow = await test.FlowAsync();
        EntraProtocolFixturePolicy.Check(flow.Begin.Request.Policy is { Fixed: true },
            "The actual initialized handler's code/PKCE/PAR/token/cookie profile was not fixed.");
        int before = test.Issuer.Snapshot().Count;
        var ambient = await test.CompleteAsync(flow, EntraProtocolOperation.Ambient);
        EntraProtocolFixturePolicy.Check(ambient.Status == HttpStatusCode.NoContent && ambient.Request.Verified is null
            && ambient.Request.EvidencePublished == 0 && test.Issuer.Snapshot().Count == before,
            "Ambient middleware independently consumed a protocol callback.");
        var result = await test.CompleteAsync(flow); test.Success(result);
        EntraProtocolFixturePolicy.Check(JsonSerializer.Serialize(flow.Begin.Request.Challenge) == "{}"
            && JsonSerializer.Serialize(result.Request.Verified) == "{}"
            && flow.Begin.Request.Challenge!.ToString() == nameof(EntraLoginChallenge)
            && result.Request.Verified!.ToString() == nameof(EntraVerifiedLogin), "Protocol operands escaped default result diagnostics.");

        // An unused ambient bag may be ignored. It must never become the owned policy.
        int alienEventCalls = 0;
        options.Events = new OpenIdConnectEvents { OnTokenValidated = _ => { Interlocked.Increment(ref alienEventCalls); return Task.CompletedTask; } };
        options.SaveTokens = true; options.GetClaimsFromUserInfoEndpoint = true; options.MapInboundClaims = true; options.Scope.Add("offline_access");
        options.TokenValidationParameters.IssuerValidator = (_, _, _) => EntraProtocolFixturePolicy.Issuer;
        var browser = new EntraProtocolBrowser(); var challenge = await test.SendAsync("/api/auth/login", browser, EntraProtocolOperation.Begin);
        if (challenge.Request.Failure is not null) test.Denied(challenge, ServerSessionIdentityFailureKind.ConfigurationFailure);
        else
        {
            EntraProtocolFixturePolicy.Check(challenge.Request.Challenge is not null, "Ambient mutation produced neither a closed refusal nor a fixed challenge.");
            browser.Apply(challenge); test.Issuer.TokenCase = EntraProtocolTokenCase.WrongIssuer;
            var bad = await test.CompleteAsync(await test.AuthorizeAsync(browser, challenge)); test.Denied(bad);
        }
        EntraProtocolFixturePolicy.Check(alienEventCalls == 0 && test.Ports.Calls == 0 && test.Issuer.UnexpectedRequests == 0,
            "An ambient delegate, UserInfo or alternate authority entered effective protocol policy.");
    });

    [Fact]
    public Task RealPkceCertificateAndNonceProduceOnlyFinalExactPrivateFacts() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        test.PoisonAmbient = true;
        var flow = await test.FlowAsync(); test.ChallengeCookies(flow.Begin);
        var snapshot = test.Ledger(); int count = snapshot.Count;
        var reply = await test.CompleteAsync(flow); test.Success(reply); test.CheckPrivacy(reply);
        EntraProtocolFixturePolicy.Check(snapshot.Count == count && test.Ledger().Count > count
            && test.Ledger().Any(value => value.Method == "POST" && value.LogicalPath == EntraProtocolFixturePolicy.Token)
            && test.Issuer.TokenResponses == 1 && flow.Browser.Names.Count == 0,
            "The terminal snapshot/counters or matching transient-cookie completion control was not live.");
        // A fixture keepalive positive control, not a promise that an arbitrary peer never closes.
        EntraProtocolFixturePolicy.Check(test.Issuer.Snapshot().Where(value => value.Path != EntraProtocolFixturePolicy.Authorize)
            .GroupBy(value => value.Connection, StringComparer.Ordinal).Any(group => group.Count() >= 2),
            "The fixed-loopback fixture did not establish the observed connection-reuse control.");
    });

    [Fact]
    public async Task IndependentReplacementAtSameOriginUnprotectsOnlyTheSameConfiguredScope()
    {
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            var flow = await test.FlowAsync();
            await test.ReplaceApplicationAsync(independentProfile: true);
            var reply = await test.CompleteAsync(flow); test.Success(reply);
            EntraProtocolFixturePolicy.Check(test.SecondProfile is not null && !ReferenceEquals(test.Profile, test.SecondProfile),
                "The cross-instance control shared the original profile/handler instance.");
        });
        foreach (string scope in new[] { "organization", "tenant", "client" })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                var flow = await test.FlowAsync();
                var other = Guid.Parse("df07c377-bdb9-4d8a-b15f-d1dfc3537e99");
                var options = new EntraClientOptions(scope == "tenant" ? other : EntraProtocolFixturePolicy.Tenant,
                    scope == "client" ? other : EntraProtocolFixturePolicy.Client, test.Origin.GetLeftPart(UriPartial.Authority));
                await test.ReplaceApplicationAsync(independentProfile: true,
                    profileOrganization: scope == "organization" ? other : EntraProtocolFixturePolicy.Organization, fixedOptions: options);
                var reply = await test.CompleteAsync(flow); test.Denied(reply);
                EntraProtocolFixturePolicy.Check(test.Ledger().Count == 0 && test.Issuer.TokenResponses == 0,
                    "Protected state crossed a configured organization/tenant/client purpose before provider work.");
            });
    }

    [Fact]
    public Task StateCorrelationNoncePkceAndReplayNeverPublishEarlyEvidence() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        foreach (string variant in new[] { "state", "correlation", "nonce", "pkce" })
        {
            test.Issuer.BreakPkce = variant == "pkce";
            test.Issuer.TokenCase = variant == "nonce" ? EntraProtocolTokenCase.BadNonce : EntraProtocolTokenCase.Good;
            var flow = await test.FlowAsync();
            if (variant == "correlation")
            {
                string prefix = flow.Begin.Request.Policy!.CorrelationPrefix;
                foreach (string name in flow.Browser.Names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))) flow.Browser.Remove(name);
            }
            if (variant == "state")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(test.Origin, flow.Callback).Query);
                string state = query["state"].ToString(); int middle = state.Length / 2;
                string changed = state[..middle] + (state[middle] == 'A' ? "B" : "A") + state[(middle + 1)..];
                string path = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(EntraProtocolFixturePolicy.Callback,
                    new Dictionary<string, string?> { ["code"] = query["code"], ["state"] = changed });
                flow = new EntraProtocolFlow(flow.Browser, flow.Begin, path);
            }
            var denied = await test.CompleteAsync(flow); test.Denied(denied); test.CheckPrivacy(denied);
            if (variant != "state") EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
        }
        test.Issuer.BreakPkce = false; test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
        var positive = await test.FlowAsync(); var captured = positive.Browser.Copy();
        var success = await test.CompleteAsync(positive); test.Success(success); int responses = test.Issuer.TokenResponses;
        test.Denied(await test.CompleteAsync(new EntraProtocolFlow(captured, positive.Begin, positive.Callback)));
        EntraProtocolFixturePolicy.Check(test.Issuer.TokenResponses == responses, "A replayed one-use code minted another ID-token response.");
        int before = test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Token);
        test.Denied(await test.CompleteAsync(positive));
        EntraProtocolFixturePolicy.Check(test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Token) == before,
            "A completed browser flow without correlation cookies was redeemed again.");
    });

    [Fact]
    public Task StandardCryptoExactIssuerAudienceTenantAndCriticalClaimsRefuseEveryBoundedBadToken() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        foreach (var variant in new[] { EntraProtocolTokenCase.WrongIssuer, EntraProtocolTokenCase.WrongAudience,
            EntraProtocolTokenCase.WrongTenant, EntraProtocolTokenCase.Unsigned, EntraProtocolTokenCase.BadSignature,
            EntraProtocolTokenCase.SymmetricAlgorithm, EntraProtocolTokenCase.UnapprovedRsaAlgorithm, EntraProtocolTokenCase.MissingSubject, EntraProtocolTokenCase.MissingNbf,
            EntraProtocolTokenCase.MissingExp, EntraProtocolTokenCase.DuplicateIssuer, EntraProtocolTokenCase.DuplicateAudience,
            EntraProtocolTokenCase.DuplicateTenant, EntraProtocolTokenCase.DuplicateSubject, EntraProtocolTokenCase.DuplicateExp,
            EntraProtocolTokenCase.Expired, EntraProtocolTokenCase.NotYetValid, EntraProtocolTokenCase.MissingObjectId,
            EntraProtocolTokenCase.NoncanonicalObjectId, EntraProtocolTokenCase.EmptyObjectId, EntraProtocolTokenCase.DuplicateObjectId,
            EntraProtocolTokenCase.ControlSubject, EntraProtocolTokenCase.OversizedSubject })
        {
            test.Issuer.TokenCase = variant;
            var flow = await test.FlowAsync(); var reply = await test.CompleteAsync(flow); test.Denied(reply); test.CheckPrivacy(reply);
            EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
        }
        test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
    });

    [Fact]
    public async Task HostileMetadataIsRefusedBeforeUnintendedTerminalSendOrWire()
    {
        foreach (bool wrongName in new[] { true, false })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                var reply = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
                test.Denied(reply, ServerSessionIdentityFailureKind.Unavailable);
                EntraProtocolFixturePolicy.Check(reply.Request.Challenge is null && test.Issuer.Snapshot().Count == 0
                    && test.Ledger().All(value => value.LogicalPath == EntraProtocolFixturePolicy.Discovery),
                    "Wrong-name/untrusted-chain TLS reached issuer HTTP or protocol success.");
            }, wrongIssuerName: wrongName, wrongTrustRoot: !wrongName);
        foreach (var variant in new[] { EntraProtocolMetadataCase.WrongIssuer, EntraProtocolMetadataCase.TemplateIssuer,
            EntraProtocolMetadataCase.ForeignKeys, EntraProtocolMetadataCase.WrongKeysPath, EntraProtocolMetadataCase.KeysQuery,
            EntraProtocolMetadataCase.KeysPort, EntraProtocolMetadataCase.ForeignToken, EntraProtocolMetadataCase.ForeignAuthorize,
            EntraProtocolMetadataCase.Redirect, EntraProtocolMetadataCase.ParEndpoint, EntraProtocolMetadataCase.RequirePar,
            EntraProtocolMetadataCase.MalformedPar })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                test.Issuer.Metadata = variant;
                var reply = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
                EntraProtocolFixturePolicy.Check(reply.Request.Failure is ServerSessionIdentityFailureKind.ConfigurationFailure or ServerSessionIdentityFailureKind.Unavailable
                    && reply.Request.Challenge is null && reply.Request.EvidencePublished == 0,
                    "Hostile metadata produced a challenge/evidence instead of a closed configuration/transport refusal.");
                var ledger = test.Ledger();
                bool hostileKeys = variant is EntraProtocolMetadataCase.ForeignKeys or EntraProtocolMetadataCase.WrongKeysPath
                    or EntraProtocolMetadataCase.KeysQuery or EntraProtocolMetadataCase.KeysPort;
                EntraProtocolFixturePolicy.Check(ledger.All(value => value.Method == "GET"
                    && (value.LogicalPath == EntraProtocolFixturePolicy.Discovery || value.LogicalPath == EntraProtocolFixturePolicy.Keys))
                    && (!hostileKeys || ledger.All(value => value.LogicalPath != EntraProtocolFixturePolicy.Keys))
                    && test.Issuer.UnexpectedRequests == 0 && test.Ports.Calls == 0,
                    "Hostile metadata reached an unintended terminal target or HTTP path, including a reused allowed-path connection.");
                test.CheckPrivacy(reply);
            });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            test.Issuer.Metadata = EntraProtocolMetadataCase.ExplicitFalsePar;
            test.Success(await test.CompleteAsync(await test.FlowAsync()));
            EntraProtocolFixturePolicy.Check(test.Ledger().All(value => !value.LogicalPath.Contains("par", StringComparison.OrdinalIgnoreCase)),
                "The initial PAR-disabled profile sent PAR HTTP.");
        });
    }

    [Fact]
    public async Task ScopedJwksAndStandardRefreshKeepRolloverAndBadRefreshBounded()
    {
        foreach (var scope in new[] { EntraProtocolKeyCase.Exact, EntraProtocolKeyCase.Template, EntraProtocolKeyCase.MixedForeign })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                test.Issuer.KeyCase = scope; test.Issuer.CommonKeys = scope != EntraProtocolKeyCase.Exact;
                test.Success(await test.CompleteAsync(await test.FlowAsync()));
                if (scope == EntraProtocolKeyCase.MixedForeign)
                {
                    test.Issuer.TokenCase = EntraProtocolTokenCase.ExcludedKey;
                    test.Denied(await test.CompleteAsync(await test.FlowAsync()));
                }
            });
        foreach (var scope in new[] { EntraProtocolKeyCase.ForeignOnly, EntraProtocolKeyCase.MissingIssuer, EntraProtocolKeyCase.MalformedIssuer })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                test.Issuer.KeyCase = scope;
                var reply = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
                EntraProtocolFixturePolicy.Check(reply.Request.Failure is not null && reply.Request.Challenge is null
                    && test.Ports.Calls == 0 && test.Issuer.TokenResponses == 0, "A JWKS without eligible issuer-scoped signing material entered protocol admission.");
            });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed); // Real K1 validation seeds standard LKG.
            int beforeObservation = test.Ledger().Count;
            var initial = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
            EntraProtocolFixturePolicy.Check(initial.SigningKeys.Any(key => key.KeyId == "braid-k1")
                && initial.SigningKeys.All(key => key.KeyId != "braid-k2") && test.Ledger().Count == beforeObservation,
                "The refresh observation did not begin with a known fresh, admitted K1-only current configuration.");
            var flow = await test.FlowAsync(); int reads = test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Keys);
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); test.Issuer.NextKeysResponse = refreshed;
            test.Issuer.PublishSecond = true; test.Issuer.OnlySecond = true; test.Issuer.SignSecond = true;
            var current = await test.CompleteAsync(flow);
            if (current.Request.Verified is null) test.Denied(current); else test.Success(current);
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await test.WaitForPublishedSecondKeyAsync(seed.Request);
            // Exactly one NEW flow/code after publication proves actual K2 verification. The original
            // redemption is never resent, including when the standard nonblocking refresh arrived late.
            test.Success(await test.CompleteAsync(await test.FlowAsync()));
            EntraProtocolFixturePolicy.Check(test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Keys) > reads,
                "An unknown kid succeeded without actual standard admitted-key refresh.");
            test.Issuer.SignSecond = false; test.Success(await test.CompleteAsync(await test.FlowAsync()));
            EntraProtocolFixturePolicy.Check(test.Issuer.OnlySecond && test.Issuer.PublishSecond,
                "The retained K1 control did not keep every current/refreshed JWKS limited to K2-only material.");
        });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            test.Success(await test.CompleteAsync(await test.FlowAsync()));
            var flow = await test.FlowAsync(); int beforeHostileKeys = test.Ledger().Count(value => value.LogicalPath == EntraProtocolFixturePolicy.Keys);
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); test.Issuer.NextMetadataResponse = refreshed;
            test.Issuer.Metadata = EntraProtocolMetadataCase.ForeignKeys; test.Issuer.PublishSecond = true; test.Issuer.SignSecond = true;
            var failed = await test.CompleteAsync(flow);
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EntraProtocolFixturePolicy.Check(failed.Request.Verified is null && failed.Request.Failure is not null
                && test.Issuer.UnexpectedRequests == 0, "An unadmitted refresh broadened keys or published evidence.");
            test.Issuer.SignSecond = false;
            test.Success(await test.CompleteAsync(await test.FlowAsync()));
            await test.Facade.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); // Drain owned retrieval, not the hidden manager continuation.
            EntraProtocolFixturePolicy.Check(test.Ledger().Count(value => value.LogicalPath == EntraProtocolFixturePolicy.Keys) == beforeHostileKeys,
                "A hostile refresh reached terminal HTTP even though its unadmitted host reused an allowed logical key path.");
        });
    }

    [Fact]
    public Task TransientCookiePolicyAndFourOutstandingFlowBudgetHavePositiveReleaseControl() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var browser = new EntraProtocolBrowser(); var begins = new List<EntraProtocolReply>();
        for (int index = 0; index < 4; index++) { var begin = await test.BeginAsync(browser); test.ChallengeCookies(begin); begins.Add(begin); }
        var fifth = await test.SendAsync("/api/auth/login", browser, EntraProtocolOperation.Begin);
        EntraProtocolFixturePolicy.Check(fifth.Request.Failure is not null && fifth.Request.Challenge is null
            && !fifth.Headers.ContainsKey("Set-Cookie"), "A fifth outstanding browser flow escaped its finite admission budget.");
        test.Success(await test.CompleteAsync(await test.AuthorizeAsync(browser, begins[0])));
        EntraProtocolFixturePolicy.Check(browser.Names.Count == 6, "Flow completion removed another outstanding nonce/correlation pair.");
        test.ChallengeCookies(await test.BeginAsync(browser));
        EntraProtocolFixturePolicy.Check(browser.Names.Count == 8, "Completing one owned flow did not free exactly one browser flow slot.");
        test.Issuer.TokenCase = EntraProtocolTokenCase.BadNonce;
        var deniedFlow = await test.AuthorizeAsync(browser, begins[1]);
        test.Denied(await test.CompleteAsync(deniedFlow)); EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(deniedFlow);
        EntraProtocolFixturePolicy.Check(browser.Names.Count == 6, "A denied owned flow retained its pair or removed another outstanding pair.");
        test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
        test.Success(await test.CompleteAsync(await test.AuthorizeAsync(browser, begins[2])));
        EntraProtocolFixturePolicy.Check(browser.Names.Count == 4, "A denied callback invalidated an independent outstanding flow.");
        var oversized = browser.Copy(); oversized.Set(begins[0].Request.Policy!.NoncePrefix + "owned-oversized", new string('x', 16_385));
        int before = test.Ledger().Count;
        var refused = await test.SendAsync("/api/auth/login", oversized, EntraProtocolOperation.Begin);
        EntraProtocolFixturePolicy.Check(refused.Request.Failure is not null && refused.Request.Challenge is null
            && !refused.Headers.ContainsKey("Set-Cookie") && test.Ledger().Count == before,
            "An oversized transient-cookie input issued a new flow or entered provider HTTP.");
    });

    [Fact]
    [Trait("Category", "EntraSlowFlowExpiry")]
    public Task RealTenMinuteProtectedStateExpiryPrecedesFreshCodeRedemption() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var browser = new EntraProtocolBrowser(); var begin = await test.BeginAsync(browser); test.ChallengeCookies(begin);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(620)); var elapsed = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(2)), limit.Token);
        EntraProtocolFixturePolicy.Check(elapsed.Elapsed >= TimeSpan.FromMinutes(10), "The real protected-flow expiry control did not pass ten minutes.");
        // Raw cookies are intentionally retained manually; the fresh code is minted only now.
        var flow = await test.AuthorizeAsync(browser, begin); test.Denied(await test.CompleteAsync(flow));
        EntraProtocolFixturePolicy.Check(test.Issuer.Snapshot().All(value => value.Path != EntraProtocolFixturePolicy.Token),
            "Expired protected state reached token redemption despite manually retained browser cookies.");
    });

    [Fact]
    public async Task OnceOnlyContextProvenanceAndOriginalCancellationOwnRealFrameworkWork()
    {
        foreach (var operation in new[] { EntraProtocolOperation.Overlap, EntraProtocolOperation.ForeignOwner })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                if (operation == EntraProtocolOperation.ForeignOwner) test.CreateForeignFacade();
                var flow = await test.FlowAsync(); var gate = new EntraProtocolGate(); test.Issuer.TokenGate = gate;
                var record = test.NewRequest(operation, ownCancellation: true);
                var pending = test.CompleteAsync(flow, operation, record);
                try
                {
                    await Task.WhenAll(gate.Entered.Task, record.OverlapCompleted.Task, record.OwnedStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                    EntraProtocolFixturePolicy.Check(record.SecondFailure == ServerSessionIdentityFailureKind.UntrustedIssuance
                        && record.OwnedWork is { IsCompleted: false } && record.EvidencePublished == 0,
                        "A forbidden context overlap joined/released ownership or published evidence.");
                    record.OriginalCancellation!.Cancel(); var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    EntraProtocolFixturePolicy.Check(record.Canceled && record.CancellationWasOriginal && record.OwnedWork!.IsCanceled && record.Verified is null
                        && record.EvidencePublished == 0 && Volatile.Read(ref gate.Canceled) == 1 && test.Ports.Calls == 0,
                        "The original canceled protocol work did not drain before evidence/ownership release.");
                    test.CheckPrivacy(reply); EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
                }
                finally { gate.Release.TrySetResult(); await DrainAsync(pending); }
            });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            var flow = await test.FlowAsync(); var gate = new EntraProtocolGate(); test.Issuer.TokenGate = gate;
            var record = test.NewRequest(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = test.SendAsync(flow.Callback, flow.Browser, record: record, cancellationToken: cancel.Token);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); bool clientCanceled = false;
                try { await pending; } catch (OperationCanceledException) { clientCanceled = true; }
                await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10)); await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                EntraProtocolFixturePolicy.Check(clientCanceled && record.Canceled && record.CancellationWasOriginal
                    && record.OwnedWork is { IsCanceled: true } && record.EvidencePublished == 0
                    && Volatile.Read(ref gate.Canceled) == 1 && test.Ports.Calls == 0,
                    "Actual HTTPS disconnect lost its request-cancellation cause or released live protocol work.");
            }
            finally { gate.Release.TrySetResult(); await DrainAsync(pending); }
        });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            var browser = new EntraProtocolBrowser();
            var begin = await test.SendAsync("/api/auth/login", browser, EntraProtocolOperation.BeginTwice);
            EntraProtocolFixturePolicy.Check(begin.Request.Challenge is not null && begin.Request.SecondFailure == ServerSessionIdentityFailureKind.UntrustedIssuance,
                "The challenge context was reusable after its first completed operation.");
            browser.Apply(begin); var reply = await test.CompleteAsync(await test.AuthorizeAsync(browser, begin), EntraProtocolOperation.Reuse);
            test.Success(reply);
            EntraProtocolFixturePolicy.Check(reply.Request.SecondFailure == ServerSessionIdentityFailureKind.UntrustedIssuance,
                "The callback context was reusable after final evidence delivery.");
        });
        await EntraProtocolHttpsFixture.RunAsync(async test =>
        {
            var flow = await test.FlowAsync(); int before = test.Ledger().Count;
            var reply = await test.CompleteAsync(flow, EntraProtocolOperation.StartedHeaders);
            test.Denied(reply, ServerSessionIdentityFailureKind.Unavailable);
            EntraProtocolFixturePolicy.Check(test.Ledger().Count == before, "A headers-started refusal entered provider HTTP.");
        });
    }

    [Fact]
    public Task OwnedDisposalDrainsHttpAndPreservesBorrowedProfileCertificateRootAndPrivateFailures() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var gate = new EntraProtocolGate(); test.Issuer.MetadataGate = gate;
        var record = test.NewRequest(EntraProtocolOperation.Begin);
        var pending = test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin, record: record);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task disposal = test.Facade.DisposeAsync().AsTask(); await disposal.WaitAsync(TimeSpan.FromSeconds(15));
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10)); await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EntraProtocolFixturePolicy.Check(record.OwnedWork is { IsCompleted: true } && record.Verified is null && record.Challenge is null
                && (record.Canceled || record.Failure == ServerSessionIdentityFailureKind.Unavailable)
                && Volatile.Read(ref gate.Canceled) == 1 && test.Ports.Calls == 0, "Runtime disposal released live owned HTTP or delivered protocol success.");
            test.CheckPrivacy(reply); int before = test.Ledger().Count;
            var refused = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
            test.Denied(refused, ServerSessionIdentityFailureKind.Unavailable);
            EntraProtocolFixturePolicy.Check(test.Ledger().Count == before, "A disposed runtime or later queued retrieval reached terminal HTTP.");
        }
        finally { gate.Release.TrySetResult(); test.Issuer.MetadataGate = null; await DrainAsync(pending); }
        await test.ReplaceApplicationAsync(independentProfile: false);
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
        test.Issuer.MalformedTokenResponse = true;
        var malformedFlow = await test.FlowAsync(); var malformed = await test.CompleteAsync(malformedFlow);
        EntraProtocolFixturePolicy.Check(malformed.Request.Failure is ServerSessionIdentityFailureKind.Denied or ServerSessionIdentityFailureKind.Unavailable,
            "Malformed provider JSON escaped the closed protocol/operational failure boundary.");
        test.Denied(malformed, malformed.Request.Failure!.Value); test.CheckPrivacy(malformed);
        EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(malformedFlow);
    });

    private static async Task DrainAsync(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { } }
}
