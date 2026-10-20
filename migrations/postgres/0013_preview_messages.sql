-- Durable preview job delivery. Wolverine creates and owns its envelope and queue tables in
-- this separate schema through the preview writer login; no weft table or grant changes.
CREATE SCHEMA weft_messages AUTHORIZATION weft_migrator;
REVOKE ALL ON SCHEMA weft_messages FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA weft_messages TO weft_preview_writer;
