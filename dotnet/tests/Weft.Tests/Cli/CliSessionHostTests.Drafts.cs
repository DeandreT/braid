using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Cli;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests.Cli;

public sealed partial class CliSessionHostTests
{
    [PostgresFact]
    public Task DraftOperationsUseTheRealSessionHostAndCurrentAuthorAuthority() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync(); await cli.LoginAsync();
        var scope = test.Data.Original.Facts.FutureRevision;
        var mapping = new MappingScope(scope.OrganizationId, scope.ProjectId, scope.MappingId);
        var store = test.First.Services.GetRequiredService<IMappingDraftStore>();
        var immutableBase = (await store.SelectBaseAsync(test.Data.Actor, mapping, scope.RevisionId, false))!;
        var draft = (await store.CreateAsync(test.Data.Actor, mapping, Guid.NewGuid(), immutableBase, MappingDraftPersistenceTests.Checkpoint(scope))).Draft!;
        string[] args = ["--project", scope.ProjectId.ToString("D"), "--mapping", scope.MappingId.ToString("D")];
        var list = await cli.RunAsync(["drafts", "list", .. args, "--json"]);
        Assert.True(list.Exit == 0, list.ToString()); Assert.Contains(draft.Id.ToString("D"), list.Out, StringComparison.Ordinal);
        var show = await cli.RunAsync(["drafts", "show", draft.Id.ToString("D"), .. args]);
        Assert.True(show.Exit == 0, show.ToString());
        var put = await cli.RunWithInputAsync(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind), ["drafts", "ops", "put", draft.Id.ToString("D"), "-", .. args]);
        Assert.True(put.Exit == 0, put.ToString());
        var stored = (await store.ReadAsync(test.Data.Actor, mapping, draft.Id))!;
        Assert.Equal(1, stored.Version); Assert.Equal(1, stored.OperationCount);
        await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
        var hidden = await cli.RunAsync(["drafts", "show", draft.Id.ToString("D"), .. args]);
        Assert.Equal(CliExitCode.NotFound, hidden.Code);
        Assert.All(records, record => Assert.False(record.Unexpected));
    }, bearerBurst: 100, configureExtra: (test, services) =>
    {
        services.AddSingleton(new CatalogMappingEditOptions(true));
        services.AddKeyedSingleton("draft-reader-test", (_, _) => new PostgresMappingDraftDatabase(test.Data.Case.RegressionConnectionString("weft_draft_reader"), MappingDraftDatabaseProfile.Reader));
        services.AddKeyedSingleton("draft-writer-test", (_, _) => new PostgresMappingDraftDatabase(test.Data.Case.RegressionConnectionString("weft_draft_writer"), MappingDraftDatabaseProfile.Writer));
        services.AddSingleton<IMappingDraftStore>(provider => new PostgresMappingDraftStore(provider.GetRequiredKeyedService<PostgresMappingDraftDatabase>("draft-reader-test"), provider.GetRequiredKeyedService<PostgresMappingDraftDatabase>("draft-writer-test")));
        services.AddSingleton(provider => new MappingDraftService(provider.GetRequiredService<IMappingDraftStore>(), new MappingDraftPersistenceTests.DraftTestObjects(), new MappingDraftPersistenceTests.DraftTestBackend(), new MappingDraftPersistenceTests.DraftTestIngestion()));
    });
}
