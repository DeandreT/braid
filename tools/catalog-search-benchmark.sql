-- Run only in a disposable PostgreSQL 16 database after Braid migrations.
-- Every synthetic relation is transaction-local and is rolled back.
BEGIN;
CREATE TEMP TABLE search_benchmark_mappings AS
SELECT n AS id, n % 250 AS project_id,
    CASE WHEN n % 997 = 0 THEN 'Hazmat orders ' ELSE 'Orders ' END || n AS name,
    'Inbound purchase orders from supplier ' || n % 1000 AS description, 'ferrule'::text AS backend_id
FROM generate_series(1,10000) n;
CREATE INDEX search_benchmark_mapping_trgm ON search_benchmark_mappings USING gin
    ((lower((name || E'\n' || description || E'\n' || backend_id) COLLATE "C")) braid_search_extensions.gin_trgm_ops);
CREATE TEMP TABLE search_benchmark_reports AS
SELECT n AS revision_id, n % 250 AS project_id,
    jsonb_agg(jsonb_build_object('kind',4,'id','field-' || f,
        'title', CASE WHEN f = 32 AND n = 5521 THEN 'SpecialTracking5521' ELSE 'TrackingNumber' || f END,
        'path','Orders:/shipment/items/' || f)) AS terms,
    string_agg(CASE WHEN f = 32 AND n = 5521 THEN 'SpecialTracking5521' ELSE 'TrackingNumber' || f END || E'\nOrders:/shipment/items/' || f, E'\n') AS search_text
FROM generate_series(1,10000) n CROSS JOIN generate_series(1,64) f GROUP BY n;
CREATE INDEX search_benchmark_reports_trgm ON search_benchmark_reports USING gin
    ((lower(search_text COLLATE "C")) braid_search_extensions.gin_trgm_ops);
ANALYZE search_benchmark_mappings;
ANALYZE search_benchmark_reports;

-- Indexed and sequential negative controls over the same 10k mappings / 640k field terms.
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT id FROM search_benchmark_mappings
WHERE lower((name || E'\n' || description || E'\n' || backend_id) COLLATE "C") LIKE '%hazmat%'
ORDER BY id LIMIT 26;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT revision_id,e->>'id' FROM search_benchmark_reports r CROSS JOIN LATERAL jsonb_array_elements(r.terms) e
WHERE lower(r.search_text COLLATE "C") LIKE '%specialtracking5521%'
    AND lower((e->>'title') COLLATE "C") LIKE '%specialtracking5521%'
ORDER BY revision_id,e->>'id' COLLATE "C" LIMIT 26;
SET LOCAL enable_bitmapscan = off;
SET LOCAL enable_indexscan = off;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT id FROM search_benchmark_mappings
WHERE lower((name || E'\n' || description || E'\n' || backend_id) COLLATE "C") LIKE '%hazmat%'
ORDER BY id LIMIT 26;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT revision_id,e->>'id' FROM search_benchmark_reports r CROSS JOIN LATERAL jsonb_array_elements(r.terms) e
WHERE lower(r.search_text COLLATE "C") LIKE '%specialtracking5521%'
    AND lower((e->>'title') COLLATE "C") LIKE '%specialtracking5521%'
ORDER BY revision_id,e->>'id' COLLATE "C" LIMIT 26;
ROLLBACK;
