using System.Diagnostics;

namespace Braid.Tests;

public sealed class TestNetworkSubnetsTests
{
    [Fact]
    public async Task Overlapping_candidates_are_retried_on_distinct_small_subnets_inside_the_range()
    {
        var offered = new List<string>();
        var subnet = await TestNetworkSubnets.CreateAsync(candidate =>
        {
            offered.Add(candidate);
            return Task.FromResult(offered.Count == 4);
        }, "198.19.0.0/16");
        Assert.Equal(4, offered.Count);
        Assert.Equal(offered[^1], subnet);
        Assert.Equal(offered.Count, offered.Distinct().Count());
        Assert.All(offered, candidate =>
        {
            Assert.EndsWith("/28", candidate);
            Assert.True(TestNetworkSubnets.Contains("198.19.0.0/16", candidate));
        });
    }

    [Fact]
    public async Task A_small_range_is_exhausted_after_each_candidate_is_tried_once()
    {
        var offered = new List<string>();
        var failure = await Assert.ThrowsAsync<TestSubnetsExhaustedException>(() => TestNetworkSubnets.CreateAsync(candidate =>
        {
            offered.Add(candidate);
            return Task.FromResult(false);
        }, "10.250.0.0/26"));
        Assert.Equal(["10.250.0.0/28", "10.250.0.16/28", "10.250.0.32/28", "10.250.0.48/28"], offered.Order(StringComparer.Ordinal));
        Assert.Contains("10.250.0.0/26", failure.Message, StringComparison.Ordinal);
        Assert.Contains(TestNetworkSubnets.RangeVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_crowded_large_range_stops_at_the_attempt_bound()
    {
        var offered = new HashSet<string>(StringComparer.Ordinal);
        await Assert.ThrowsAsync<TestSubnetsExhaustedException>(() => TestNetworkSubnets.CreateAsync(candidate =>
        {
            Assert.True(offered.Add(candidate));
            return Task.FromResult(false);
        }, "198.19.0.0/16"));
        Assert.Equal(TestNetworkSubnets.MaximumAttempts, offered.Count);
    }

    [Fact]
    public async Task Other_creation_failures_are_not_retried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => TestNetworkSubnets.CreateAsync(_ =>
        {
            attempts++;
            throw new IOException("network with name already exists");
        }, "198.19.0.0/16"));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("2001:db8::/64")]
    [InlineData("198.19.0.0/29")]
    [InlineData("198.19.0.1/16")]
    [InlineData("198.19.0.0")]
    [InlineData("not a range")]
    public async Task Invalid_ranges_are_refused_before_any_network_is_created(string range)
    {
        var attempts = 0;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestNetworkSubnets.CreateAsync(_ =>
        {
            attempts++;
            return Task.FromResult(true);
        }, range));
        Assert.Contains(TestNetworkSubnets.RangeVariable, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void Only_docker_pool_overlap_refusals_are_retried()
    {
        Assert.True(TestNetworkSubnets.IsOverlap("Error response from daemon: invalid pool request: Pool overlaps with other one on this address space"));
        Assert.False(TestNetworkSubnets.IsOverlap("Error response from daemon: network with name braid-pg-net-x already exists"));
        Assert.False(TestNetworkSubnets.IsOverlap("Error response from daemon: all predefined address pools have been fully subnetted"));
    }
}

public sealed class DockerTestNetworkFactAttribute : FactAttribute
{
    public DockerTestNetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BRAID_POSTGRES_IMAGE") is null)
            Skip = "Set BRAID_POSTGRES_IMAGE to exercise Docker test networks.";
    }
}

[Collection(PostgresCollection.Name)]
public sealed class DockerTestNetworkSubnetsTests
{
    private const string OwnerLabel = "braid.subnet-test-owner";
    private readonly string docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "docker";

    [DockerTestNetworkFact]
    public async Task Docker_overlap_refusals_move_to_the_next_candidate_and_exhaust_an_occupied_range()
    {
        var owner = Guid.NewGuid().ToString("N");
        var created = new List<string>();
        async Task<bool> Create(string subnet)
        {
            var name = $"braid-subnet-test-{owner}-{created.Count}";
            var result = await DockerAsync("network", "create", "--driver", "bridge", "--internal", "--subnet", subnet,
                "--label", $"{OwnerLabel}={owner}", name);
            if (result.ExitCode == 0)
            {
                created.Add(name);
                return true;
            }
            Assert.True(TestNetworkSubnets.IsOverlap(result.Error), result.Error);
            return false;
        }
        try
        {
            // Occupy a free /27 of the configured range: each /28 inside it overlaps.
            var block = await TestNetworkSubnets.CreateAsync(Create, prefixLength: 27);
            var offered = new List<string>();
            await Assert.ThrowsAsync<TestSubnetsExhaustedException>(() => TestNetworkSubnets.CreateAsync(subnet =>
            {
                offered.Add(subnet);
                return Create(subnet);
            }, block));
            Assert.Equal(2, offered.Distinct().Count());

            // With only the lower half occupied, the refused candidate moves on to the upper half.
            Assert.Equal(0, (await DockerAsync("network", "rm", created[0])).ExitCode);
            created.RemoveAt(0);
            var lower = block.Replace("/27", "/28", StringComparison.Ordinal);
            Assert.True(await Create(lower));
            offered.Clear();
            var chosen = await TestNetworkSubnets.CreateAsync(subnet =>
            {
                offered.Add(subnet);
                return Create(subnet);
            }, block, new OrderedRandom());
            Assert.Equal(new[] { lower, chosen }, offered);
            Assert.NotEqual(lower, chosen);
            Assert.True(TestNetworkSubnets.Contains(block, chosen));
        }
        finally
        {
            foreach (var name in created)
                await DockerAsync("network", "rm", name);
        }
    }

    [DockerTestNetworkFact]
    public async Task Owned_postgres_network_takes_a_small_subnet_from_the_range_and_is_removed_with_the_fixture()
    {
        var fixture = new PostgresFixture(provisionMetadataEditor: false);
        await fixture.InitializeAsync();
        string inspected;
        try
        {
            var result = await DockerAsync("network", "inspect", "--format", "{{range .IPAM.Config}}{{.Subnet}} {{end}}", fixture.Network);
            Assert.Equal(0, result.ExitCode);
            inspected = result.Output;
        }
        finally { await fixture.DisposeAsync(); }
        var subnet = Assert.Single(inspected.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.EndsWith("/28", subnet);
        Assert.True(TestNetworkSubnets.Contains(TestNetworkSubnets.ConfiguredRange, subnet));
        var absent = await DockerAsync("network", "inspect", fixture.Network);
        Assert.NotEqual(0, absent.ExitCode);
        Assert.Matches("(?i)not found|no such network", absent.Error);
    }

    private sealed class OrderedRandom : Random
    {
        private long next;

        public override long NextInt64(long maxValue) => next++ % maxValue;
    }

    private async Task<(int ExitCode, string Output, string Error)> DockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(docker) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
