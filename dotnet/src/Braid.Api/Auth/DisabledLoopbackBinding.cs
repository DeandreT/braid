using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace Braid.Api.Auth;

/// <summary>Only the noncredential Disabled port-zero profile may admit an actual bound origin.</summary>
internal sealed class DisabledLoopbackBinding(AuthenticationHttpOptions options, IServer server) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (options.Mode != AuthenticationHttpMode.Disabled || options.DisabledListenerAddress is null ||
            addresses is null || addresses.Count != 1) throw Failure();
        var value = addresses.Single();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) ||
            !address.Equals(options.DisabledListenerAddress)) throw Failure();
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address + "]" : address.ToString();
        var origin = "http://" + host + (uri.Port == 80 ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        cancellationToken.ThrowIfCancellationRequested();
        options.PublishDisabledOrigin(origin);
        return Task.CompletedTask;
    }
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private static InvalidOperationException Failure() => new("The disabled host binding is invalid.");
}
