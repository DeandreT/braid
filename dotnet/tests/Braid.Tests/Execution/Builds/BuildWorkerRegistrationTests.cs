using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Braid.Deployment;
using Braid.Jobs;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class BuildWorkerRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Disabled_build_dispatch_reads_no_credentials_and_registers_nothing(string? flag)
    {
        var services = new ServiceCollection();
        var reads = new List<string>();
        services.AddBraidBuildWorker(name =>
        {
            reads.Add(name);
            if (name != "BRAID_BUILD_WORKER_ENABLED") throw new InvalidOperationException("Unexpected credential read.");
            return flag;
        }, () => throw new InvalidOperationException("Unexpected compiler creation."));
        Assert.Equal(["BRAID_BUILD_WORKER_ENABLED"], reads);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData(" false")]
    public void Unknown_enablement_is_not_an_implicit_activation(string value)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddBraidBuildWorker(_ => value, Options));
        Assert.Empty(services);
    }

    [Fact]
    public void Enabled_configuration_requires_every_profile_before_registering_services()
    {
        var names = new[] { "BRAID_BUILD_DISPATCHER_CONNECTION_STRING", "BRAID_BUILD_WRITER_CONNECTION_STRING",
            "BRAID_BUILD_ACCESS_CONNECTION_STRING", "BRAID_BUILD_REPORT_READER_CONNECTION_STRING",
            "BRAID_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING", "BRAID_BUILD_CASE_READER_CONNECTION_STRING" };
        foreach (var absent in names)
        {
            var services = new ServiceCollection();
            Assert.Throws<ArgumentException>(() => services.AddBraidBuildWorker(name =>
                name == "BRAID_BUILD_WORKER_ENABLED" ? "true" : name == absent ? null : "Host=127.0.0.1;Database=fixture", Options));
            Assert.Empty(services);
        }
    }

    [BuildWorkerLinuxFact]
    public void Enabled_registration_is_explicit_and_does_not_connect_or_launch_during_registration()
    {
        var services = new ServiceCollection();
        services.AddBraidBuildWorker(name => name == "BRAID_BUILD_WORKER_ENABLED" ? "true" : "Host=127.0.0.1;Database=fixture", Options);
        Assert.Single(services, item => item.ServiceType == typeof(IBuildRunStore));
        Assert.Single(services, item => item.ServiceType == typeof(IBuildRunQueue));
        Assert.Single(services, item => item.ServiceType == typeof(IBuildRunExecution));
        Assert.Contains(services, item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(BuildWorkerService));
    }

    [BuildWorkerLinuxFact]
    public void Preview_sandbox_settings_cannot_select_the_compiler_transport()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddBraidBuildWorker(name =>
            name == "BRAID_BUILD_WORKER_ENABLED" ? "true" : "Host=127.0.0.1;Database=fixture",
            () => new DockerInspectionWorkerOptions("/usr/bin/docker", "sha256:" + new string('a', 64))));
        Assert.Empty(services);
    }
    private static DockerInspectionWorkerOptions Options() =>
        new("/usr/bin/docker", "sha256:" + new string('a', 64), profile: DockerWorkerProfile.Build);
}

public sealed class BuildWorkerLinuxFactAttribute : FactAttribute
{
    public BuildWorkerLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Compiler worker options require the qualified Linux path and sandbox profile.";
    }
}
