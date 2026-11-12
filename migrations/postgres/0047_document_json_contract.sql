-- Restore the recursive duplicate-key contract referenced by XML migration 0042.
-- The helper's definition is retained from the original document-format history.
CREATE FUNCTION weft.document_json_unique(value json) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE item json;
BEGIN
    IF json_typeof(value)='object' THEN
        IF (SELECT count(*)<>count(DISTINCT key) FROM json_each(value)) THEN RETURN false; END IF;
        FOR item IN SELECT e.value FROM json_each(value) e LOOP
            IF NOT weft.document_json_unique(item) THEN RETURN false; END IF;
        END LOOP;
    ELSIF json_typeof(value)='array' THEN
        FOR item IN SELECT e.value FROM json_array_elements(value) e LOOP
            IF NOT weft.document_json_unique(item) THEN RETURN false; END IF;
        END LOOP;
    END IF;
    RETURN true;
END;
$body$;
REVOKE ALL ON FUNCTION weft.document_json_unique(json) FROM PUBLIC;
