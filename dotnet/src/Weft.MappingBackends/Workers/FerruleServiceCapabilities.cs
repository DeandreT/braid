using System.Net;
using System.Text.Json;

namespace Weft.MappingBackends.Workers;

/// <summary>Bounded capability proof on the operation's own channel, before any frame is sent.</summary>
internal static class FerruleServiceCapabilities
{
    internal static async Task<bool> VerifyAsync(IFerruleServiceChannel channel, string profile, string buildIdentity,
        string engineRevision, string replica, CancellationToken ct)
    {
        try { return await ReadAsync(channel, profile, buildIdentity, engineRevision, replica, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return false; }
    }

    private static async Task<bool> ReadAsync(IFerruleServiceChannel channel, string profile, string buildIdentity,
        string engineRevision, string replica, CancellationToken ct)
    {
        using var capsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(FerruleServiceProtocol.CapabilitiesPath, UriKind.Relative));
        using var caps = await channel.SendAsync(capsRequest, ct);
        if (caps.StatusCode != HttpStatusCode.OK ||
            !Header(caps, FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version) || !Header(caps, FerruleServiceProtocol.ReplicaHeader, replica) ||
            caps.Content.Headers.ContentType?.MediaType != "application/json" || caps.Content.Headers.ContentLength > 16_384) return false;
        await using var input = await caps.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 16_385 - (int)bytes.Length)), ct);
            if (count == 0) break;
            bytes.Write(buffer, 0, count); if (bytes.Length > 16_384) return false;
        }
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (!Unique(root) || root.GetProperty("protocol").GetString() != FerruleServiceProtocol.Version ||
            root.GetProperty("buildIdentity").GetString() != buildIdentity ||
            root.GetProperty("engineRevision").GetString() != engineRevision) return false;
        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();
        if (profiles.Length > 8 || profiles.Select(p => p.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count() != profiles.Length) return false;
        foreach (var name in new[] { profile })
        {
            var matches = profiles.Where(p => p.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) return false;
            var observed = matches[0]; var fixedProfile = FerruleServiceProtocol.Profile(name)!;
            if (observed.GetProperty("maximumRequestBytes").GetInt32() != fixedProfile.MaximumRequestBytes ||
                observed.GetProperty("maximumResponseBytes").GetInt32() != fixedProfile.MaximumOutputBytes ||
                observed.GetProperty("wallSeconds").GetInt64() != (long)fixedProfile.MaximumWallTimeout.TotalSeconds ||
                observed.GetProperty("cpuSeconds").GetInt32() != fixedProfile.CpuSeconds ||
                observed.GetProperty("memoryBytes").GetInt64() != fixedProfile.MemoryBytes || observed.TryGetProperty("toolchain", out _) ||
                observed.TryGetProperty("runtime", out _) || root.GetProperty("reserved").EnumerateArray().Any(p => p.GetString() == name)) return false;
        }
        return true;
    }

    private static bool Unique(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count() &&
            value.EnumerateObject().All(p => Unique(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(Unique),
        _ => true
    };
    private static string? SingleHeader(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) && values.Count() == 1 ? values.Single() : null;
    private static bool Header(HttpResponseMessage response, string name, string expected) => SingleHeader(response, name) == expected;
}
