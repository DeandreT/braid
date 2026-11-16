using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Independent public-API oracle; no new catalog-session production source was read before this packet froze.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogSessionHttpsIntegrationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task ActualSessionContextReadAndMetadataPatchUseRestrictedPostgresAndDueRenewal() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            await test.SetCapabilityAsync(true);
            var cookies = await test.IssueAsync();
            var context = await test.ContextAsync(cookies);
            CatalogSessionHttpsFixture.Check(context.Reply.Json().GetProperty("organizationActions").GetProperty("createProjects").GetBoolean()
                && context.Reply.Record.CatalogReads > 0 && context.Reply.Record.CapabilityReads > 0
                && context.Reply.Record.SessionRenews > 0, "Context did not freshly admit authority and use ordinary due-renewal admission.");
            var read = await test.SendAsync(test.MappingPath, cookies);
            CatalogSessionHttpsFixture.Check(read.Status == HttpStatusCode.OK && read.NoStore && read.Record.CatalogReads > 0
                && read.Record.SessionRenews > 0 && read.Record.Actor?.PrincipalId == test.Data.Actor.PrincipalId,
                "The actual mapping read did not use the session actor and ordinary admission.");
            string tag = await test.MetadataTagAsync(cookies);
            string[] immutable = await test.Data.ImmutableSnapshotAsync();
            var patch = await test.SendAsync(test.MetadataPath, cookies, "PATCH", context.Token,
                MetadataHttpTestSupport.Edit("Session-bound edit", "Exact owned HTTPS edit"), tag);
            CatalogSessionHttpsFixture.Check(patch.Status == HttpStatusCode.OK && patch.NoStore && patch.Record.BodyReads > 0
                && patch.Record.MetadataWrites == 1 && patch.Record.SessionRenews > 0
                && patch.Json().GetProperty("name").GetString() == "Session-bound edit",
                "The protected actual PostgreSQL metadata edit did not complete exactly once.");
            var stored = await test.Data.HeaderAsync();
            CatalogSessionHttpsFixture.Check(stored.Name == "Session-bound edit" && Enumerable.SequenceEqual(immutable, await test.Data.ImmutableSnapshotAsync())
                && patch.Record.DeferredCalls == 0 && patch.Record.DownstreamPaths == 0,
                "The metadata edit changed immutable native evidence or entered a downstream worker/store path.");
        });

    [PostgresFact]
    public Task EveryUnsafeFamilyRejectsMissingOldEpochForeignOriginAndMissingSessionBeforeAnyBodyOrBusiness() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); var old = await test.ContextAsync(cookies);
            string? sameAntiforgery = cookies.Antiforgery;
            var renewed = await test.IssueAsync(cookies);
            CatalogSessionHttpsFixture.Check(renewed.Session != cookies.Session && renewed.Antiforgery == sameAntiforgery,
                "The old-epoch catalog setup did not preserve the same AF cookie with a distinct fresh session.");
            var current = await test.ContextAsync(renewed); string tag = await test.MetadataTagAsync(renewed);
            string before = await test.Data.RowSnapshotAsync(); string[] immutable = await test.Data.ImmutableSnapshotAsync();
            foreach (var route in test.UnsafeRoutes())
            {
                var absentSession = renewed.Copy(); absentSession.Session = null;
                var tamperedSession = renewed.Copy(); tamperedSession.Session = "invalid-owned-carrier";
                var absentAf = renewed.Copy(); absentAf.Antiforgery = null;
                var cases = new[] {
                    (Cookies: renewed, Token: (string?)null, Origins: (string[]?)null, Status: HttpStatusCode.Forbidden),
                    (Cookies: renewed, Token: (string?)old.Token, Origins: (string[]?)null, Status: HttpStatusCode.Forbidden),
                    (Cookies: renewed, Token: (string?)current.Token, Origins: new string[] { "https://foreign-origin.invalid" }, Status: HttpStatusCode.Forbidden),
                    (Cookies: renewed, Token: (string?)current.Token, Origins: new string[] { test.Origin.GetLeftPart(UriPartial.Authority), test.Origin.GetLeftPart(UriPartial.Authority) }, Status: HttpStatusCode.Forbidden),
                    (Cookies: absentSession, Token: (string?)current.Token, Origins: (string[]?)null, Status: HttpStatusCode.Unauthorized),
                    (Cookies: tamperedSession, Token: (string?)current.Token, Origins: (string[]?)null, Status: HttpStatusCode.Unauthorized),
                    (Cookies: absentAf, Token: (string?)current.Token, Origins: (string[]?)null, Status: HttpStatusCode.Forbidden)
                };
                foreach (var item in cases)
                {
                    var reply = await test.HeadersOnlyAsync(route, item.Cookies, item.Token, tag, item.Origins);
                    CatalogSessionHttpsFixture.Error(reply, item.Status, "identityRejected");
                    CatalogSessionHttpsFixture.NoBodyOrBusiness(reply);
                }
            }
            CatalogSessionHttpsFixture.Check(before == await test.Data.RowSnapshotAsync() && Enumerable.SequenceEqual(immutable, await test.Data.ImmutableSnapshotAsync()),
                "A refused unsafe family changed actual private catalog/native facts.");
            var allowed = await test.SendAsync(test.MetadataPath, renewed, "PATCH", current.Token,
                MetadataHttpTestSupport.Edit("New-epoch positive control", ""), tag);
            CatalogSessionHttpsFixture.Check(allowed.Status == HttpStatusCode.OK && allowed.Record.MetadataWrites == 1
                && allowed.Record.SessionRenews > 0, "The same renewal-due session/token did not admit its positive protected mutation control.");
            test.CheckDeferredWitnessWiring();
            await test.ProbeDeferredWitnessesAsync(renewed, current.Token);
        });

    [PostgresFact]
    public Task EarlierContextAndCookieDoNotCacheOrganizationOrProjectAuthority() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); var context = await test.ContextAsync(cookies);
            string tag = await test.MetadataTagAsync(cookies);
            await test.SetOrganizationAsync(false);
            var deniedContext = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies,
                observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(deniedContext, HttpStatusCode.NotFound, "notFound");
            CatalogSessionHttpsFixture.Check(!deniedContext.Headers.ContainsKey("Set-Cookie") && deniedContext.Record.CapabilityReads == 0,
                "Organization denial exposed a new request token/cookie or advanced to capability admission.");
            var deniedMapping = await test.SendAsync(test.MappingPath, cookies, observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(deniedMapping, HttpStatusCode.NotFound, "notFound");
            await test.SetOrganizationAsync(true); await test.SetMembershipAsync(false);
            var route = test.UnsafeRoutes().Single(value => value.Family == "metadata-patch");
            var patch = await test.HeadersOnlyAsync(route, cookies, context.Token, tag);
            CatalogSessionHttpsFixture.Error(patch, HttpStatusCode.NotFound, "notFound");
            CatalogSessionHttpsFixture.Check(patch.Record.BodyReads == 0 && patch.Record.MetadataWrites == 0 && patch.Record.CatalogReads > 0,
                "Project revocation was not freshly checked before metadata body/writer admission.");
        });

    [PostgresFact]
    public Task CapabilityRevocationAfterPositiveContextRejectsProjectCreationBeforeBodyAndCreator() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            await test.SetCapabilityAsync(true); var cookies = await test.IssueAsync(); var context = await test.ContextAsync(cookies);
            CatalogSessionHttpsFixture.Check(context.Reply.Json().GetProperty("organizationActions").GetProperty("createProjects").GetBoolean(),
                "The capability-revocation oracle did not start with a positive current context.");
            var created = await test.SendAsync(ProjectCreationHttpTestSupport.CreatePath(test.Data.Seed.OrganizationA),
                cookies, "POST", context.Token, ProjectCreationHttpTestSupport.Body("Session-created project", "Owned authority witness"),
                mediaType: ProjectCreationHttpTestSupport.MediaType, idempotencyKey: true);
            CatalogSessionHttpsFixture.Check(created.Status == HttpStatusCode.Created && created.Record.CreatorCalls == 1
                && created.Record.BodyReads > 0 && created.Record.SessionRenews > 0,
                "The enabled project creator did not have a positive actual PostgreSQL route witness.");
            await test.SetCapabilityAsync(false);
            var reply = await test.HeadersOnlyAsync(test.UnsafeRoutes().Single(value => value.Family == "project-create"), cookies, context.Token);
            CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.Forbidden, "permissionDenied");
            CatalogSessionHttpsFixture.Check(reply.Record.CapabilityReads > 0 && reply.Record.BodyReads == 0 && reply.Record.CreatorCalls == 0,
                "A stale context capability reached a project body or creator.");
            var fresh = await test.ContextAsync(cookies);
            CatalogSessionHttpsFixture.Check(!fresh.Reply.Json().GetProperty("organizationActions").GetProperty("createProjects").GetBoolean(),
                "The next context reused a revoked organization capability.");
        });

    [PostgresFact]
    public Task RealMetadataWriterRechecksMembershipRevokedDuringAnActualBodyGate() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); var context = await test.ContextAsync(cookies); string tag = await test.MetadataTagAsync(cookies);
            string before = await test.Data.RowSnapshotAsync(); string[] immutable = await test.Data.ImmutableSnapshotAsync();
            var gate = new CatalogSessionBodyGate(); var record = test.NewObservation(gate, seedEtag: true);
            var pending = test.SendAsync(test.MetadataPath, cookies, "PATCH", context.Token,
                MetadataHttpTestSupport.Edit("Must remain uncommitted", ""), tag, observation: record);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                CatalogSessionHttpsFixture.Check(record.CatalogReads > 0 && record.MetadataWrites == 0 && record.BodyBytes == 0
                    && record.Order.Contains("mapping-completed"), "The real metadata read did not precede the cancellable body gate.");
                await test.SetMembershipAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
                gate.Release.TrySetResult(); var denied = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                CatalogSessionHttpsFixture.Error(denied, HttpStatusCode.NotFound, "notFound");
                CatalogSessionHttpsFixture.Check(record.MetadataWrites == 1 && before == await test.Data.RowSnapshotAsync()
                    && Enumerable.SequenceEqual(immutable, await test.Data.ImmutableSnapshotAsync()),
                    "The atomic metadata writer did not preserve facts under fresh post-body authority denial.");
            }
            finally { gate.Release.TrySetResult(); await DrainAsync(pending); }
        });

    [PostgresFact]
    public Task AmbientProviderRoleIdentityAndBearerHeadersNeverReplaceTheSessionActor() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            test.PoisonAmbient = true; var cookies = await test.IssueAsync();
            var context = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, forgedHeaders: true);
            CatalogSessionHttpsFixture.Check(context.Status == HttpStatusCode.OK
                && context.Json().GetProperty("actorPrincipalId").GetString() == test.Data.Seed.Alice.ToString("D")
                && context.Record.Actor?.PrincipalId == test.Data.Actor.PrincipalId, "Ambient identity/role/header claims replaced the private session actor.");
            var mapping = await test.SendAsync(test.MappingPath, cookies, forgedHeaders: true);
            CatalogSessionHttpsFixture.Check(mapping.Status == HttpStatusCode.OK && mapping.Record.Actor?.PrincipalId == test.Data.Actor.PrincipalId,
                "A resource port received an ambient/header actor instead of the admitted session principal.");
            var absent = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, new CatalogSessionCookies(), forgedHeaders: true,
                observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(absent, HttpStatusCode.Unauthorized, "identityRejected");
            CatalogSessionHttpsFixture.NoBodyOrBusiness(absent);
            // Without bearer access an Authorization header is refused outright, with or without the session's cookies.
            foreach (var carrier in new[] { cookies, new CatalogSessionCookies() })
            {
                var bearer = await test.SendAsync(test.MappingPath, carrier, forgedHeaders: true, authorization: "Bearer forged-catalog-fixture",
                    observation: test.NewObservation(seedEtag: true));
                CatalogSessionHttpsFixture.Error(bearer, HttpStatusCode.Unauthorized, "identityRejected");
                CatalogSessionHttpsFixture.NoBodyOrBusiness(bearer);
                CatalogSessionHttpsFixture.Check(bearer.Record.Actor is null && !bearer.Headers.ContainsKey("WWW-Authenticate"),
                    "A disabled bearer path admitted an actor or advertised a challenge.");
            }
        });

    [PostgresFact]
    public async Task ForeignResourceScopeIsHiddenAndMismatchedConfiguredSessionOrganizationIsUnavailable()
    {
        await CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); await test.ContextAsync(cookies);
            var foreign = $"/api/v1/organizations/{test.Data.Seed.OrganizationB:D}/projects/{test.Data.Seed.ProjectB:D}";
            var reply = await test.SendAsync(foreign, cookies, observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.NotFound, "notFound");
        });
        await CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync();
            var reply = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.ServiceUnavailable, "unavailable");
            CatalogSessionHttpsFixture.NoBodyOrBusiness(reply, noRenew: false);
        }, mismatchedCatalogOrganization: true);
    }

    [PostgresFact]
    public Task SessionActorStillNeedsFreshIndependentPayloadGrantAndCurrentProjectAccess() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); var context = await test.ContextAsync(cookies);
            var actor = context.Reply.Record.Actor!;
            CatalogSessionHttpsFixture.Check(actor.PrincipalId == test.Data.Actor.PrincipalId && !await test.EffectivePayloadAsync(actor),
                "Session identity or an existing project role silently supplied a payload grant.");
            await test.SetPayloadAsync(true, 0);
            CatalogSessionHttpsFixture.Check(await test.EffectivePayloadAsync(actor), "The explicitly granted current tuple was not effective.");
            await test.SetMembershipAsync(false);
            CatalogSessionHttpsFixture.Check(!await test.EffectivePayloadAsync(actor), "An explicit grant bypassed current project authority.");
            await test.SetMembershipAsync(true); await test.SetPayloadAsync(false, 1);
            CatalogSessionHttpsFixture.Check(!await test.EffectivePayloadAsync(actor), "Restored project access undid an explicit grant revoke.");
            var read = await test.SendAsync(test.MappingPath, cookies);
            CatalogSessionHttpsFixture.Check(read.Status == HttpStatusCode.OK && read.Record.DeferredCalls == 0 && read.Record.DownstreamPaths == 0,
                "Metadata visibility depended on revoked payload permission or touched payload/worker I/O.");
        });

    [PostgresFact]
    public async Task SessionPoolAndTokenIssuanceFailuresAreClosedWithoutIncompleteContext()
    {
        await CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); await test.BreakSessionReaderAsync();
            var reply = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.ServiceUnavailable, "unavailable");
            CatalogSessionHttpsFixture.NoBodyOrBusiness(reply);
        });
        await CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); await test.ContextAsync(cookies);
            test.BreakTokenProfileAfterOrganization = true;
            var reply = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, observation: test.NewObservation(seedEtag: true));
            CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.ServiceUnavailable, "unavailable");
            CatalogSessionHttpsFixture.Check(reply.Record.Order.Contains("organization-completed") && reply.Record.Order.Contains("token-profile-disposed")
                && !reply.Headers.ContainsKey("Set-Cookie")
                && reply.Record.MetadataWrites == 0 && reply.Record.CreatorCalls == 0,
                "The post-organization token refusal returned incomplete context or exposed a token cookie.");
        });
    }

    [PostgresFact]
    public async Task InactivePrincipalDisabledBindingAndActualDatabaseExpiryMapTo401BeforeCatalog()
    {
        foreach (string transition in new[] { "principal", "binding", "expiry" })
            await CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
            {
                if (transition == "expiry") test.VerifiedDeadline = new DateTimeOffset(await test.Data.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()+interval '4 seconds'"));
                var cookies = await test.IssueAsync();
                if (transition == "principal") await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Data.Seed.Alice);
                else if (transition == "binding")
                {
                    var result = await test.IdentityOwner.DisableAsync(test.Binding.Key, test.Data.Actor.PrincipalId, test.Binding.Version);
                    CatalogSessionHttpsFixture.Check(result.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled, "The captured binding was not actually disabled.");
                }
                else
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (new DateTimeOffset(await test.Data.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")) < test.VerifiedDeadline)
                        await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }
                var reply = await test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, observation: test.NewObservation(seedEtag: true));
                CatalogSessionHttpsFixture.Error(reply, HttpStatusCode.Unauthorized, "identityRejected");
                CatalogSessionHttpsFixture.NoBodyOrBusiness(reply);
            });
    }

    [PostgresFact]
    public Task ActualHttpsDisconnectCancelsOwnedSessionObservationBeforeCatalogOrRenewal() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync(); var gate = new CatalogSessionObserveGate(); test.ObserveGate = gate;
            var record = test.NewObservation(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = test.SendAsync(CatalogSessionHttpsFixture.ContextPath, cookies, cancellationToken: cancel.Token, observation: record);
            Task<CurrentServerSession?>? observation = null;
            try
            {
                await Task.WhenAll(gate.Entered.Task, gate.Observation.Task).WaitAsync(TimeSpan.FromSeconds(5));
                observation = gate.Observation.Task.Result;
                cancel.Cancel(); bool canceled = false;
                try { await pending; } catch (OperationCanceledException) { canceled = true; }
                try { await Task.WhenAll(record.Ended.Task, observation).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                CatalogSessionHttpsFixture.Check(observation.IsCanceled,
                    "The original owned session observation did not reach a canceled terminal state.");
                CatalogSessionHttpsFixture.Check(canceled && record.Canceled && Volatile.Read(ref gate.Canceled) == 1
                    && record.CatalogReads == 0 && record.SessionRenews == 0 && record.BodyReads == 0,
                    "The actual disconnected request did not cancel and settle before catalog/renewal/body work.");
            }
            finally
            {
                gate.Release.TrySetResult(); test.ObserveGate = null;
                if (observation is null && gate.Observation.Task.IsCompletedSuccessfully)
                    observation = gate.Observation.Task.Result;
                await DrainAsync(observation is null ? pending : Task.WhenAll(pending, observation));
            }
        });

    [PostgresFact]
    public Task ExistingDevelopmentContextAndAntiforgeryUnsafeRequestRemainOperational() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            await using var development = await CatalogHttpTestSupport.StartAsync(test.Data.Case);
            using var response = await development.Client.GetAsync(CatalogSessionHttpsFixture.ContextPath);
            var context = await CatalogHttpTestSupport.JsonAsync(response);
            CatalogSessionHttpsFixture.Check(context.GetProperty("mode").GetString() == "development", "The existing Development composition changed its identity mode.");
            string token = context.GetProperty("requestToken").GetString()!;
            using var accepted = await development.UnsafeAsync(token, development.Origin.GetLeftPart(UriPartial.Authority));
            CatalogSessionHttpsFixture.Check(accepted.StatusCode == HttpStatusCode.OK && development.UnsafeBodyReadCount == 1
                && development.UnsafeMutationCount == 1, "The existing Development token/unsafe admission no longer works independently.");
        });

    private static async Task DrainAsync(Task operation)
    { try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception) { } }
}
