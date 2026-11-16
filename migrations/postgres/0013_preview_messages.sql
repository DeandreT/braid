-- Durable preview job delivery. Wolverine creates and owns its envelope and queue tables in
-- this separate schema through the preview writer login; no braid table or grant changes.
CREATE SCHEMA braid_messages AUTHORIZATION braid_migrator;
REVOKE ALL ON SCHEMA braid_messages FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA braid_messages TO braid_preview_writer;
