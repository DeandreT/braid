using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat;

/// <summary>One administrator-configured model profile. Browsers select it by ID/version; they never supply endpoints.</summary>
public sealed class ChatModelProfile
{
    public ChatModelProfile(string displayName, string providerId, AiProfileSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (displayName is null || displayName.Length is < 1 or > 120 || displayName.Trim() != displayName ||
            displayName.Any(char.IsControl))
            throw new ArgumentException("A chat profile display name must be 1-120 trimmed characters.", nameof(displayName));
        DisplayName = displayName;
        ProviderId = ChatBounds.Identifier(providerId);
        Spec = spec;
        Pin = ChatAiBridge.PinProfile(providerId, spec);
    }

    public string DisplayName { get; }
    public string ProviderId { get; }
    public AiProfileSpec Spec { get; }
    public ChatProfilePin Pin { get; }
    /// <summary>Grounded chat sends mapping metadata; a profile whose policy refuses it is never offered.</summary>
    public bool Selectable => Spec.MappingMetadataAllowed;
    public override string ToString() => nameof(ChatModelProfile);
}

/// <summary>Explicit server chat configuration. The default instance is disabled and creates no provider client.</summary>
public sealed class ChatApiOptions
{
    public const int MaximumTextBytes = 32_768;
    public const int MaximumSelectors = 32;

    public ChatApiOptions()
    {
        Profiles = [];
    }

    public ChatApiOptions(IEnumerable<ChatModelProfile> profiles, TimeSpan retention, TimeSpan turnDuration, int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var owned = profiles.ToArray();
        if (owned.Length is < 1 or > 16 || owned.Any(profile => profile is null) ||
            owned.Select(profile => profile.Spec.Key).Distinct().Count() != owned.Length ||
            owned.Select(profile => profile.Spec.Key.Id).Distinct(StringComparer.Ordinal).Count() != owned.Length)
            throw new ArgumentException("Chat requires 1-16 profiles with distinct IDs.", nameof(profiles));
        if (retention < TimeSpan.FromHours(1) || retention > TimeSpan.FromDays(3650) || retention.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(retention), "Chat retention must be whole seconds from one hour to ten years.");
        if (turnDuration < TimeSpan.FromSeconds(10) || turnDuration > TimeSpan.FromSeconds(180) ||
            turnDuration.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(turnDuration), "Chat turns last whole seconds from 10 to 180.");
        if (maxOutputTokens is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "Chat output tokens are 1-2048.");
        Profiles = owned;
        Retention = retention;
        TurnDuration = turnDuration;
        MaxOutputTokens = maxOutputTokens;
    }

    public bool Enabled => Profiles.Count != 0;
    public IReadOnlyList<ChatModelProfile> Profiles { get; }
    public TimeSpan Retention { get; }
    public TimeSpan TurnDuration { get; }
    public int MaxOutputTokens { get; }

    internal ChatModelProfile? Find(string id, int version) =>
        Profiles.FirstOrDefault(profile => profile.Selectable && profile.Spec.Key.Version == version &&
            string.Equals(profile.Spec.Key.Id, id, StringComparison.Ordinal));

    public override string ToString() => nameof(ChatApiOptions);
}
