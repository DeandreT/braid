-- Retained accounting contains identifiers and counters only. It remains separate
-- from erasable conversation content so erasure cannot reset admission usage.
LOCK TABLE braid.chat_conversations,braid.chat_turns,braid.chat_rounds,braid.chat_events IN SHARE ROW EXCLUSIVE MODE;

CREATE TABLE braid.ai_usage_turns (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    principal_id uuid NOT NULL, turn_id uuid NOT NULL, project_id uuid,
    profile_id text COLLATE "C" NOT NULL CHECK(profile_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
    profile_version integer NOT NULL CHECK(profile_version>0),
    admitted_at timestamp with time zone NOT NULL CHECK(isfinite(admitted_at)),
    phase integer NOT NULL CHECK(phase BETWEEN 1 AND 5),
    finished_at timestamp with time zone,
    rounds integer NOT NULL DEFAULT 0 CHECK(rounds BETWEEN 0 AND 5),
    completed_rounds integer NOT NULL DEFAULT 0 CHECK(completed_rounds BETWEEN 0 AND rounds),
    failed_rounds integer NOT NULL DEFAULT 0 CHECK(failed_rounds BETWEEN 0 AND completed_rounds),
    tool_calls integer NOT NULL DEFAULT 0 CHECK(tool_calls BETWEEN 0 AND 4096),
    known_input_rounds integer NOT NULL DEFAULT 0 CHECK(known_input_rounds BETWEEN 0 AND completed_rounds),
    known_output_rounds integer NOT NULL DEFAULT 0 CHECK(known_output_rounds BETWEEN 0 AND completed_rounds),
    known_total_rounds integer NOT NULL DEFAULT 0 CHECK(known_total_rounds BETWEEN 0 AND completed_rounds),
    known_input_tokens bigint NOT NULL DEFAULT 0 CHECK(known_input_tokens BETWEEN 0 AND 5000000000000),
    known_output_tokens bigint NOT NULL DEFAULT 0 CHECK(known_output_tokens BETWEEN 0 AND 5000000000000),
    known_total_tokens bigint NOT NULL DEFAULT 0 CHECK(known_total_tokens BETWEEN 0 AND 5000000000000),
    PRIMARY KEY(organization_id,principal_id,turn_id),
    CHECK(principal_id<>'00000000-0000-0000-0000-000000000000' AND turn_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK((phase=1)=(finished_at IS NULL)),
    CHECK(finished_at IS NULL OR isfinite(finished_at) AND finished_at>=admitted_at)
);
CREATE INDEX ai_usage_window ON braid.ai_usage_turns(organization_id,admitted_at,principal_id,turn_id);
CREATE INDEX ai_usage_project_window ON braid.ai_usage_turns(organization_id,project_id,admitted_at);
CREATE INDEX ai_usage_principal_window ON braid.ai_usage_turns(organization_id,principal_id,admitted_at);

CREATE TABLE braid.ai_usage_failures (
    organization_id uuid NOT NULL, principal_id uuid NOT NULL, turn_id uuid NOT NULL,
    code integer NOT NULL CHECK(code BETWEEN 1 AND 20),
    occurrences integer NOT NULL CHECK(occurrences BETWEEN 1 AND 5),
    PRIMARY KEY(organization_id,principal_id,turn_id,code),
    FOREIGN KEY(organization_id,principal_id,turn_id) REFERENCES braid.ai_usage_turns(organization_id,principal_id,turn_id)
);
CREATE TABLE braid.ai_usage_coverage (
    singleton boolean PRIMARY KEY CHECK(singleton),
    complete_since timestamp with time zone NOT NULL CHECK(isfinite(complete_since))
);
ALTER TABLE braid.ai_usage_turns ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.ai_usage_failures ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.ai_usage_turns USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.ai_usage_failures USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.ai_usage_turns,braid.ai_usage_failures,braid.ai_usage_coverage FROM PUBLIC;

CREATE FUNCTION braid.ai_usage_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP NOT IN ('INSERT','UPDATE') THEN
        RAISE EXCEPTION 'Usage accounting is owner-maintained.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='ai_usage_coverage' AND TG_OP<>'INSERT' THEN
        RAISE EXCEPTION 'Usage coverage is immutable.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='UPDATE' THEN
        IF TG_TABLE_NAME='ai_usage_turns' THEN
            IF ROW(NEW.organization_id,NEW.principal_id,NEW.turn_id,NEW.project_id,
                NEW.profile_id,NEW.profile_version,NEW.admitted_at) IS DISTINCT FROM ROW(OLD.organization_id,OLD.principal_id,
                OLD.turn_id,OLD.project_id,OLD.profile_id,OLD.profile_version,OLD.admitted_at) THEN
                RAISE EXCEPTION 'Usage identity is immutable.' USING ERRCODE='23514';
            END IF;
        ELSIF TG_TABLE_NAME='ai_usage_failures' THEN
            IF ROW(NEW.organization_id,NEW.principal_id,NEW.turn_id,NEW.code)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.principal_id,OLD.turn_id,OLD.code) THEN
                RAISE EXCEPTION 'Usage failure identity is immutable.' USING ERRCODE='23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER ai_usage_turn_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_usage_turns FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_guard();
CREATE TRIGGER ai_usage_turn_no_truncate BEFORE TRUNCATE ON braid.ai_usage_turns FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_usage_guard();
CREATE TRIGGER ai_usage_failure_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_usage_failures FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_guard();
CREATE TRIGGER ai_usage_failure_no_truncate BEFORE TRUNCATE ON braid.ai_usage_failures FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_usage_guard();
CREATE TRIGGER ai_usage_coverage_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_usage_coverage FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_guard();
CREATE TRIGGER ai_usage_coverage_no_truncate BEFORE TRUNCATE ON braid.ai_usage_coverage FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_usage_guard();

CREATE FUNCTION braid.ai_usage_observe(p_org uuid,p_principal uuid,p_turn uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE conversation uuid; terminal jsonb;
BEGIN
    -- Select only accounting metadata, never prompts, envelopes, tool arguments,
    -- provider responses, citations, payloads or their digests.
    INSERT INTO braid.ai_usage_turns(organization_id,principal_id,turn_id,project_id,profile_id,profile_version,admitted_at,phase,finished_at)
        SELECT t.organization_id,t.creator_id,t.turn_id,
            CASE WHEN c.conversation_kind=1 THEN c.project_id ELSE (t.original_envelope::jsonb->'focus'->>'project')::uuid END,
            t.profile_id,t.profile_version,t.created_at,t.phase,t.finished_at
        FROM ONLY braid.chat_turns t JOIN ONLY braid.chat_conversations c
            ON c.organization_id=t.organization_id AND c.creator_id=t.creator_id AND c.conversation_id=t.conversation_id
        WHERE t.organization_id=p_org AND t.creator_id=p_principal AND t.turn_id=p_turn
        ON CONFLICT(organization_id,principal_id,turn_id) DO NOTHING;
    PERFORM 1 FROM ONLY braid.ai_usage_turns WHERE organization_id=p_org AND principal_id=p_principal AND turn_id=p_turn FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Usage source is unavailable.' USING ERRCODE='23514'; END IF;
    SELECT conversation_id INTO STRICT conversation FROM ONLY braid.chat_turns
        WHERE organization_id=p_org AND creator_id=p_principal AND turn_id=p_turn;
    -- A final turn event can acknowledge provider terminal metadata even when
    -- the round completion was interrupted. Count that round once, not twice.
    SELECT e.metadata_json::jsonb INTO terminal FROM ONLY braid.chat_events e
        WHERE e.organization_id=p_org AND e.creator_id=p_principal AND e.conversation_id=conversation
            AND e.turn_id=p_turn AND e.kind IN (5,6,7,8) ORDER BY e.sequence DESC LIMIT 1;
    WITH observed AS (
        SELECT CASE WHEN r.outcome<>1 THEN r.outcome
                WHEN terminal->'failure'->>'round'=r.round_id::text THEN 4
                WHEN terminal->'usage'->>'round'=r.round_id::text AND terminal->'usage'->>'origin' IN ('2','3') THEN 3 ELSE 1 END AS outcome,
            CASE WHEN r.outcome<>1 THEN r.result_metadata::jsonb->'usage'
                WHEN terminal->'usage'->>'round'=r.round_id::text AND terminal->'usage'->>'origin' IN ('2','3') THEN terminal->'usage' END AS usage
        FROM ONLY braid.chat_rounds r WHERE r.organization_id=p_org AND r.creator_id=p_principal
            AND r.conversation_id=conversation AND r.turn_id=p_turn
    ), totals AS (
        SELECT count(*)::integer AS rounds,count(*) FILTER(WHERE outcome<>1)::integer AS completed,
            count(*) FILTER(WHERE outcome=4)::integer AS failed,
            count(*) FILTER(WHERE outcome<>1 AND usage->>'input' IS NOT NULL)::integer AS inputs,
            count(*) FILTER(WHERE outcome<>1 AND usage->>'output' IS NOT NULL)::integer AS outputs,
            count(*) FILTER(WHERE outcome<>1 AND usage->>'total' IS NOT NULL)::integer AS totals,
            coalesce(sum((usage->>'input')::bigint) FILTER(WHERE outcome<>1),0)::bigint AS input_tokens,
            coalesce(sum((usage->>'output')::bigint) FILTER(WHERE outcome<>1),0)::bigint AS output_tokens,
            coalesce(sum((usage->>'total')::bigint) FILTER(WHERE outcome<>1),0)::bigint AS total_tokens FROM observed
    )
    UPDATE braid.ai_usage_turns u SET phase=t.phase,finished_at=t.finished_at,
        rounds=r.rounds,completed_rounds=r.completed,failed_rounds=r.failed,
        known_input_rounds=r.inputs,known_output_rounds=r.outputs,known_total_rounds=r.totals,
        known_input_tokens=r.input_tokens,known_output_tokens=r.output_tokens,known_total_tokens=r.total_tokens,
        tool_calls=(SELECT count(*)::integer FROM ONLY braid.chat_events e WHERE e.organization_id=p_org
            AND e.creator_id=p_principal AND e.conversation_id=conversation AND e.turn_id=p_turn AND e.kind=3)
        FROM ONLY braid.chat_turns t,totals r
        WHERE u.organization_id=p_org AND u.principal_id=p_principal AND u.turn_id=p_turn
            AND t.organization_id=p_org AND t.creator_id=p_principal AND t.turn_id=p_turn;
    WITH failures AS (
        SELECT coalesce(r.result_metadata::jsonb->'failure'->>'code',
            CASE WHEN terminal->'failure'->>'round'=r.round_id::text THEN terminal->'failure'->>'code' END)::integer AS code
        FROM ONLY braid.chat_rounds r WHERE r.organization_id=p_org AND r.creator_id=p_principal
            AND r.conversation_id=conversation AND r.turn_id=p_turn
        UNION ALL
        SELECT t.phase+15 FROM ONLY braid.chat_turns t WHERE t.organization_id=p_org AND t.creator_id=p_principal
            AND t.turn_id=p_turn AND t.phase IN (3,4,5)
    )
    INSERT INTO braid.ai_usage_failures(organization_id,principal_id,turn_id,code,occurrences)
        SELECT p_org,p_principal,p_turn,code,count(*)::integer FROM failures WHERE code IS NOT NULL GROUP BY code
        ON CONFLICT(organization_id,principal_id,turn_id,code) DO UPDATE SET occurrences=EXCLUDED.occurrences;
END;
$body$;

DO $backfill$
DECLARE item record;
BEGIN
    FOR item IN SELECT organization_id,creator_id,turn_id FROM ONLY braid.chat_turns LOOP
        PERFORM braid.ai_usage_observe(item.organization_id,item.creator_id,item.turn_id);
    END LOOP;
END;
$backfill$;
-- Older erased conversations cannot be reconstructed. Only admissions after this
-- boundary have complete coverage; earlier retained rows are useful partial history.
INSERT INTO braid.ai_usage_coverage(singleton,complete_since) VALUES(true,clock_timestamp());

CREATE FUNCTION braid.ai_usage_capture() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_TABLE_NAME='chat_events' THEN
        IF NEW.kind NOT IN (3,5,6,7,8) THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='chat_turns' THEN
        IF TG_OP='UPDATE' THEN
            IF NEW.phase=OLD.phase THEN RETURN NEW; END IF;
        END IF;
    ELSIF TG_TABLE_NAME='chat_rounds' THEN
        IF TG_OP='UPDATE' THEN
            IF NEW.outcome=OLD.outcome THEN RETURN NEW; END IF;
        END IF;
    ELSE RAISE EXCEPTION 'Unknown usage source.' USING ERRCODE='22023';
    END IF;
    PERFORM braid.ai_usage_observe(NEW.organization_id,NEW.creator_id,NEW.turn_id);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER ai_usage_turn AFTER INSERT OR UPDATE ON braid.chat_turns FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_capture();
CREATE TRIGGER ai_usage_round AFTER INSERT OR UPDATE ON braid.chat_rounds FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_capture();
CREATE TRIGGER ai_usage_tool AFTER INSERT ON braid.chat_events FOR EACH ROW EXECUTE FUNCTION braid.ai_usage_capture();
REVOKE ALL ON FUNCTION braid.ai_usage_guard(),braid.ai_usage_observe(uuid,uuid,uuid),braid.ai_usage_capture() FROM PUBLIC;

CREATE FUNCTION braid.read_ai_usage(p_org uuid,p_actor uuid,p_project uuid,p_principal uuid,p_profile text,p_version integer,
    p_grouping text,p_since timestamp with time zone,p_until timestamp with time zone,p_after text,p_limit integer)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE org_admin boolean; result jsonb; coverage timestamp with time zone;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN NULL; END IF;
    IF p_org IS NULL OR p_actor IS NULL OR p_grouping IS NULL OR p_grouping NOT IN ('organization','project','principal','modelProfile')
        OR p_since IS NULL OR p_until IS NULL OR NOT isfinite(p_since) OR NOT isfinite(p_until) OR p_since>=p_until
        OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR p_version<1 OR p_version IS NOT NULL AND p_profile IS NULL
        OR p_profile IS NOT NULL AND p_profile !~ '^[a-z0-9][a-z0-9._-]{0,63}$'
        OR p_after IS NOT NULL AND octet_length(p_after)>128 THEN
        RAISE EXCEPTION 'Invalid usage query.' USING ERRCODE='22023';
    END IF;
    org_admin:=braid.administration_authorized(p_org,p_actor,NULL);
    IF NOT org_admin AND NOT EXISTS(SELECT 1 FROM ONLY braid.project_memberships m WHERE m.organization_id=p_org
        AND (p_project IS NULL OR m.project_id=p_project) AND m.principal_id=p_actor
        AND braid.administration_authorized(p_org,p_actor,m.project_id)) THEN RETURN NULL; END IF;
    SELECT complete_since INTO STRICT coverage FROM ONLY braid.ai_usage_coverage WHERE singleton;
    WITH filtered AS MATERIALIZED (
        SELECT u.*,CASE p_grouping WHEN 'organization' THEN p_org::text
            WHEN 'project' THEN coalesce(u.project_id::text,'-') WHEN 'principal' THEN u.principal_id::text
            ELSE u.profile_id||':'||u.profile_version::text END COLLATE "C" AS group_key
        FROM ONLY braid.ai_usage_turns u WHERE u.organization_id=p_org AND u.admitted_at>=p_since AND u.admitted_at<p_until
            AND (p_project IS NULL OR u.project_id=p_project) AND (p_principal IS NULL OR u.principal_id=p_principal)
            AND (p_profile IS NULL OR u.profile_id=p_profile) AND (p_version IS NULL OR u.profile_version=p_version)
            AND (org_admin OR u.project_id IS NOT NULL AND braid.administration_authorized(p_org,p_actor,u.project_id))
    ), grouped AS (
        SELECT group_key,min(project_id::text)::uuid AS project_id,min(principal_id::text)::uuid AS principal_id,
            min(profile_id) AS profile_id,min(profile_version) AS profile_version,count(*)::bigint AS turns,
            count(*) FILTER(WHERE phase IN (3,4,5))::bigint AS failed_turns,
            sum(rounds)::bigint AS rounds,sum(tool_calls)::bigint AS tool_calls,
            sum(known_input_tokens)::bigint AS known_input_tokens,sum(known_output_tokens)::bigint AS known_output_tokens,
            sum(known_total_tokens)::bigint AS known_total_tokens,
            sum(rounds-known_input_rounds)::bigint AS unknown_input_rounds,
            sum(rounds-known_output_rounds)::bigint AS unknown_output_rounds,
            sum(rounds-known_total_rounds)::bigint AS unknown_total_rounds
        FROM filtered GROUP BY group_key
    ), page AS (
        SELECT * FROM grouped WHERE p_after IS NULL OR group_key>p_after COLLATE "C" ORDER BY group_key LIMIT p_limit+1
    ), failures AS (
        SELECT u.group_key,f.code,sum(f.occurrences)::bigint AS occurrences FROM filtered u
            JOIN ONLY braid.ai_usage_failures f ON f.organization_id=u.organization_id AND f.principal_id=u.principal_id AND f.turn_id=u.turn_id
        WHERE u.group_key IN (SELECT group_key FROM page) GROUP BY u.group_key,f.code
    )
    SELECT jsonb_build_object('groups',coalesce((SELECT jsonb_agg(jsonb_build_object(
        'organizationId',p_org,'projectId',CASE WHEN p_grouping='project' THEN g.project_id ELSE p_project END,
        'principalId',CASE WHEN p_grouping='principal' THEN g.principal_id ELSE p_principal END,
        'profileId',CASE WHEN p_grouping='modelProfile' THEN g.profile_id ELSE p_profile END,
        'profileVersion',CASE WHEN p_grouping='modelProfile' THEN g.profile_version ELSE p_version END,
        'usage',jsonb_build_object('turns',g.turns::text,'rounds',g.rounds::text,'toolCalls',g.tool_calls::text,'failedTurns',g.failed_turns::text,
            'inputTokens',CASE WHEN g.unknown_input_rounds=0 THEN g.known_input_tokens::text ELSE NULL END,
            'outputTokens',CASE WHEN g.unknown_output_rounds=0 THEN g.known_output_tokens::text ELSE NULL END,
            'totalTokens',CASE WHEN g.unknown_total_rounds=0 THEN g.known_total_tokens::text ELSE NULL END,
            'knownInputTokens',g.known_input_tokens::text,'knownOutputTokens',g.known_output_tokens::text,'knownTotalTokens',g.known_total_tokens::text,
            'unknownInputRounds',g.unknown_input_rounds::text,'unknownOutputRounds',g.unknown_output_rounds::text,'unknownTotalRounds',g.unknown_total_rounds::text,
            'failures',coalesce((SELECT jsonb_agg(jsonb_build_object('category',(ARRAY['InvalidRequest','ProfileUnavailable','MetadataDisallowed',
                'CapabilityUnavailable','CredentialUnavailable','Busy','BudgetExceeded','Cancelled','DeadlineExceeded','TransportFailure',
                'ProviderAuthentication','ProviderRateLimited','ProviderRejected','ProviderUnavailable','UnsupportedResponse','MalformedResponse',
                 'TruncatedResponse','TurnFailed','TurnCancelled','TurnInterrupted']::text[])[f.code],'count',f.occurrences::text) ORDER BY f.code)
                FROM failures f WHERE f.group_key=g.group_key),'[]'::jsonb))
        ) ORDER BY g.group_key) FROM (SELECT * FROM page ORDER BY group_key LIMIT p_limit) g),'[]'::jsonb),
        'hasMore',(SELECT count(*)>p_limit FROM page),
        'lastKey',(SELECT group_key FROM page ORDER BY group_key OFFSET greatest(p_limit-1,0) LIMIT 1),
        'organizationWide',org_admin,'observedAtUtc',statement_timestamp(),'completeHistorySince',coverage,
        'includesPartialHistory',p_since<coverage) INTO result;
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_ai_usage(uuid,uuid,uuid,uuid,text,integer,text,timestamp with time zone,timestamp with time zone,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_ai_usage(uuid,uuid,uuid,uuid,text,integer,text,timestamp with time zone,timestamp with time zone,text,integer) TO braid_administration;

-- Configuration and new-turn admission serialize on one organization row.
-- Private chat erasure never removes usage or unsettled reservations.
CREATE TABLE braid.ai_quota_state (
    organization_id uuid PRIMARY KEY REFERENCES braid.organizations(id),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0)
);
CREATE TABLE braid.ai_quota_limits (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    subject_id uuid NOT NULL,
    period integer NOT NULL CHECK(period BETWEEN 1 AND 3),
    maximum_turns bigint CHECK(maximum_turns>0), maximum_tokens bigint CHECK(maximum_tokens>0),
    PRIMARY KEY(organization_id,subject_id,period),
    CHECK(maximum_turns IS NOT NULL OR maximum_tokens IS NOT NULL)
);
CREATE TABLE braid.ai_quota_reservations (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    principal_id uuid NOT NULL, conversation_id uuid NOT NULL, turn_id uuid NOT NULL,
    envelope_fingerprint text NOT NULL CHECK(envelope_fingerprint ~ '^[0-9a-f]{64}$'),
    profile_fingerprint text NOT NULL CHECK(profile_fingerprint ~ '^[0-9a-f]{64}$'),
    admitted_at timestamp with time zone NOT NULL CHECK(isfinite(admitted_at)),
    maximum_tokens bigint CHECK(maximum_tokens>0), authority_id text, authority_version integer,
    settled_tokens bigint CHECK(settled_tokens>=0),
    PRIMARY KEY(organization_id,principal_id,turn_id),
    CHECK(principal_id<>'00000000-0000-0000-0000-000000000000' AND conversation_id<>'00000000-0000-0000-0000-000000000000'
        AND turn_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK((maximum_tokens IS NULL AND authority_id IS NULL AND authority_version IS NULL) OR
        (maximum_tokens IS NOT NULL AND authority_id IS NOT NULL AND authority_id ~ '^[A-Za-z0-9.-]{1,64}$' AND authority_version IS NOT NULL AND authority_version>0))
);
CREATE INDEX ai_quota_reservation_window ON braid.ai_quota_reservations(organization_id,admitted_at,principal_id);
ALTER TABLE braid.ai_quota_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.ai_quota_limits ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.ai_quota_reservations ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.ai_quota_state USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.ai_quota_limits USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.ai_quota_reservations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.ai_quota_state,braid.ai_quota_limits,braid.ai_quota_reservations FROM PUBLIC;

CREATE FUNCTION braid.ai_quota_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP='TRUNCATE' OR
        (TG_OP='DELETE' AND TG_TABLE_NAME<>'ai_quota_limits') THEN
        RAISE EXCEPTION 'Quota records are owner-maintained.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='ai_quota_reservations' AND TG_OP='UPDATE' THEN
        IF ROW(NEW.organization_id,NEW.principal_id,NEW.conversation_id,NEW.turn_id,NEW.envelope_fingerprint,NEW.profile_fingerprint,
            NEW.admitted_at,NEW.maximum_tokens,NEW.authority_id,NEW.authority_version) IS DISTINCT FROM
            ROW(OLD.organization_id,OLD.principal_id,OLD.conversation_id,OLD.turn_id,OLD.envelope_fingerprint,OLD.profile_fingerprint,
            OLD.admitted_at,OLD.maximum_tokens,OLD.authority_id,OLD.authority_version)
            OR (OLD.settled_tokens IS NOT NULL AND NEW.settled_tokens IS DISTINCT FROM OLD.settled_tokens) THEN
            RAISE EXCEPTION 'Quota reservation identity is immutable.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER ai_quota_state_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_quota_state FOR EACH ROW EXECUTE FUNCTION braid.ai_quota_guard();
CREATE TRIGGER ai_quota_state_no_truncate BEFORE TRUNCATE ON braid.ai_quota_state FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_quota_guard();
CREATE TRIGGER ai_quota_limit_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_quota_limits FOR EACH ROW EXECUTE FUNCTION braid.ai_quota_guard();
CREATE TRIGGER ai_quota_limit_no_truncate BEFORE TRUNCATE ON braid.ai_quota_limits FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_quota_guard();
CREATE TRIGGER ai_quota_reservation_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.ai_quota_reservations FOR EACH ROW EXECUTE FUNCTION braid.ai_quota_guard();
CREATE TRIGGER ai_quota_reservation_no_truncate BEFORE TRUNCATE ON braid.ai_quota_reservations FOR EACH STATEMENT EXECUTE FUNCTION braid.ai_quota_guard();

CREATE FUNCTION braid.read_ai_quotas(p_org uuid,p_actor uuid,p_principal uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN NULL; END IF;
    IF p_principal IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.organization_memberships
        WHERE organization_id=p_org AND principal_id=p_principal) THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('organizationId',p_org,'principalId',p_principal,
        'version',coalesce((SELECT version FROM ONLY braid.ai_quota_state WHERE organization_id=p_org),1)::text,
        'limits',coalesce((SELECT jsonb_agg(jsonb_build_object('period',CASE period WHEN 1 THEN 'hour' WHEN 2 THEN 'day' ELSE 'month' END,
            'maximumTurns',maximum_turns::text,'maximumTokens',maximum_tokens::text) ORDER BY period)
            FROM ONLY braid.ai_quota_limits WHERE organization_id=p_org
                AND subject_id=coalesce(p_principal,'00000000-0000-0000-0000-000000000000')),'[]'::jsonb));
END;
$body$;

CREATE FUNCTION braid.write_ai_quota(p_org uuid,p_actor uuid,p_version bigint,p_principal uuid,p_period integer,p_turns bigint,p_tokens bigint) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE current_version bigint; subject uuid:=coalesce(p_principal,'00000000-0000-0000-0000-000000000000'); changed integer;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN 'refused'; END IF;
    IF p_version IS NULL OR p_version<1 OR p_period IS NULL OR p_period NOT BETWEEN 1 AND 3
        OR p_principal='00000000-0000-0000-0000-000000000000' OR p_turns<1 OR p_tokens<1 THEN RETURN 'invalidRequest'; END IF;
    INSERT INTO braid.ai_quota_state(organization_id) VALUES(p_org) ON CONFLICT DO NOTHING;
    SELECT version INTO STRICT current_version FROM ONLY braid.ai_quota_state WHERE organization_id=p_org FOR UPDATE;
    IF NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN 'refused'; END IF;
    IF current_version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF p_principal IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.organization_memberships
        WHERE organization_id=p_org AND principal_id=p_principal) THEN RETURN 'refused'; END IF;
    IF p_turns IS NULL AND p_tokens IS NULL THEN
        DELETE FROM braid.ai_quota_limits WHERE organization_id=p_org AND subject_id=subject AND period=p_period;
    ELSE
        INSERT INTO braid.ai_quota_limits(organization_id,subject_id,period,maximum_turns,maximum_tokens)
            VALUES(p_org,subject,p_period,p_turns,p_tokens)
            ON CONFLICT(organization_id,subject_id,period) DO UPDATE SET maximum_turns=EXCLUDED.maximum_turns,maximum_tokens=EXCLUDED.maximum_tokens
            WHERE ROW(ai_quota_limits.maximum_turns,ai_quota_limits.maximum_tokens) IS DISTINCT FROM ROW(EXCLUDED.maximum_turns,EXCLUDED.maximum_tokens);
    END IF;
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed=0 THEN RETURN 'unchanged'; END IF;
    UPDATE braid.ai_quota_state SET version=version+1 WHERE organization_id=p_org;
    PERFORM braid.audit_log_append('ai_quota_limits',gen_random_uuid()::text,p_org,NULL,NULL,p_actor,'user','administration',
        'quotaChanged',coalesce(p_principal,p_org),'updated',clock_timestamp(),false);
    RETURN 'updated';
END;
$body$;

CREATE FUNCTION braid.reserve_ai_quota(p_org uuid,p_actor uuid,p_conversation uuid,p_turn uuid,p_envelope text,p_profile text,
    p_deadline timestamp with time zone,p_maximum bigint,p_authority text,p_authority_version integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE c braid.chat_conversations%ROWTYPE; now_at timestamp with time zone; covered timestamp with time zone;
    born timestamp with time zone; start_at timestamp with time zone; rule record; turns numeric; tokens numeric; unknown boolean;
BEGIN
    IF session_user<>'braid_chat_writer' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS DISTINCT FROM NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid THEN
        RETURN jsonb_build_object('outcome','NotAuthorizedOrNotFound');
    END IF;
    SELECT * INTO c FROM ONLY braid.chat_conversations WHERE organization_id=p_org AND creator_id=p_actor AND conversation_id=p_conversation;
    IF c.conversation_id IS NULL OR NOT braid.chat_authorized(p_org,c.project_id,p_actor) THEN
        RETURN jsonb_build_object('outcome','NotAuthorizedOrNotFound'); END IF;
    IF p_turn IS NULL OR p_turn='00000000-0000-0000-0000-000000000000' OR p_envelope IS NULL OR p_envelope !~ '^[0-9a-f]{64}$'
        OR p_profile IS NULL OR p_profile !~ '^[0-9a-f]{64}$' OR p_deadline IS NULL OR NOT isfinite(p_deadline) OR p_maximum<1
        OR ((p_maximum IS NULL)<>(p_authority IS NULL)) OR ((p_maximum IS NULL)<>(p_authority_version IS NULL))
        OR p_authority !~ '^[A-Za-z0-9.-]{1,64}$' OR p_authority_version<1 THEN
        RAISE EXCEPTION 'Invalid quota proof.' USING ERRCODE='22023'; END IF;
    INSERT INTO braid.ai_quota_state(organization_id) VALUES(p_org) ON CONFLICT DO NOTHING;
    PERFORM 1 FROM ONLY braid.ai_quota_state WHERE organization_id=p_org FOR UPDATE;
    IF NOT braid.chat_authorized(p_org,c.project_id,p_actor) THEN RETURN jsonb_build_object('outcome','NotAuthorizedOrNotFound'); END IF;
    -- Reservation and turn insertion are one statement, using its existing ledger clock.
    -- Deadlines are also checked after any wait for the organization lock.
    now_at:=statement_timestamp();
    IF c.retain_until<=clock_timestamp() OR p_deadline<=clock_timestamp() OR p_deadline>c.retain_until THEN RETURN jsonb_build_object('outcome','Expired'); END IF;
    -- Replay was resolved by the caller before reserving. Erased identity is never new capacity.
    IF EXISTS(SELECT 1 FROM ONLY braid.ai_quota_reservations WHERE organization_id=p_org AND principal_id=p_actor AND turn_id=p_turn) THEN
        RETURN jsonb_build_object('outcome','Conflict'); END IF;
    SELECT complete_since INTO STRICT covered FROM ONLY braid.ai_usage_coverage WHERE singleton;
    SELECT created_at INTO STRICT born FROM ONLY braid.organizations WHERE id=p_org;
    FOR rule IN SELECT * FROM ONLY braid.ai_quota_limits WHERE organization_id=p_org
        AND subject_id IN ('00000000-0000-0000-0000-000000000000',p_actor) ORDER BY subject_id,period LOOP
        start_at:=date_trunc(CASE rule.period WHEN 1 THEN 'hour' WHEN 2 THEN 'day' ELSE 'month' END,now_at,'UTC');
        IF greatest(start_at,born)<covered THEN RETURN jsonb_build_object('outcome','QuotaCoverageUnavailable'); END IF;
        WITH charges AS (
            SELECT r.principal_id,r.admitted_at,coalesce(r.settled_tokens,r.maximum_tokens) AS charge
                FROM ONLY braid.ai_quota_reservations r WHERE r.organization_id=p_org
            UNION ALL
            SELECT u.principal_id,u.admitted_at,NULL::bigint FROM ONLY braid.ai_usage_turns u WHERE u.organization_id=p_org
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.ai_quota_reservations r WHERE r.organization_id=u.organization_id
                    AND r.principal_id=u.principal_id AND r.turn_id=u.turn_id)
        ) SELECT count(*),coalesce(sum(charge),0),coalesce(bool_or(charge IS NULL),false) INTO turns,tokens,unknown
            FROM charges WHERE admitted_at>=start_at
                AND (rule.subject_id='00000000-0000-0000-0000-000000000000' OR principal_id=p_actor);
        IF rule.maximum_turns IS NOT NULL AND turns>=rule.maximum_turns THEN RETURN jsonb_build_object('outcome','QuotaTurnLimitReached'); END IF;
        IF rule.maximum_tokens IS NOT NULL THEN
            IF p_maximum IS NULL THEN RETURN jsonb_build_object('outcome','QuotaTokenBoundUnavailable'); END IF;
            IF unknown THEN RETURN jsonb_build_object('outcome','QuotaCoverageUnavailable'); END IF;
            IF tokens+p_maximum>rule.maximum_tokens THEN RETURN jsonb_build_object('outcome','QuotaTokenLimitReached'); END IF;
        END IF;
    END LOOP;
    INSERT INTO braid.ai_quota_reservations(organization_id,principal_id,conversation_id,turn_id,envelope_fingerprint,profile_fingerprint,
        admitted_at,maximum_tokens,authority_id,authority_version)
        VALUES(p_org,p_actor,p_conversation,p_turn,p_envelope,p_profile,now_at,p_maximum,p_authority,p_authority_version);
    RETURN jsonb_build_object('outcome','Applied','admittedAt',now_at);
END;
$body$;

CREATE FUNCTION braid.ai_quota_require_reservation() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE admitted timestamp with time zone;
BEGIN
    SELECT r.admitted_at INTO admitted FROM ONLY braid.ai_quota_reservations r WHERE r.organization_id=NEW.organization_id
        AND r.principal_id=NEW.creator_id AND r.conversation_id=NEW.conversation_id AND r.turn_id=NEW.turn_id
        AND r.envelope_fingerprint=encode(sha256(convert_to(NEW.original_envelope,'UTF8')),'hex')
        AND r.profile_fingerprint=NEW.profile_fingerprint;
    IF admitted IS NULL OR NEW.created_at<>admitted THEN
        RAISE EXCEPTION 'Turn requires its exact quota reservation and admission time.' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER ai_quota_turn BEFORE INSERT ON braid.chat_turns FOR EACH ROW EXECUTE FUNCTION braid.ai_quota_require_reservation();

CREATE FUNCTION braid.ai_quota_settle() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    -- Reported terminal usage on an uncertain round is not proof of settlement.
    IF NEW.phase NOT IN (2,3) OR NEW.known_total_rounds<>NEW.rounds OR EXISTS(
        SELECT 1 FROM ONLY braid.chat_rounds r WHERE r.organization_id=NEW.organization_id AND r.creator_id=NEW.principal_id
            AND r.turn_id=NEW.turn_id AND r.outcome=1) THEN RETURN NEW; END IF;
    UPDATE braid.ai_quota_reservations SET settled_tokens=NEW.known_total_tokens
        WHERE organization_id=NEW.organization_id AND principal_id=NEW.principal_id AND turn_id=NEW.turn_id AND settled_tokens IS NULL;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER ai_quota_usage AFTER INSERT OR UPDATE ON braid.ai_usage_turns FOR EACH ROW EXECUTE FUNCTION braid.ai_quota_settle();
REVOKE ALL ON FUNCTION braid.ai_quota_guard(),braid.read_ai_quotas(uuid,uuid,uuid),braid.write_ai_quota(uuid,uuid,bigint,uuid,integer,bigint,bigint),
    braid.reserve_ai_quota(uuid,uuid,uuid,uuid,text,text,timestamp with time zone,bigint,text,integer),braid.ai_quota_require_reservation(),braid.ai_quota_settle() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_ai_quotas(uuid,uuid,uuid),braid.write_ai_quota(uuid,uuid,bigint,uuid,integer,bigint,bigint) TO braid_administration;
GRANT EXECUTE ON FUNCTION braid.reserve_ai_quota(uuid,uuid,uuid,uuid,text,text,timestamp with time zone,bigint,text,integer) TO braid_chat_writer;
