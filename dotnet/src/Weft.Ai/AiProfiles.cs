using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Weft.Ai;

public readonly struct AiProfileKey : IEquatable<AiProfileKey>
{
    private readonly string? _id;
    private AiProfileKey(string id, int version) { _id = id; Version = version; }
    public string Id => _id ?? string.Empty;
    public int Version { get; }
    internal bool Valid => _id is not null && Version > 0;
    public static AiProfileKey Create(string id, int version)
    {
        AiGuard.Identifier(id);
        if (version <= 0) throw AiGuard.Invalid();
        return new AiProfileKey(id, version);
    }
    public bool Equals(AiProfileKey other) => Version == other.Version && StringComparer.Ordinal.Equals(_id, other._id);
    public override bool Equals(object? obj) => obj is AiProfileKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_id, Version);
    public override string ToString() => nameof(AiProfileKey);
    public static bool operator ==(AiProfileKey left, AiProfileKey right) => left.Equals(right);
    public static bool operator !=(AiProfileKey left, AiProfileKey right) => !left.Equals(right);
}

public sealed class AiProfileSpec
{
    private AiProfileSpec(AiProfileKey key, AiDialect dialect, string model, Uri endpoint,
        bool loopbackFixture, AiAuthentication authentication, string? credentialReference,
        AiCapability capabilities, string policy, int policyVersion, bool allowed, bool thinkFalse)
    {
        Key = key; Dialect = dialect; Model = model; Endpoint = endpoint; LoopbackFixture = loopbackFixture;
        Authentication = authentication; CredentialReference = credentialReference; Capabilities = capabilities;
        MetadataPolicyId = policy; MetadataPolicyVersion = policyVersion; MappingMetadataAllowed = allowed; RequestThinkFalse = thinkFalse;
    }
    public AiProfileKey Key { get; }
    public AiDialect Dialect { get; }
    public string Model { get; }
    public Uri Endpoint { get; }
    public bool LoopbackFixture { get; }
    public AiAuthentication Authentication { get; }
    public string? CredentialReference { get; }
    public AiCapability Capabilities { get; }
    public string MetadataPolicyId { get; }
    public int MetadataPolicyVersion { get; }
    public bool MappingMetadataAllowed { get; }
    public bool RequestThinkFalse { get; }
    public static AiProfileSpec Create(AiProfileKey key, AiDialect dialect, string model, Uri endpoint,
        bool loopbackFixture, AiAuthentication authentication, string? credentialReference,
        AiCapability capabilities, string metadataPolicyId, int metadataPolicyVersion,
        bool mappingMetadataAllowed, bool requestThinkFalse)
    {
        if (!key.Valid || !Enum.IsDefined(dialect) || !Enum.IsDefined(authentication)
            || (capabilities & AiCapability.Text) == 0 || (capabilities & ~(AiCapability.Text | AiCapability.FunctionTools)) != 0
            || metadataPolicyVersion <= 0 || (requestThinkFalse && dialect != AiDialect.OllamaChatNdjsonV1)) throw AiGuard.Invalid();
        AiGuard.Token(model, 256); AiGuard.Identifier(metadataPolicyId);
        if (authentication == AiAuthentication.None && credentialReference is not null) throw AiGuard.Invalid();
        if (authentication == AiAuthentication.Bearer) AiGuard.Token(credentialReference!, 128);
        ValidateEndpoint(endpoint, dialect, loopbackFixture);
        return new AiProfileSpec(key, dialect, model, new Uri(endpoint.AbsoluteUri, UriKind.Absolute), loopbackFixture, authentication, credentialReference,
            capabilities, metadataPolicyId, metadataPolicyVersion, mappingMetadataAllowed, requestThinkFalse);
    }
    private static void ValidateEndpoint(Uri endpoint, AiDialect dialect, bool fixture)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri) throw AiGuard.Invalid();
        AiGuard.Token(endpoint.OriginalString, 2048);
        string path = dialect == AiDialect.ChatCompletionsSseV1 ? "/v1/chat/completions" : "/api/chat";
        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.OriginalString.Contains('\\') || endpoint.AbsolutePath != path || endpoint.Port <= 0) throw AiGuard.Invalid();
        if (fixture)
        {
            string host = endpoint.Host.Trim('[', ']');
            if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(host, out IPAddress? address)
                || (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.IPv6Loopback))) throw AiGuard.Invalid();
        }
        else if (endpoint.Scheme != Uri.UriSchemeHttps) throw AiGuard.Invalid();
    }
    public override string ToString() => nameof(AiProfileSpec);
}

public sealed class AiProfileCatalog
{
    private readonly ImmutableDictionary<AiProfileKey, AiProfileSpec> _profiles;
    private AiProfileCatalog(ImmutableDictionary<AiProfileKey, AiProfileSpec> profiles) { _profiles = profiles; }
    public static AiProfileCatalog Create(IEnumerable<AiProfileSpec> profiles)
    {
        ImmutableArray<AiProfileSpec> owned = AiGuard.Copy(profiles, AiCeilings.Profiles);
        var builder = ImmutableDictionary.CreateBuilder<AiProfileKey, AiProfileSpec>();
        foreach (AiProfileSpec profile in owned)
        {
            if (builder.ContainsKey(profile.Key)) throw AiGuard.Invalid();
            builder.Add(profile.Key, profile);
        }
        return new AiProfileCatalog(builder.ToImmutable());
    }
    internal bool TryGet(AiProfileKey key, out AiProfileSpec? profile) => _profiles.TryGetValue(key, out profile);
    public override string ToString() => nameof(AiProfileCatalog);
}

public interface IAiCredentialResolver
{
    ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct);
}

public abstract class AiCredentialResult
{
    private protected AiCredentialResult() { }
    public sealed override string ToString() => nameof(AiCredentialResult);
}
public sealed class CredentialFound : AiCredentialResult
{
    private CredentialFound(AiBearerSecret secret) { Secret = secret; }
    public AiBearerSecret Secret { get; }
    public static CredentialFound Create(AiBearerSecret secret) => new CredentialFound(secret ?? throw AiGuard.Invalid());
}
public sealed class CredentialUnavailable : AiCredentialResult
{
    private CredentialUnavailable() { }
    public static CredentialUnavailable Instance { get; } = new CredentialUnavailable();
}
public sealed class AiBearerSecret : IDisposable
{
    private readonly object _gate = new object();
    private char[]? _characters;
    private AiBearerSecret(char[] characters) { _characters = characters; }
    public static AiBearerSecret CopyFrom(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 4096) throw AiGuard.Invalid();
        foreach (char c in value) if (c is < '!' or > '~') throw AiGuard.Invalid();
        return new AiBearerSecret(value.ToArray());
    }
    internal string HeaderValue()
    {
        lock (_gate)
        {
            if (_characters is null) throw new ObjectDisposedException(nameof(AiBearerSecret));
            return new string(_characters);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_characters is null) return;
            Array.Clear(_characters, 0, _characters.Length); _characters = null;
        }
    }
    public override string ToString() => nameof(AiBearerSecret);
}
