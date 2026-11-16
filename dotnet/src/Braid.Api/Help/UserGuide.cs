using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Braid.Application;
using Braid.Contracts.Help;

namespace Braid.Api.Help;

/// <summary>
/// The user guide embedded in this assembly from <c>docs/user-guide</c> at build time. It is parsed and
/// cross-checked once; nothing is read from disk or the network at run time.
/// </summary>
public sealed partial class UserGuide : IUserGuideReader
{
    public const int MaximumSectionIdCharacters = 64;
    internal const string ResourcePrefix = "Braid.Api.UserGuide.";
    internal const int MaximumSectionBytes = 64 * 1024;
    internal const int MaximumSections = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Lazy<UserGuide> EmbeddedGuide = new(() => Load(typeof(UserGuide).Assembly));
    private readonly Dictionary<string, HelpSectionV1> _sections;

    private UserGuide(IReadOnlyList<HelpSectionV1> sections)
    {
        _sections = sections.ToDictionary(section => section.Id, StringComparer.Ordinal);
        Contents = new HelpSectionListV1(sections.Select(section =>
            new HelpSectionSummaryV1(section.Id, section.Title, section.Summary)).ToArray().AsReadOnly());
    }

    /// <summary>The guide shipped with this build. Throws if an embedded file breaks a guide rule.</summary>
    public static UserGuide Embedded => EmbeddedGuide.Value;

    public HelpSectionListV1 Contents { get; }

    public bool TryGetSection(string sectionId, [NotNullWhen(true)] out HelpSectionV1? section)
    {
        ArgumentNullException.ThrowIfNull(sectionId);
        return _sections.TryGetValue(sectionId, out section);
    }

    /// <summary>Lowercase ASCII words joined by single hyphens, at most 64 characters.</summary>
    public static bool IsSectionId(string? value) =>
        value is { Length: > 0 and <= MaximumSectionIdCharacters } && SectionIdPattern().IsMatch(value);

    internal static UserGuide Load(Assembly assembly)
    {
        var files = new List<(string FileName, string Text)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new UserGuideFormatException($"The embedded guide file '{name}' cannot be read.");
            if (stream.Length > MaximumSectionBytes)
                throw new UserGuideFormatException($"{name[ResourcePrefix.Length..]}: A guide file exceeds {MaximumSectionBytes} bytes.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                throw new UserGuideFormatException($"{name[ResourcePrefix.Length..]}: A guide file must be UTF-8.");
            }
            files.Add((name[ResourcePrefix.Length..], text));
        }
        return Parse(files);
    }

    /// <summary>Parses every file, then requires unique orders and links that resolve to sections and headings.</summary>
    internal static UserGuide Parse(IReadOnlyCollection<(string FileName, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is 0 or > MaximumSections)
            throw new UserGuideFormatException($"The guide needs from 1 to {MaximumSections} sections.");
        var parsed = files.Select(file => UserGuideMarkdown.Parse(file.FileName, file.Text)).ToArray();
        var byId = parsed.ToDictionary(section => section.Section.Id, StringComparer.Ordinal);
        if (parsed.DistinctBy(section => section.Order).Count() != parsed.Length)
            throw new UserGuideFormatException("Every guide section needs its own order.");
        foreach (var section in parsed)
        {
            foreach (var link in section.Links)
            {
                if (!byId.TryGetValue(link.SectionId, out var target))
                    throw new UserGuideFormatException($"{section.Section.Id}.md:{link.Line}: The linked section '{link.SectionId}' does not exist.");
                if (link.Anchor is { } anchor && !target.Anchors.Contains(anchor))
                    throw new UserGuideFormatException($"{section.Section.Id}.md:{link.Line}: The section '{link.SectionId}' has no heading '#{anchor}'.");
            }
        }
        return new(parsed.OrderBy(section => section.Order).Select(section => section.Section).ToArray());
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SectionIdPattern();
}
