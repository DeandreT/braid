using System.Text;
using Braid.Application;

namespace Braid.Tests;

internal static class CsvPreviewTestData
{
    internal static readonly CsvDocumentDialect Dialect = new(CsvDocumentPolicy.DialectSchema, "\t", "\"", false, false, false, true);
    internal static byte[] Native => Encoding.UTF8.GetBytes("""
        {"source":{"name":"Row","kind":{"kind":"group","children":[{"name":"value","kind":{"kind":"scalar","ty":"string"}}]}},
         "target":{"name":"Row","kind":{"kind":"group","children":[{"name":"value","kind":{"kind":"scalar","ty":"string"}}]}},
         "source_path":"source.json","target_path":"target.xml",
         "source_options":{"tabular_kind":"csv","delimiter":"\t","has_header_row":false,"csv_preserve_empty_strings":true},
         "target_options":{"tabular_kind":"csv","delimiter":"\t","has_header_row":false,"csv_preserve_empty_strings":true},
         "graph":{"nodes":{"1":{"kind":"source_field","path":["value"]}}},
         "root":{"target_field":"","bindings":[{"target_field":"value","node":1}],"children":[]}}
        """);

    internal sealed class Backend(CsvDocumentDialect? outputDialect = null) : IMappingPreviewBackend
    {
        internal int Calls { get; private set; }
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var dialect = outputDialect ?? Dialect;
            return Task.FromResult(new MappingPreviewResult(MappingPreviewStatus.Succeeded,
                new(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "engine", "image", "adapter", true),
                CsvDocumentPolicy.Profile, 0, [new("primary-output", "Row", false, CsvDialectJson.MediaType(dialect), 0, [], dialect)], null));
        }
    }
}
