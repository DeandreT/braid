using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;
using Weft.Server;

namespace Weft.Tests;

public sealed class RevisionReportHostAdmissionTests
{
    [Fact]
    public async Task SeparateHostProvidersNeverShareTheirKnownRunnerAdmission()
    {
        var services = new ServiceCollection();
        InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => new UnusedConnector(), FerruleReportTestData.Image);
        await using var first = services.BuildServiceProvider();
        await using var second = services.BuildServiceProvider();
        var firstAdmission = first.GetRequiredService<InspectionWorkerAdmission>();
        var secondAdmission = second.GetRequiredService<InspectionWorkerAdmission>();
        Assert.NotSame(firstAdmission, secondAdmission);
        var runner = Assert.IsType<FerruleServiceRunner>(first.GetRequiredService<IInspectionWorkerRunner>());
        await runner.StopAsync();
        Assert.False(firstAdmission.CanExecute);
        Assert.True(secondAdmission.CanExecute);
        Assert.True(Assert.IsType<FerruleServiceRunner>(second.GetRequiredService<IInspectionWorkerRunner>()).Available);
    }

    [Fact]
    public async Task ReportRegistrationObservesTheHostsKnownRunnerWithoutResolvingAnUnusedRunnerOrBackend()
    {
        var resolutions = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        InspectionWorkerHostConfiguration.ConfigureConnector(builder.Services, _ =>
        {
            resolutions++;
            return new UnusedConnector();
        }, FerruleReportTestData.Image);
        CatalogRevisionReportHostConfiguration.Configure(builder, new(true, true),
            name => name == "WEFT_FERRULE_WORKER_IMAGE" ? FerruleReportTestData.Image : null);
        await using var provider = builder.Services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IRevisionReportProfileRegistry>();
        var projection = registry.FindCurrent("ferrule", RevisionReportOperation.Projection)!;
        var validation = registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!;
        Assert.True(projection.CanExecute);
        Assert.True(validation.CanExecute);
        Assert.Equal(0, resolutions);

        var runner = Assert.IsType<FerruleServiceRunner>(provider.GetRequiredService<IInspectionWorkerRunner>());
        Assert.Equal(1, resolutions);
        await runner.StopAsync();
        Assert.False(projection.CanExecute);
        Assert.False(validation.CanExecute);
        Assert.Equal(1, resolutions);
    }

    private sealed class UnusedConnector : IFerruleServiceConnector
    {
        public IFerruleServiceChannel Open() => throw new InvalidOperationException("Admission observation must not open a channel.");
    }
}
