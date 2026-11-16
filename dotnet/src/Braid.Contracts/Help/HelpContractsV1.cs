using System.Text.Json.Serialization;

namespace Braid.Contracts.Help;

/// <summary>One section of the user guide embedded in this server build.</summary>
public sealed record HelpSectionSummaryV1(string Id, string Title, string Summary);

/// <summary>Every guide section in reading order.</summary>
public sealed record HelpSectionListV1(IReadOnlyList<HelpSectionSummaryV1> Sections);

/// <summary>
/// One guide section as a closed structure of text blocks. It carries no markup, so clients render
/// it as plain text and links without interpreting HTML.
/// </summary>
public sealed record HelpSectionV1(string Id, string Title, string Summary, IReadOnlyList<HelpBlockV1> Blocks);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HelpHeadingV1), "heading")]
[JsonDerivedType(typeof(HelpParagraphV1), "paragraph")]
[JsonDerivedType(typeof(HelpListV1), "list")]
[JsonDerivedType(typeof(HelpTableV1), "table")]
[JsonDerivedType(typeof(HelpCodeBlockV1), "codeBlock")]
public abstract record HelpBlockV1;

/// <summary>A level 2 or 3 heading. Anchor is unique within its section.</summary>
public sealed record HelpHeadingV1(int Level, string Anchor, string Text) : HelpBlockV1;

public sealed record HelpParagraphV1(IReadOnlyList<HelpInlineV1> Content) : HelpBlockV1;

public sealed record HelpListV1(bool Ordered, IReadOnlyList<HelpListItemV1> Items) : HelpBlockV1;

public sealed record HelpListItemV1(IReadOnlyList<HelpInlineV1> Content);

/// <summary>Every row has as many cells as the header.</summary>
public sealed record HelpTableV1(IReadOnlyList<HelpTableCellV1> Header, IReadOnlyList<HelpTableRowV1> Rows) : HelpBlockV1;

public sealed record HelpTableRowV1(IReadOnlyList<HelpTableCellV1> Cells);

public sealed record HelpTableCellV1(IReadOnlyList<HelpInlineV1> Content);

public sealed record HelpCodeBlockV1(string Text) : HelpBlockV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HelpTextV1), "text")]
[JsonDerivedType(typeof(HelpStrongV1), "strong")]
[JsonDerivedType(typeof(HelpEmphasisV1), "emphasis")]
[JsonDerivedType(typeof(HelpCodeV1), "code")]
[JsonDerivedType(typeof(HelpLinkV1), "link")]
public abstract record HelpInlineV1;

public sealed record HelpTextV1(string Text) : HelpInlineV1;

public sealed record HelpStrongV1(string Text) : HelpInlineV1;

public sealed record HelpEmphasisV1(string Text) : HelpInlineV1;

public sealed record HelpCodeV1(string Text) : HelpInlineV1;

/// <summary>A link to a guide section of this build, optionally to one of its heading anchors.</summary>
public sealed record HelpLinkV1(string Text, string SectionId, string? Anchor) : HelpInlineV1;
