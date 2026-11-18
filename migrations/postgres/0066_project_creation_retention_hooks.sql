-- Preserve 0057's owner-only assertion and privilege profile. 0062 added a
-- second hook on project_creations: BEFORE INSERT OR DELETE, FOR EACH ROW.
DO $migration$
DECLARE
    original text;
    revised text;
    check_key text := $check$
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_trigger t
        WHERE t.tgrelid='braid.project_creations'::regclass
          AND t.tgname='request_key_guard' AND t.tgtype=15 AND t.tgenabled='O'
          AND t.tgfoid='braid.request_key_guard()'::regprocedure
          AND NOT t.tgisinternal AND t.tgconstraint=0 AND t.tgnargs=0 AND t.tgattr::text=''
          AND t.tgqual IS NULL AND NOT t.tgdeferrable AND NOT t.tginitdeferred
          AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation request-key trigger is inconsistent.';
    END IF;
$check$;
BEGIN
    SELECT prosrc INTO STRICT original FROM pg_catalog.pg_proc
        WHERE oid='braid.assert_project_creation_contract_v1()'::regprocedure;
    IF pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(original,'UTF8')),'hex')
        <> 'db5615357e699454b86957370e45dcf40267b770162b8931707dd72feee46f08' THEN
        RAISE EXCEPTION 'Unexpected project creation assertion predecessor.';
    END IF;
    revised := replace(original, 'WHEN ''project_creations'' THEN 1', 'WHEN ''project_creations'' THEN 2');
    revised := replace(revised, '    IF (SELECT count(*) FROM pg_catalog.pg_trigger t JOIN', check_key || '    IF (SELECT count(*) FROM pg_catalog.pg_trigger t JOIN');
    EXECUTE format('CREATE OR REPLACE FUNCTION braid.assert_project_creation_contract_v1() RETURNS void
        LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
        SET search_path=pg_catalog,braid,pg_temp AS %L', revised);
END;
$migration$;
