using System.CommandLine;
using System.Globalization;
using Weft.Cli.Output;

namespace Weft.Cli.Commands;

/// <summary><c>--limit</c>, <c>--cursor</c> and <c>--all</c> for cursor-paged catalog lists.</summary>
internal sealed class Paging
{
    public Option<int?> LimitOption { get; } = new("--limit") { Description = "Items per page, 1-100 (default: the server's page size)" };
    public Option<string?> Cursor { get; } = new("--cursor") { Description = "Continue from the next cursor of an earlier page" };
    public Option<bool> All { get; } = new("--all") { Description = "Read every page" };
    public Symbol[] Symbols => [LimitOption, Cursor, All];

    public string? Limit(CliInvocation invocation) => invocation.Value(LimitOption) switch
    {
        null => null,
        >= 1 and <= 100 and var limit => limit.ToString(CultureInfo.InvariantCulture),
        _ => throw CliException.Usage("--limit must be between 1 and 100.")
    };

    public async Task<List<TPage>> ReadAsync<TPage>(CliInvocation invocation, Func<string?, Task<(TPage Page, string? Next)>> read)
    {
        var pages = new List<TPage>();
        var cursor = invocation.Value(Cursor);
        var traversal = new PageTraversal(cursor);
        do
        {
            var (page, next) = await read(cursor);
            pages.Add(page);
            cursor = next;
        }
        while (traversal.Continue(invocation.Value(All), cursor));
        return pages;
    }

    /// <summary>The page's exact document, or for <c>--all</c> the same document shape with every item and no next cursor.</summary>
    public object Document<TPage>(CliInvocation invocation, List<TPage> pages, Func<object> all) =>
        invocation.Value(All) ? all() : pages[0]!;

    public void Footer(CliOutput output, string? next)
    {
        if (next is not null)
            output.Line($"More results: add --cursor {next} (or --all).");
    }
}

/// <summary>Bound a full traversal without ever reporting a truncated or cyclic result as complete.</summary>
internal sealed class PageTraversal
{
    internal const int MaximumPages = 1000;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private int pages;

    public PageTraversal(string? initialCursor = null)
    {
        if (initialCursor is not null)
            seen.Add(initialCursor);
    }

    public bool Continue(bool all, string? next)
    {
        pages++;
        if (!all || next is null)
            return false;
        if (!seen.Add(next))
            throw new CliException(CliExitCode.Transport, "The server repeated a pagination cursor; the list is incomplete.", "invalidPagination");
        if (pages >= MaximumPages)
            throw new CliException(CliExitCode.Transport, "The list exceeded the pagination safety limit; use individual pages instead of --all.", "invalidPagination");
        return true;
    }
}
