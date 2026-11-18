-- AS2 protocol evidence, not a second outbox. Only a live shared outbox claim can
-- acquire this one-shot permit. Outbox expiry/observation owns all state transitions.
CREATE TABLE braid.as2_send_fences (
    organization_id uuid NOT NULL, delivery_id uuid NOT NULL, claim_id uuid NOT NULL, send_number integer NOT NULL CHECK(send_number BETWEEN 1 AND 32),
    binding_version text NOT NULL, payload_sha256 text NOT NULL CHECK(payload_sha256~'^[0-9a-f]{64}$'),
    message_id text NOT NULL, mic text NOT NULL CHECK(mic~'^[A-Za-z0-9+/]{43}=$'),
    sender text NOT NULL CHECK(sender~'^[A-Za-z0-9_.-]{1,128}$'), recipient text NOT NULL CHECK(recipient~'^[A-Za-z0-9_.-]{1,128}$'),
    receipt_cert_sha256 text NOT NULL CHECK(receipt_cert_sha256~'^[0-9A-F]{64}$'),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,delivery_id,send_number), UNIQUE(organization_id,claim_id),
    FOREIGN KEY(organization_id,delivery_id) REFERENCES braid.trigger_deliveries(organization_id,delivery_id)
);
ALTER TABLE braid.as2_send_fences ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.as2_send_fences USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.as2_send_fences FROM PUBLIC;

CREATE FUNCTION braid.begin_as2_send(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, delivery uuid, claim uuid, attempt integer, receipt uuid, binding uuid, connector text, version bigint, pin text, sha text, message text, mic_value text, sender_id text, recipient_id text, cert text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE expires timestamp with time zone;
BEGIN
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN false; END IF;
    SELECT d.claim_expires_at INTO expires FROM ONLY braid.trigger_deliveries d
        JOIN ONLY braid.trigger_work w ON w.organization_id=d.organization_id AND w.work_id=d.work_id
        JOIN ONLY braid.trigger_receipts r ON r.organization_id=d.organization_id AND r.receipt_id=d.receipt_id AND r.work_id=w.work_id
        JOIN ONLY braid.local_release_targets t ON t.organization_id=d.organization_id AND t.target_id=d.target_id
        WHERE d.organization_id=org AND d.delivery_id=delivery AND d.phase='sending' AND d.claim_id=claim AND d.sends=attempt
        AND d.target_id=target AND w.project_id=project AND w.mapping_id=mapping AND t.environment_id=environment AND t.environment_version=target_version
        AND d.destination_kind='as2' AND d.destination_binding_id=binding AND d.connector_id=connector AND d.connector_version=version
        AND r.direction='output' AND r.receipt_id=receipt AND r.digest=sha AND lower(r.media_type)='application/edi-x12'
        AND pin=binding::text||'/'||connector||'/'||version::text AND message='<'||replace(delivery::text,'-','')||'@braid.invalid>'
        FOR UPDATE OF d;
    IF NOT FOUND OR expires<=clock_timestamp() THEN RETURN false; END IF;
    INSERT INTO braid.as2_send_fences(organization_id,delivery_id,claim_id,send_number,binding_version,payload_sha256,message_id,mic,sender,recipient,receipt_cert_sha256)
        VALUES(org,delivery,claim,attempt,pin,sha,message,mic_value,sender_id,recipient_id,cert) ON CONFLICT DO NOTHING;
    RETURN FOUND;
END;
$body$;
REVOKE ALL ON FUNCTION braid.begin_as2_send(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,integer,uuid,uuid,text,bigint,text,text,text,text,text,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.begin_as2_send(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,integer,uuid,uuid,text,bigint,text,text,text,text,text,text,text) TO braid_trigger_worker;
