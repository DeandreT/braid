# Catalog search

The header search and `/catalog/search` use the same `ICatalogSearch` query service
as the HTTP API and other application callers. `weft search "order number"`
provides the authenticated command-line equivalent, including `--cursor`,
`--limit`, `--all`, and `--json`.

Search covers project names and descriptions, mapping names, descriptions and
backend IDs, revision upload filenames and SHA-256 digests, and field names,
rule summaries and field paths in retained projection reports. It does not search
sample inputs, outputs, literal dependencies, arbitrary report details or native
mapping bytes. A search never invokes a mapping engine or derives a report.

## Query and result contract

`GET /api/v1/organizations/{organizationId}/search?q=...&limit=25&cursor=...`
accepts a literal substring of 1–256 UTF-8 bytes, without control characters.
ASCII letters match case-insensitively; other Unicode is matched literally.
`%`, `_`, backslashes, punctuation and quotes have no query-language meaning.

Each statement freshly checks the active principal, organization admission and
explicit project membership. Organization membership alone does not expose
projects, and payload-read permission is neither required nor granted. Opening
a result runs the destination's own fresh authorization checks.

Pages contain at most 100 distinct resources ordered by kind and complete
resource ancestry, not a relevance score. Continuations bind the trimmed query
and organization, and never retain an authorization grant or database snapshot.
Concurrent catalog changes may affect later pages. A revoked project disappears
on the next request even when a continuation was issued before revocation.

Mapping results retain the head revision observed by the search statement.
Revision results retain their exact revision; field and rule results additionally
retain the projection derivation and resource ID. Browser links select that
retained report and open the field or rule in the workbench inspector, rather
than silently adopting the current report after a repair or profile change.
Project results open their project.

Titles and context snippets are plain text with bounded, nonoverlapping UTF-16
highlight offsets. Ranges never split Unicode scalars. Angular interpolates the
text and renders separate `<mark>` elements; it never interprets result text as
HTML. Long descriptions and paths use a bounded context around the first match.

## Indexing and existing reports

The database administrator first provisions `pg_trgm` in the private
`weft_search_extensions` schema. PostgreSQL makes trusted extension routines
bootstrap-owned; the ordinary migrator cannot remove their default public
execution privileges. The migration refuses an unprepared extension rather than
weakening any existing runtime role guard. Fresh local databases provision this
through `deploy/local/postgres-init.sh`; for an existing database, its administrator
runs the following in that database before applying the search migration:

```sql
CREATE SCHEMA weft_search_extensions AUTHORIZATION weft_migrator;
CREATE EXTENSION pg_trgm WITH SCHEMA weft_search_extensions;
REVOKE ALL ON SCHEMA weft_search_extensions FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA weft_search_extensions FROM PUBLIC;
```

Do not move or adopt an extension already installed in another schema without
reviewing its existing consumers. The ordinary table expression indexes track
project and mapping metadata, upload filenames and revision digests automatically.
Runtime roles receive no extension-schema or primary-table write privileges.

A metadata-only, organization-bound publication view includes **every retained
usable projection**, including publications predating the search migration and
reports superseded by a later profile or repair. Report publication remains the
single authoritative transaction: there is no secondary publication callback
that can fail and silently lose a report from search.

Search fills missing report indexes in bounded batches from the existing verified
retained-report reader. Each report is authorized again before its object is
read, and its complete scope, derivation and digest are checked before a restricted
database function admits the index. That function can write only derived search
metadata, not primary catalog facts. Empty reports receive a completed empty
index too. The indexed report text prefilters candidates before JSON resource
expansion, using the same literal matching rules as the final resource filter.

The final search statement checks completeness and permissions again. If an
authorized report remains unindexed, the API returns `503` with `Retry-After`,
not a successful partial page. Retrying advances another bounded batch. Priming
an existing catalog needs report reads, private report storage and the configured
derivation pool; once complete, ordinary searches need only the catalog reader.
Providing this cache-writer connection does not require enabling native report
derivation: search still calls only retained-report reads and index publication.
Missing, corrupt, unsupported or over-limit old reports remain explicit indexing
failures: restore the retained report or resolve its format/limit configuration.
Generating a different report does not establish the contents of a missing old
one.

## PostgreSQL strategy and measurement

`tools/catalog-search-benchmark.sql` creates transaction-local synthetic relations
in a disposable PostgreSQL 16 database and rolls them back. It exercises the
production expression/index shapes with 10,000 mappings and 10,000 retained
reports containing 64 fields each (640,000 field terms). It captures actual
execution plans for selective mapping and field-path/name searches and repeats
them with index scans disabled as a negative control.

Representative local PostgreSQL 16 measurements for these selective queries:

| Query | Trigram index | Sequential negative control |
| --- | ---: | ---: |
| Mapping substring | 0.92 ms | 5.71 ms |
| Retained-report field substring | 0.29 ms | 145.82 ms |

These are query-strategy measurements on self-authored synthetic data, not an
HTTP latency guarantee. Short/common substrings can be less selective, and the
planner may choose a scan. Page limits, statement deadlines, report byte/item
limits and the final completeness check remain in force independently of the
planner's choice.
