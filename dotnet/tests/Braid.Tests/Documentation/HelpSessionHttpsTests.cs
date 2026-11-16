using System.Net;
using System.Runtime.Versioning;

namespace Braid.Tests;

/// <summary>The help routes behind actual Session admission over owned HTTPS and restricted PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class HelpSessionHttpsTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task SessionAdmissionAndFreshOrganizationAuthorityGateTheGuide() =>
        CatalogSessionHttpsFixture.RunAsync(fixture, async test =>
        {
            var cookies = await test.IssueAsync();
            var list = await test.SendAsync("/api/v1/help/sections", cookies);
            CatalogSessionHttpsFixture.Check(list.Status == HttpStatusCode.OK && list.NoStore && list.Record.CatalogReads == 1
                && list.Json().GetProperty("sections")[0].GetProperty("id").GetString() == "overview",
                "The session guide list did not use session admission and one fresh organization read.");
            var section = await test.SendAsync("/api/v1/help/sections/sign-in", cookies);
            CatalogSessionHttpsFixture.Check(section.Status == HttpStatusCode.OK && section.NoStore && section.Record.CatalogReads == 1
                && section.Json().GetProperty("id").GetString() == "sign-in", "The session guide section was not served.");
            CatalogSessionHttpsFixture.Error(await test.SendAsync("/api/v1/help/sections/missing-section", cookies),
                HttpStatusCode.NotFound, "notFound");
            var absent = await test.SendAsync("/api/v1/help/sections", new CatalogSessionCookies());
            CatalogSessionHttpsFixture.Error(absent, HttpStatusCode.Unauthorized, "identityRejected");
            CatalogSessionHttpsFixture.NoBodyOrBusiness(absent);
            var tampered = cookies.Copy(); tampered.Session = "invalid-owned-carrier";
            var rejected = await test.SendAsync("/api/v1/help/sections/overview", tampered);
            CatalogSessionHttpsFixture.Error(rejected, HttpStatusCode.Unauthorized, "identityRejected");
            CatalogSessionHttpsFixture.NoBodyOrBusiness(rejected);
            await test.SetOrganizationAsync(false);
            var denied = await test.SendAsync("/api/v1/help/sections/overview", cookies);
            CatalogSessionHttpsFixture.Error(denied, HttpStatusCode.NotFound, "notFound");
            CatalogSessionHttpsFixture.Check(denied.Record.CatalogReads == 1, "Organization revocation was not read freshly.");
        });
}
