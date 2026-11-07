using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>The PostgreSQL reads the workspace tools add: identity location and submitter-only preview history and inputs.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresWorkspaceReadsTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task The_locator_resolves_identities_only_in_projects_the_actor_can_read_now()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var own = await catalog.CommitCreateAsync("Located mapping");
        var appended = await catalog.CommitAppendAsync(own);
        var administrator = new ActorContext(new(catalog.Seed.OtherProjectAdmin));
        var hidden = await catalog.CommitCreateAsync("Hidden mapping", project: new(catalog.Seed.OrganizationA, catalog.Seed.HiddenProjectA),
            actor: administrator);
        var organization = catalog.Seed.OrganizationA;
        var mapping = PostgresCatalogTestSupport.Target(own);
        var reader = catalog.Reader;
        Assert.Equal(mapping, await reader.LocateMappingAsync(catalog.Actor, organization, mapping.MappingId));
        Assert.Equal(own.Facts.FutureRevision, await reader.LocateRevisionAsync(catalog.Actor, organization, own.Facts.FutureRevision.RevisionId));
        Assert.Equal(appended.Facts.FutureRevision, await reader.LocateRevisionAsync(catalog.Actor, organization, appended.Facts.FutureRevision.RevisionId));

        // A project the actor is not a member of, another organization and the wrong kind of identity are all absent.
        var hiddenMapping = PostgresCatalogTestSupport.Target(hidden);
        Assert.Null(await reader.LocateMappingAsync(catalog.Actor, organization, hiddenMapping.MappingId));
        Assert.Null(await reader.LocateRevisionAsync(catalog.Actor, organization, hidden.Facts.FutureRevision.RevisionId));
        Assert.Equal(hiddenMapping, await reader.LocateMappingAsync(administrator, organization, hiddenMapping.MappingId));
        Assert.Null(await reader.LocateMappingAsync(catalog.Actor, catalog.Seed.OrganizationB, mapping.MappingId));
        Assert.Null(await reader.LocateRevisionAsync(catalog.Actor, catalog.Seed.OrganizationB, own.Facts.FutureRevision.RevisionId));
        Assert.Null(await reader.LocateMappingAsync(catalog.Actor, organization, own.Facts.FutureRevision.RevisionId));
        Assert.Null(await reader.LocateRevisionAsync(catalog.Actor, organization, mapping.MappingId));
        Assert.Null(await reader.LocateMappingAsync(catalog.Actor, organization, Guid.NewGuid()));

        // Authority is observed freshly on every call.
        await catalog.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            catalog.Seed.Alice, catalog.Seed.ProjectA);
        Assert.Null(await reader.LocateMappingAsync(catalog.Actor, organization, mapping.MappingId));
        Assert.Null(await reader.LocateRevisionAsync(catalog.Actor, organization, own.Facts.FutureRevision.RevisionId));
        await catalog.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2",
            catalog.Seed.OtherProjectAdmin, organization);
        Assert.Null(await reader.LocateMappingAsync(administrator, organization, hiddenMapping.MappingId));
    }

    [PostgresFact]
    public async Task Preview_history_and_inputs_are_the_submitters_own_under_fresh_authority()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var service = test.Service();
        var runs = new List<Guid>();
        for (var index = 0; index < 3; index++) runs.Add((await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value);
        var organization = test.Revision.OrganizationId;
        var mapping = new MappingScope(organization, test.Revision.ProjectId, test.Revision.MappingId);

        var all = await test.Reader.ListAsync(test.Alice, mapping, 10);
        Assert.Equal(runs.Order(), all.Runs.Select(run => run.RunId).Order());
        Assert.Null(all.NextRunId);
        Assert.All(all.Runs, run => Assert.Equal(test.Revision, run.Revision));
        // Newest first by creation, then identity; pages continue exactly after the previous page.
        Assert.Equal(all.Runs.OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.RunId.ToString("D"), StringComparer.Ordinal)
            .Select(run => run.RunId), all.Runs.Select(run => run.RunId));
        var paged = new List<Guid>();
        Guid? after = null;
        do
        {
            var page = await test.Reader.ListAsync(test.Alice, mapping, 2, after);
            paged.AddRange(page.Runs.Select(run => run.RunId));
            after = page.NextRunId;
        } while (after is not null);
        Assert.Equal(all.Runs.Select(run => run.RunId), paged);
        Assert.Empty((await test.Reader.ListAsync(test.Alice, mapping, 10, Guid.NewGuid())).Runs);

        var inputs = await test.Reader.ReadInputDescriptorsAsync(test.Alice, organization, runs[0]);
        Assert.Equal(new (int, string?, int)[] { (0, null, PreviewRunTestSupport.Order.Length), (1, "rates", PreviewRunTestSupport.Rates.Length) },
            inputs!.Select(input => (input.Ordinal, input.Name, input.ByteCount)));
        var primary = await test.Reader.ReadInputAsync(test.Alice, organization, runs[0], 0);
        Assert.Equal(PreviewRunTestSupport.Order, primary!.Content);
        Assert.Equal(inputs![0].Digest, primary.Digest);
        Assert.Equal(PreviewRunTestSupport.Rates, (await test.Reader.ReadInputAsync(test.Alice, organization, runs[0], 1))!.Content);
        Assert.Null(await test.Reader.ReadInputAsync(test.Alice, organization, runs[0], 2));

        // Another member of the organization sees none of them.
        var other = new ActorContext(new(test.Catalog.Seed.Admitted));
        Assert.Empty((await test.Reader.ListAsync(other, mapping, 10)).Runs);
        Assert.Null(await test.Reader.ReadInputDescriptorsAsync(other, organization, runs[0]));
        Assert.Null(await test.Reader.ReadInputAsync(other, organization, runs[0], 0));
        Assert.Null(await test.Reader.ReadInputDescriptorsAsync(test.Alice, test.Catalog.Seed.OrganizationB, runs[0]));

        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            test.Alice.PrincipalId.Value, test.Revision.ProjectId);
        Assert.Empty((await test.Reader.ListAsync(test.Alice, mapping, 10)).Runs);
        Assert.Null(await test.Reader.ReadInputDescriptorsAsync(test.Alice, organization, runs[0]));
        Assert.Null(await test.Reader.ReadInputAsync(test.Alice, organization, runs[0], 0));
    }
}
