using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Weft.Tests;

/// <summary>Explicit small subnets for test-owned Docker networks, chosen outside the daemon's default pools.</summary>
/// <remarks>
/// A network created without a subnet takes a whole /16 or /20 from the daemon's default pools, which a few
/// concurrent suites exhaust. The default range is the upper half of the RFC 2544 benchmarking block: neither
/// those pools nor private LANs, CGNAT/Tailscale (100.64.0.0/10) or the common fake-IP proxy range
/// (198.18.0.0/16) use it.
/// </remarks>
internal static class TestNetworkSubnets
{
    public const string RangeVariable = "WEFT_TEST_SUBNET_RANGE";
    public const string DefaultRange = "198.19.0.0/16";
    public const int PrefixLength = 28;
    public const int MaximumAttempts = 32;

    /// <summary>Creates a network on distinct random candidates until one does not overlap an existing network.</summary>
    /// <param name="create">Creates the network on a subnet; returns false only when Docker reported an overlapping pool.</param>
    public static async Task<string> CreateAsync(Func<string, Task<bool>> create, string? range = null,
        Random? random = null, int prefixLength = PrefixLength)
    {
        range ??= ConfiguredRange;
        random ??= Random.Shared;
        var (first, count) = Candidates(range, prefixLength);
        var attempts = (int)Math.Min(count, MaximumAttempts);
        var tried = new HashSet<long>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            long index;
            do index = random.NextInt64(count);
            while (!tried.Add(index));
            var subnet = $"{Format(first + ((uint)index << (32 - prefixLength)))}/{prefixLength}";
            if (await create(subnet))
                return subnet;
        }
        throw new TestSubnetsExhaustedException(
            $"No free /{prefixLength} subnet for a test Docker network in {range}: Docker reported each of {attempts} candidates "
            + $"as overlapping an existing network. Remove leaked test networks or set {RangeVariable} to a free IPv4 range.");
    }

    public static string ConfiguredRange => Environment.GetEnvironmentVariable(RangeVariable) is { Length: > 0 } configured
        ? configured
        : DefaultRange;

    /// <summary>Docker's refusal of a subnet that overlaps another network's pool.</summary>
    public static bool IsOverlap(string dockerError) =>
        dockerError.Contains("Pool overlaps", StringComparison.OrdinalIgnoreCase)
        || dockerError.Contains("invalid pool request", StringComparison.OrdinalIgnoreCase);

    internal static bool Contains(string range, string subnet)
    {
        var (first, count) = Candidates(range, PrefixLength);
        if (!IPNetwork.TryParse(subnet, out var network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var address = BinaryPrimitives.ReadUInt32BigEndian(network.BaseAddress.GetAddressBytes());
        return network.PrefixLength >= PrefixLength && address >= first && address - first < (ulong)count << (32 - PrefixLength);
    }

    private static (uint First, long Count) Candidates(string range, int prefixLength)
    {
        if (!IPNetwork.TryParse(range, out var network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork
            || network.PrefixLength > prefixLength || range != network.ToString())
            throw new InvalidOperationException(
                $"{RangeVariable} must be an IPv4 network of /{prefixLength} or larger in canonical form, such as {DefaultRange}.");
        return (BinaryPrimitives.ReadUInt32BigEndian(network.BaseAddress.GetAddressBytes()), 1L << (prefixLength - network.PrefixLength));
    }

    private static string Format(uint address)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes).ToString();
    }
}

/// <summary>Every candidate in the configured test subnet range overlapped an existing Docker network.</summary>
internal sealed class TestSubnetsExhaustedException(string message) : InvalidOperationException(message);
