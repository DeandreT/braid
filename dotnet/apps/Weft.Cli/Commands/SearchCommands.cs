using System.CommandLine;
using System.Text;
using Weft.Cli.Http;
using Weft.Contracts.Catalog;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class SearchCommands
{
    public static Command Search(CliGlobals globals)
    {
        var query = Argument("query", "Literal search text (1-256 UTF-8 bytes); quote text containing spaces");
        var paging = new Paging();
        var command = new Command("search", "Search the catalog resources you can read").With([query, .. paging.Symbols]);
        command.Handle(globals, async invocation =>
        {
            var text = (invocation.Value(query) ?? "").Trim();
            try
            {
                if (text.Length == 0 || new UTF8Encoding(false, true).GetByteCount(text) > 256 || text.Any(char.IsControl))
                    throw CliException.Usage("Search text must contain 1-256 UTF-8 bytes without controls.");
            }
            catch (EncoderFallbackException) { throw CliException.Usage("Search text must be valid Unicode."); }
            var pages = await paging.ReadAsync(invocation, async cursor =>
            {
                var request = (await invocation.RequestAsync(ApiOperations.SearchCatalog))
                    .Query("q", text).Query("limit", paging.Limit(invocation)).Query("cursor", cursor);
                var page = await invocation.Api.ReadAsync<CatalogSearchPageV1>(request, invocation.Cancellation);
                return (page, page.NextCursor);
            });
            var hits = pages.SelectMany(page => page.Hits).ToArray();
            invocation.Output.Write(paging.Document(invocation, pages, () => new { query = text, hits, nextCursor = (string?)null }), output =>
            {
                output.Table(["KIND", "TITLE", "PROJECT", "MAPPING", "REVISION", "REPORT", "RESOURCE"], hits.Select(hit =>
                    (IReadOnlyList<object?>)[hit.Kind, hit.Title.Text, hit.ProjectId, hit.MappingId, hit.RevisionId, hit.ProjectionDerivationId, hit.ResourceId]));
                paging.Footer(output, pages[^1].NextCursor);
            });
        });
        return command;
    }
}
