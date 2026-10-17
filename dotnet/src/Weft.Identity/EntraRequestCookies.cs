using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Weft.Identity;

// Preserve the original request cookies, but give the standard nonce scanner only
// the exact cookie authenticated by this flow's protected state. Other flows must
// neither poison this operation's failure latch nor be decrypted or deleted by it.
internal sealed class EntraRequestCookies(EntraBrowserClient.Operation operation,
    IRequestCookieCollection original) : IRequestCookiesFeature, IRequestCookieCollection
{
    public IRequestCookieCollection Cookies
    {
        get => this;
        set => throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    public int Count => this.Count(_ => true);
    public ICollection<string> Keys => this.Select(pair => pair.Key).ToArray();
    public string? this[string key] => TryGetValue(key, out var value) ? value : null;
    public bool ContainsKey(string key) => Included(key) && original.ContainsKey(key);

    public bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
    {
        value = null;
        return Included(key) && original.TryGetValue(key, out value);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
        original.Where(pair => Included(pair.Key)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool Included(string key) =>
        !key.StartsWith(EntraBrowserClient.NoncePrefix, StringComparison.Ordinal) ||
        operation.NonceName is { } selected && string.Equals(key, selected, StringComparison.Ordinal);
}
