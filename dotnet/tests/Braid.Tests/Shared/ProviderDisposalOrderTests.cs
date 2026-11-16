using Microsoft.Extensions.DependencyInjection;

namespace Braid.Tests;

public sealed class ProviderDisposalOrderTests
{
    [Fact]
    public async Task Provider_disposal_can_yield_before_stopping_an_earlier_singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ProviderDisposalGate());
        services.AddKeyedSingleton("later", (_, _) => new ProviderDisposalGate());
        var provider = services.BuildServiceProvider();
        var earlier = provider.GetRequiredService<ProviderDisposalGate>();
        var later = provider.GetRequiredKeyedService<ProviderDisposalGate>("later");
        var disposal = provider.DisposeAsync().AsTask();
        try
        {
            await later.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(earlier.Entered.IsCompleted);
            Assert.False(disposal.IsCompleted);
            later.Release();
            await earlier.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            later.Release();
            earlier.Release();
            await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }
}
