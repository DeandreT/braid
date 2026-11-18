-- One immutable release pin per accepted order. Inputs, idempotency, grants,
-- claim leases, attempt history and retained output remain in the machine ledger.
CREATE TABLE braid.host_order_pins (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, provider text NOT NULL,
    tenant_id uuid NOT NULL, object_id uuid NOT NULL, request_id uuid NOT NULL,
    release_id uuid NOT NULL, publisher_id uuid NOT NULL,
    PRIMARY KEY (organization_id,target_id,provider,tenant_id,object_id,request_id)
);
-- Fixed, bounded lookup regardless of the number of completed orders.
CREATE TABLE braid.host_target_slots (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, slot integer NOT NULL CHECK (slot BETWEEN 1 AND 2),
    provider text NOT NULL, tenant_id uuid NOT NULL, object_id uuid NOT NULL, request_id uuid NOT NULL,
    claim_id uuid NOT NULL, PRIMARY KEY (organization_id,target_id,slot)
);
REVOKE ALL ON braid.host_order_pins,braid.host_target_slots FROM PUBLIC;
ALTER TABLE braid.host_order_pins ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.host_target_slots ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.host_order_pins USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.host_target_slots USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);

CREATE FUNCTION braid.guard_host_order() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_invocation_writer' OR current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR (TG_TABLE_NAME='host_order_pins' AND TG_OP<>'INSERT') THEN
        RAISE EXCEPTION 'Host order mutation refused.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_host_order() FROM PUBLIC;
CREATE TRIGGER host_pin_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.host_order_pins FOR EACH ROW EXECUTE FUNCTION braid.guard_host_order();
CREATE TRIGGER host_pin_no_truncate BEFORE TRUNCATE ON braid.host_order_pins FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_host_order();
CREATE TRIGGER host_slot_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.host_target_slots FOR EACH ROW EXECUTE FUNCTION braid.guard_host_order();
CREATE TRIGGER host_slot_no_truncate BEFORE TRUNCATE ON braid.host_target_slots FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_host_order();

ALTER FUNCTION braid.claim_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text) RENAME TO claim_machine_attempt;
REVOKE ALL ON FUNCTION braid.claim_machine_attempt(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text) FROM braid_invocation_writer;
CREATE FUNCTION braid.claim_machine_invocation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, issuer text, tenant uuid, principal uuid, application uuid, request uuid, digest text) RETURNS TABLE(status integer, attempt_number integer, claim_key uuid, publisher uuid, final_state text, final_outcome text, final_release uuid, retained bytea)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a record; p braid.host_order_pins%ROWTYPE; seat integer;
BEGIN
    SELECT * INTO STRICT a FROM braid.claim_machine_attempt(org,project,mapping,environment,target_version,target,issuer,tenant,principal,application,request,digest);
    IF a.status<>0 THEN
        RETURN QUERY SELECT a.status,a.attempt_number,a.claim_key,a.publisher,a.final_state,a.final_outcome,a.final_release,a.retained;
        RETURN;
    END IF;
    -- Only admitted new attempts compete for slots. Replay, conflict and
    -- in-progress lookups need no slot. A rejected admission rolls back its
    -- tentative claim and audit entry rather than consuming an attempt.
    IF NOT pg_try_advisory_xact_lock(hashtextextended('braid.host.target:'||org::text||':'||target::text,0)) THEN
        RAISE EXCEPTION USING ERRCODE='BH001', MESSAGE='target busy';
    END IF;
    SELECT s INTO seat FROM generate_series(1,2) s WHERE NOT EXISTS (
        SELECT 1 FROM ONLY braid.host_target_slots x JOIN ONLY braid.machine_invocations i
          USING (organization_id,target_id,provider,tenant_id,object_id,request_id,claim_id)
        WHERE x.organization_id=org AND x.target_id=target AND x.slot=s AND i.state='running' AND i.lease_until>clock_timestamp())
        ORDER BY s LIMIT 1;
    IF seat IS NULL THEN RAISE EXCEPTION USING ERRCODE='BH001', MESSAGE='target busy'; END IF;
    IF a.attempt_number=1 THEN
        INSERT INTO braid.host_order_pins SELECT org,target,issuer,tenant,principal,request,t.active_release_id,a.publisher
            FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target RETURNING * INTO STRICT p;
    ELSE
        SELECT * INTO p FROM ONLY braid.host_order_pins x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
            AND x.tenant_id=tenant AND x.object_id=principal AND x.request_id=request;
        -- A pre-upgrade unfinished order has no provable pin. Hold it rather
        -- than choosing today's release and claiming it was the original.
        IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='BH002', MESSAGE='unproven order release'; END IF;
    END IF;
    INSERT INTO braid.host_target_slots VALUES(org,target,seat,issuer,tenant,principal,request,a.claim_key)
        ON CONFLICT (organization_id,target_id,slot) DO UPDATE SET provider=excluded.provider,tenant_id=excluded.tenant_id,
            object_id=excluded.object_id,request_id=excluded.request_id,claim_id=excluded.claim_id;
    RETURN QUERY SELECT a.status,a.attempt_number,a.claim_key,p.publisher_id,a.final_state,a.final_outcome,p.release_id,a.retained;
EXCEPTION WHEN SQLSTATE 'BH001' THEN
    RETURN QUERY SELECT 6,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea;
WHEN SQLSTATE 'BH002' THEN
    RETURN QUERY SELECT 4,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.claim_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text) TO braid_invocation_writer;

ALTER FUNCTION braid.finish_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea) RENAME TO finish_machine_attempt;
REVOKE ALL ON FUNCTION braid.finish_machine_attempt(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea) FROM braid_invocation_writer;
CREATE FUNCTION braid.finish_machine_invocation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, issuer text, tenant uuid, principal uuid, request uuid, claim uuid, application uuid, finding text, release uuid, qualification uuid, build uuid, intent_sha text, artifact_sha text, revision uuid, image text, clock text, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF release IS NOT NULL AND session_user='braid_invocation_writer' AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND EXISTS (SELECT 1 FROM ONLY braid.host_order_pins x JOIN ONLY braid.machine_invocations i
            USING (organization_id,target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer AND x.tenant_id=tenant AND x.object_id=principal
            AND x.request_id=request AND i.project_id=project AND i.mapping_id=mapping AND i.application_id=application AND i.claim_id=claim
            AND i.state='running' AND i.lease_until>clock_timestamp() AND x.release_id<>release) THEN RETURN 3; END IF;
    RETURN braid.finish_machine_attempt(org,project,mapping,environment,target_version,target,issuer,tenant,principal,request,claim,application,
        finding,release,qualification,build,intent_sha,artifact_sha,revision,image,clock,body);
END;
$body$;
REVOKE ALL ON FUNCTION braid.finish_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.finish_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea) TO braid_invocation_writer;
