using System.Diagnostics.CodeAnalysis;
using Braid.Contracts.Help;

namespace Braid.Application;

/// <summary>The user guide shipped with this build. Its content never changes while the process runs.</summary>
public interface IUserGuideReader
{
    /// <summary>Every section in reading order.</summary>
    HelpSectionListV1 Contents { get; }

    bool TryGetSection(string sectionId, [NotNullWhen(true)] out HelpSectionV1? section);
}
