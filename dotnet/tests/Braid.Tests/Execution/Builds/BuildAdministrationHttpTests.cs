using System.Net;
using System.Text.Json;
using Braid.Api.Catalog;
using Braid.Deployment;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildAdministrationHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Administrator_creates_the_next_version_and_retained_replay_never_reactivates_an_old_version()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var host = await BuildHttpTests.Host.StartAsync(data);
        var token = await host.TokenAsync();
        using (var created = await host.PutAsync(EnvironmentPath(host, data.Intent, 2), Body(data.Intent.Profile), CatalogBuildRequest.EnvironmentMediaType, token))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var replayed = await host.PutAsync(EnvironmentPath(host, data.Intent, 1), Body(data.Intent.Profile), CatalogBuildRequest.EnvironmentMediaType, token))
        {
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            var body = await Json(replayed);
            Assert.Equal("replayed", body.GetProperty("outcome").GetString());
            Assert.False(body.GetProperty("environment").GetProperty("active").GetBoolean());
        }
        var conflict = JsonSerializer.Serialize(new { profileId = data.Intent.Profile.Id, profileVersion = "999" });
        using (var response = await host.PutAsync(EnvironmentPath(host, data.Intent, 1), conflict, CatalogBuildRequest.EnvironmentMediaType, token))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (var response = await host.PutAsync(EnvironmentPath(host, data.Intent, 3), conflict, CatalogBuildRequest.EnvironmentMediaType, token))
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(2L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments"));
        Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments WHERE active"));
        using var capabilities = await host.Client.GetAsync(ProjectPath(host) + "/build-environment-capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.True((await Json(capabilities)).GetProperty("canCreateVersions").GetBoolean());
    }

    [RegressionPostgresTheory]
    [InlineData("publisher")]
    [InlineData("viewer")]
    public async Task Non_administrators_never_read_environment_bodies_or_resolve_build_ports(string role)
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await data.Runs.Cases.RoleAsync(role);
        await using var host = await BuildHttpTests.Host.StartAsync(data);
        var token = await host.TokenAsync();
        using var denied = await host.PutAsync(EnvironmentPath(host, data.Intent, 2), "{", CatalogBuildRequest.EnvironmentMediaType, token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, host.BodyReads); Assert.Equal(0, host.MetadataResolutions);
    }

    [PostgresFact]
    public async Task Administrator_removal_during_body_read_denies_before_build_port_resolution()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var host = await BuildHttpTests.Host.StartAsync(data);
        var token = await host.TokenAsync();
        host.AfterBody = () => data.Runs.Cases.RoleAsync("publisher");
        using var denied = await host.PutAsync(EnvironmentPath(host, data.Intent, 2), Body(data.Intent.Profile), CatalogBuildRequest.EnvironmentMediaType, token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.True(host.BodyReads > 0); Assert.Equal(0, host.MetadataResolutions); Assert.Equal(0, host.AdministrationCommands);
        Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments"));
    }

    [PostgresFact]
    public async Task Concurrent_identical_versions_have_one_creation_and_one_retained_replay()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var host = await BuildHttpTests.Host.StartAsync(data);
        var token = await host.TokenAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => host.PutAsync(EnvironmentPath(host, data.Intent, 2),
            Body(data.Intent.Profile), CatalogBuildRequest.EnvironmentMediaType, token)));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Created }, responses.Select(value => value.StatusCode).OrderBy(value => (int)value));
            Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments WHERE version=2"));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [PostgresFact]
    public async Task Lost_version_reply_recovers_the_original_profile_after_restart_and_current_profile_change()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using (var original = await BuildHttpTests.Host.StartAsync(data))
        {
            var token = await original.TokenAsync(); original.DropNextReply = 1;
            await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            {
                using var lost = await original.PutAsync(EnvironmentPath(original, data.Intent, 2), Body(data.Intent.Profile), CatalogBuildRequest.EnvironmentMediaType, token);
            });
        }
        var profile = data.Intent.Profile;
        var changed = new BuildRuntimeProfile(profile.Id, profile.Version + 1, profile.Runtime, "changed-runtime", profile.ArtifactFormat, profile.MaximumArtifactBytes);
        await using var restarted = await BuildHttpTests.Host.StartAsync(data, selectedProfile: changed);
        var renewed = await restarted.TokenAsync();
        using var response = await restarted.PutAsync(EnvironmentPath(restarted, data.Intent, 2), Body(profile), CatalogBuildRequest.EnvironmentMediaType, renewed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(profile.RuntimeVersion, (await Json(response)).GetProperty("environment").GetProperty("profile").GetProperty("runtimeVersion").GetString());
        Assert.Equal(2L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments"));
    }

    [PostgresFact]
    public async Task Own_revocation_is_idempotent_and_does_not_renew_build_admission()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using var host = await BuildHttpTests.Host.StartAsync(data);
        var token = await host.TokenAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, host.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D") + "/revoke");
            request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            using var revoked = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.True((await Json(revoked)).GetProperty("revoked").GetBoolean());
        }
        using var denied = await host.PutAsync(host.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D"),
            JsonSerializer.Serialize(new { approvalId = data.Intent.ApprovalId.ToString("D") }), CatalogBuildRequest.RunMediaType, token);
        Assert.Equal(HttpStatusCode.PreconditionFailed, denied.StatusCode);
        Assert.Equal(0L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_runs"));
    }

    [PostgresFact]
    public async Task A_different_administrator_cannot_revoke_another_actors_approval_through_the_own_route()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        var seed = data.Runs.Cases.Seed;
        await seed.AddMembershipAsync(data.Runs.Cases.Case, seed.OrganizationA, seed.ProjectA, seed.OtherProjectAdmin, "administrator");
        await using var host = await BuildHttpTests.Host.StartAsync(data, principalOverride: seed.OtherProjectAdmin);
        var token = await host.TokenAsync();
        using var denied = await RevokeAsync(host, data.Intent.ApprovalId, token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.Equal(0, host.AdministrationCommands);
        Assert.False(await data.Runs.Cases.Case.ScalarAsync<bool>("SELECT revoked FROM braid.build_approvals"));
    }

    [PostgresFact]
    public async Task Lost_revocation_reply_recovers_after_restart_without_issuing_another_command()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using (var original = await BuildHttpTests.Host.StartAsync(data))
        {
            var token = await original.TokenAsync(); original.DropNextReply = 1;
            await Assert.ThrowsAnyAsync<HttpRequestException>(async () => { using var lost = await RevokeAsync(original, data.Intent.ApprovalId, token); });
        }
        await using var restarted = await BuildHttpTests.Host.StartAsync(data, changedRuntime: true);
        var renewed = await restarted.TokenAsync();
        using var recovered = await RevokeAsync(restarted, data.Intent.ApprovalId, renewed);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.True((await Json(recovered)).GetProperty("revoked").GetBoolean());
        Assert.Equal(0, restarted.AdministrationCommands);
    }

    private static string ProjectPath(BuildHttpTests.Host host) => host.MappingPath[..host.MappingPath.IndexOf("/mappings/", StringComparison.Ordinal)];
    private static string EnvironmentPath(BuildHttpTests.Host host, BuildIntent intent, long version) => ProjectPath(host) +
        "/build-environments/" + intent.Environment.EnvironmentId.ToString("D") + "/versions/" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Body(BuildRuntimeProfile profile) => JsonSerializer.Serialize(new { profileId = profile.Id, profileVersion = profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    private static async Task<HttpResponseMessage> RevokeAsync(BuildHttpTests.Host host, Guid approval, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, host.MappingPath + "/build-approvals/" + approval.ToString("D") + "/revoke");
        request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
        return await host.Client.SendAsync(request);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()); return document.RootElement.Clone(); }
}
