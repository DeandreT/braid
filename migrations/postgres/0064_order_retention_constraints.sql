-- A pointer may move or retire. Its old release is still input to every
-- accepted order that pinned it, including trigger orders not yet claimed.
-- These references check the exact tenant/target keys at mutation time, even
-- if a purge candidate was discovered before a concurrent order admission.
ALTER TABLE braid.host_order_pins
    ADD CONSTRAINT host_order_retains_invocation FOREIGN KEY
        (organization_id,target_id,provider,tenant_id,object_id,request_id)
        REFERENCES braid.machine_invocations(organization_id,target_id,provider,tenant_id,object_id,request_id) ON DELETE RESTRICT,
    ADD CONSTRAINT host_order_retains_release FOREIGN KEY (organization_id,target_id,release_id)
        REFERENCES braid.local_releases(organization_id,target_id,release_id) ON DELETE RESTRICT;
ALTER TABLE braid.trigger_work
    ADD CONSTRAINT trigger_order_retains_release FOREIGN KEY (organization_id,target_id,release_id)
        REFERENCES braid.local_releases(organization_id,target_id,release_id) ON DELETE RESTRICT;
CREATE INDEX host_order_release_reference ON braid.host_order_pins(organization_id,target_id,release_id);
CREATE INDEX trigger_work_release_reference ON braid.trigger_work(organization_id,target_id,release_id);
