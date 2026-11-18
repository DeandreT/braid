-- Positive readiness for an immutable, administrator-enrolled target roster.
-- Only verifier hashes are retained; the release writer has no table access.
CREATE TABLE braid.release_replica_rosters (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, members text[] NOT NULL,
    PRIMARY KEY(organization_id,target_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK(cardinality(members) BETWEEN 1 AND 32)
);
CREATE TABLE braid.release_replica_receipts (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, member_hash text NOT NULL, qualification_id uuid NOT NULL,
    PRIMARY KEY(organization_id,qualification_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.release_replica_rosters(organization_id,target_id),
    FOREIGN KEY(organization_id,qualification_id) REFERENCES braid.build_qualifications(organization_id,qualification_id)
);
CREATE INDEX release_replica_target_idx ON braid.release_replica_receipts(organization_id,target_id,member_hash);

CREATE FUNCTION braid.enroll_release_replicas(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, roster text[]) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; ordered text[];
BEGIN
    IF session_user<>'braid_release_writer' OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR roster IS NULL OR cardinality(roster) NOT BETWEEN 1 AND 32 OR array_ndims(roster)<>1 THEN RETURN false; END IF;
    SELECT array_agg(DISTINCT m ORDER BY m) INTO ordered FROM unnest(roster) m WHERE m~'^[0-9a-f]{64}$';
    IF cardinality(ordered) IS DISTINCT FROM cardinality(roster) THEN RETURN false; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN RETURN false; END IF;
    PERFORM 1 FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target AND project_id=project
        AND mapping_id=mapping AND environment_id=environment AND environment_version=target_version AND enabled FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.release_replica_rosters WHERE organization_id=org AND target_id=target) THEN
        RETURN EXISTS(SELECT 1 FROM ONLY braid.release_replica_rosters WHERE organization_id=org AND target_id=target AND members=ordered);
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.local_releases WHERE organization_id=org AND target_id=target) THEN RETURN false; END IF;
    INSERT INTO braid.release_replica_rosters VALUES(org,target,ordered);
    RETURN true;
END;
$body$;
REVOKE ALL ON FUNCTION braid.enroll_release_replicas(uuid,uuid,uuid,uuid,bigint,uuid,text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.enroll_release_replicas(uuid,uuid,uuid,uuid,bigint,uuid,text[]) TO braid_release_writer;

ALTER FUNCTION braid.local_release_admitted(uuid,uuid,uuid,uuid) RENAME TO qualified_release_admitted;
CREATE FUNCTION braid.record_release_replica(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, proof uuid, credential bytea) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; member text:=encode(sha256(credential),'hex');
BEGIN
    IF session_user<>'braid_release_writer' OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR octet_length(credential) IS DISTINCT FROM 32 THEN RETURN false; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN false; END IF;
    PERFORM 1 FROM ONLY braid.local_release_targets t JOIN ONLY braid.release_replica_rosters r USING(organization_id,target_id)
        WHERE t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping AND t.environment_id=environment
        AND t.environment_version=target_version AND t.target_id=target AND member=ANY(r.members) FOR UPDATE OF t;
    IF NOT FOUND THEN RETURN false; END IF;
    IF proof IS NULL THEN
        DELETE FROM braid.release_replica_receipts s USING ONLY braid.build_qualifications q WHERE s.organization_id=org
            AND s.target_id=target AND s.member_hash=member AND q.organization_id=org AND q.qualification_id=s.qualification_id AND q.actor_id=actor;
        RETURN true;
    END IF;
    IF braid.qualified_release_admitted(org,target,proof,actor) IS NOT TRUE OR NOT EXISTS(
        SELECT 1 FROM ONLY braid.build_qualifications WHERE organization_id=org AND qualification_id=proof AND created_at>clock_timestamp()-interval '10 minutes') THEN RETURN false; END IF;
    INSERT INTO braid.release_replica_receipts VALUES(org,target,member,proof) ON CONFLICT DO NOTHING;
    RETURN EXISTS(SELECT 1 FROM ONLY braid.release_replica_receipts WHERE organization_id=org AND target_id=target AND member_hash=member AND qualification_id=proof);
END;
$body$;
REVOKE ALL ON FUNCTION braid.record_release_replica(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.record_release_replica(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bytea) TO braid_release_writer;

CREATE FUNCTION braid.local_release_admitted(org uuid, target_key uuid, qkey uuid, actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT braid.qualified_release_admitted(org,target_key,qkey,actor) AND NOT EXISTS(
        SELECT 1 FROM ONLY braid.release_replica_rosters r CROSS JOIN LATERAL unnest(r.members) m
        WHERE r.organization_id=org AND r.target_id=target_key AND NOT EXISTS(
            SELECT 1 FROM ONLY braid.release_replica_receipts s
            JOIN ONLY braid.build_qualifications p ON p.organization_id=s.organization_id AND p.qualification_id=s.qualification_id
            JOIN ONLY braid.build_qualifications q ON q.organization_id=p.organization_id AND q.build_id=p.build_id AND q.actor_id=p.actor_id
            WHERE s.organization_id=org AND s.target_id=target_key AND s.member_hash=m AND q.qualification_id=qkey AND q.actor_id=actor
                AND convert_from(p.proof_body,'UTF8')::jsonb->>'runtimeImage'=convert_from(q.proof_body,'UTF8')::jsonb->>'runtimeImage'
                AND p.created_at>clock_timestamp()-interval '10 minutes'))
$body$;
REVOKE ALL ON FUNCTION braid.local_release_admitted(uuid,uuid,uuid,uuid) FROM PUBLIC;
