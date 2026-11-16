using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.WebUtilities;
using Braid.Application;
using Braid.Identity;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class BrowserReturnPathFlowTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData(false, "unicode")]
    [InlineData(true, "unicode")]
    [InlineData(false, "cursor")]
    [InlineData(true, "cursor")]
    [InlineData(false, "field")]
    [InlineData(true, "field")]
    [InlineData(false, "rule")]
    [InlineData(true, "rule")]
    public Task OrdinaryAndInvitationFlowsPreserveExactBoundedSearchReturn(bool invitation, string kind) =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            var target = Target(kind);
            Assert.True(BrowserReturnPath.IsValid(target));
            var secret = invitation ? await AdministrationEnrollmentHttpsTests.InviteAsync(test) : null;
            var flow = await test.FlowAsync(invitation: secret, returnTo: target);
            var query = QueryHelpers.ParseQuery(flow.Callback[flow.Callback.IndexOf('?')..]);
            Assert.InRange(query["state"].ToString().Length, 1, BrowserReturnPath.MaximumProtectedStateLength);
            Assert.InRange(flow.Callback.Length, 1, 16384);
            if (kind is "field" or "rule") Assert.True(query["state"].ToString().Length > 8192,
                "The long retained-resource case must exercise the expanded protected-state bound.");
            var reply = await test.CompleteAsync(flow);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.SeeOther && reply.Record.Creates == 1,
                "The bounded search return did not complete one acknowledged session.");
            AuthHttpFixture.Check(reply.Headers.TryGetValue("Location", out var location) && location.SequenceEqual([target]),
                "The protected search return path or resource selection changed.");
            AuthHttpFixture.Check((await test.StatusAsync(flow.Jar)).State == "authenticated", "The returned session was not admitted.");
            if (invitation)
                Assert.Equal(1L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE operation='inviteRedeem'"));
        }, bindIssuer: !invitation, administration: invitation);

    [PostgresFact]
    public Task UnsafeSearchReturnHeadersRefuseBeforeStartingProviderWork() => AuthHttpFixture.RunAsync(fixture, async test =>
    {
        var jar = new AuthHttpJar();
        var status = await test.StatusAsync(jar);
        foreach (var target in new[]
        {
            "/catalog/search?q=a&q=b", "/catalog/search?q=a&returnTo=https%3A%2F%2Fevil.example",
            "/catalog/%2f%2fevil.example", "/catalog/search?q=%0D%0A", "/catalog/search?q=%ED%A0%80",
            "/catalog/search?q=a&cursor=" + new string('A', 4097),
            Revision + "?projectionDerivationId=" + Projection + "#node:wrong"
        })
        {
            var reply = await test.SendAsync(AuthHttpFixture.LoginPath, jar, "POST", status.Action, status.Epoch,
                mutate: request => request.Headers.Add("X-Braid-Return-To", target));
            AuthHttpFixture.Error(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AuthHttpFixture.NoLaterWork(reply);
            Assert.False(reply.Headers.ContainsKey("Set-Cookie"));
        }
    });

    internal const string Projection = "50000000-0000-4000-8000-000000000001";
    internal const string Revision = "/catalog/organizations/10000000-0000-4000-8000-000000000001/projects/20000000-0000-4000-8000-000000000001/mappings/30000000-0000-4000-8000-000000000001/revisions/40000000-0000-4000-8000-000000000001";

    internal static string Target(string kind)
    {
        if (kind == "unicode") return "/catalog/search?q=caf%C3%A9%20Orders%2FOrder%2FId";
        if (kind is "field" or "rule")
            return Revision + "?projectionDerivationId=" + Projection + "#" + kind + ":" + Uri.EscapeDataString(new string('é', 1024));
        var query = new CatalogSearchQuery(new string('é', 128));
        var key = new CatalogSearchKey(CatalogSearchKind.Field,
            new(Guid.Parse("10000000-0000-4000-8000-000000000001"), Guid.Parse("20000000-0000-4000-8000-000000000001")),
            Guid.Parse("30000000-0000-4000-8000-000000000001"), Guid.Parse("40000000-0000-4000-8000-000000000001"),
            new string('é', 1024), Guid.Parse(Projection));
        var cursor = CatalogSearchCursorCodec.Encode(new(query, key));
        Assert.True(cursor.Length > 1024);
        return "/catalog/search?q=" + Uri.EscapeDataString(query.Text) + "&cursor=" + cursor;
    }
}
