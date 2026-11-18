#!/usr/bin/env bash
set -euo pipefail

: "${BRAID_LOCAL_CHAT_READER_PASSWORD:?Set a separate chat reader password}"
: "${BRAID_LOCAL_CHAT_WRITER_PASSWORD:?Set a separate chat writer password}"
: "${BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD:?Set a separate chat lifecycle password}"
: "${BRAID_LOCAL_CHAT_APPROVER_PASSWORD:?Set a separate chat approver password}"
[[ "$BRAID_LOCAL_CHAT_READER_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$BRAID_LOCAL_CHAT_READER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_READER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$BRAID_LOCAL_CHAT_APPROVER_PASSWORD" != "$BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD" ]] || {
  echo 'All chat profile passwords must be independent.' >&2; exit 1;
}

: "${BRAID_LOCAL_PREVIEW_READER_PASSWORD:?Set a separate preview reader password}"
: "${BRAID_LOCAL_PREVIEW_WRITER_PASSWORD:?Set a separate preview writer password}"
[[ "$BRAID_LOCAL_PREVIEW_READER_PASSWORD" != "$BRAID_LOCAL_PREVIEW_WRITER_PASSWORD" ]] || {
  echo 'Preview profile passwords must be independent.' >&2; exit 1;
}

regression_passwords=()
: "${BRAID_LOCAL_TRIGGER_WORKER_PASSWORD:?Set a separate trigger worker password}"
for role in CASE_READER CASE_WRITER RUN_READER RUN_WRITER COMPARISON BUILD_READER BUILD_WRITER BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER RELEASE_READER RELEASE_WRITER INVOCATION_GRANT_WRITER INVOCATION_WRITER API_AUDIT_WRITER DRAFT_READER DRAFT_WRITER; do
  variable=BRAID_LOCAL_${role}_PASSWORD
  [[ -n ${!variable:-} ]] || { echo "Set $variable." >&2; exit 1; }
  for previous in "${regression_passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Regression profile passwords must be independent.' >&2; exit 1; }
  done
  regression_passwords+=("${!variable}")
done
unset regression_passwords previous
export BRAID_LOCAL_ADMINISTRATION_PASSWORD=${BRAID_LOCAL_ADMINISTRATION_PASSWORD:-}
export BRAID_LOCAL_SPEC_READER_PASSWORD=${BRAID_LOCAL_SPEC_READER_PASSWORD:-}
export BRAID_LOCAL_SPEC_WRITER_PASSWORD=${BRAID_LOCAL_SPEC_WRITER_PASSWORD:-}
export BRAID_LOCAL_SPEC_MAINTENANCE_PASSWORD=${BRAID_LOCAL_SPEC_MAINTENANCE_PASSWORD:-}
export BRAID_LOCAL_CREATION_READER_PASSWORD=${BRAID_LOCAL_CREATION_READER_PASSWORD:-}
export BRAID_LOCAL_CREATION_WRITER_PASSWORD=${BRAID_LOCAL_CREATION_WRITER_PASSWORD:-}
export BRAID_LOCAL_CREATION_APPROVER_PASSWORD=${BRAID_LOCAL_CREATION_APPROVER_PASSWORD:-}
creation_passwords=()
for role in READER WRITER APPROVER; do
  variable=BRAID_LOCAL_CREATION_${role}_PASSWORD
  [[ -n ${!variable} ]] || continue
  for previous in "${creation_passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Creation profile passwords must be independent.' >&2; exit 1; }
  done
  creation_passwords+=("${!variable}")
done
unset creation_passwords previous
export BRAID_LOCAL_ORGANIZATION_DISCOVERY_PASSWORD=${BRAID_LOCAL_ORGANIZATION_DISCOVERY_PASSWORD:-}
# An absent opt-in credential creates an unusable password login. Existing stacks
# can migrate without enabling administration or delivering another credential.

# This runs once inside this compose project's new, dedicated database.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv migrator_password BRAID_LOCAL_MIGRATOR_PASSWORD
\getenv runtime_password BRAID_LOCAL_RUNTIME_PASSWORD
\getenv ingest_password BRAID_LOCAL_INGEST_PASSWORD
\getenv catalog_password BRAID_LOCAL_CATALOG_PASSWORD
\getenv metadata_password BRAID_LOCAL_METADATA_PASSWORD
\getenv creator_password BRAID_LOCAL_CREATOR_PASSWORD
\getenv report_password BRAID_LOCAL_REPORT_PASSWORD
\getenv derivation_password BRAID_LOCAL_DERIVATION_PASSWORD
\getenv maintenance_password BRAID_LOCAL_MAINTENANCE_PASSWORD
\getenv identity_password BRAID_LOCAL_IDENTITY_PASSWORD
\getenv payload_permission_password BRAID_LOCAL_PAYLOAD_PERMISSION_PASSWORD
\getenv session_reader_password BRAID_LOCAL_SESSION_READER_PASSWORD
\getenv session_writer_password BRAID_LOCAL_SESSION_WRITER_PASSWORD
\getenv chat_reader_password BRAID_LOCAL_CHAT_READER_PASSWORD
\getenv chat_writer_password BRAID_LOCAL_CHAT_WRITER_PASSWORD
\getenv chat_lifecycle_password BRAID_LOCAL_CHAT_LIFECYCLE_PASSWORD
\getenv chat_approver_password BRAID_LOCAL_CHAT_APPROVER_PASSWORD
\getenv preview_reader_password BRAID_LOCAL_PREVIEW_READER_PASSWORD
\getenv preview_writer_password BRAID_LOCAL_PREVIEW_WRITER_PASSWORD
\getenv case_reader_password BRAID_LOCAL_CASE_READER_PASSWORD
\getenv case_writer_password BRAID_LOCAL_CASE_WRITER_PASSWORD
\getenv run_reader_password BRAID_LOCAL_RUN_READER_PASSWORD
\getenv run_writer_password BRAID_LOCAL_RUN_WRITER_PASSWORD
\getenv comparison_password BRAID_LOCAL_COMPARISON_PASSWORD
\getenv build_reader_password BRAID_LOCAL_BUILD_READER_PASSWORD
\getenv build_writer_password BRAID_LOCAL_BUILD_WRITER_PASSWORD
\getenv build_run_reader_password BRAID_LOCAL_BUILD_RUN_READER_PASSWORD
\getenv build_run_writer_password BRAID_LOCAL_BUILD_RUN_WRITER_PASSWORD
\getenv build_dispatcher_password BRAID_LOCAL_BUILD_DISPATCHER_PASSWORD
\getenv build_metadata_password BRAID_LOCAL_BUILD_METADATA_PASSWORD
\getenv qualification_reader_password BRAID_LOCAL_QUALIFICATION_READER_PASSWORD
\getenv qualification_writer_password BRAID_LOCAL_QUALIFICATION_WRITER_PASSWORD
\getenv release_reader_password BRAID_LOCAL_RELEASE_READER_PASSWORD
\getenv release_writer_password BRAID_LOCAL_RELEASE_WRITER_PASSWORD
\getenv invocation_grant_writer_password BRAID_LOCAL_INVOCATION_GRANT_WRITER_PASSWORD
\getenv trigger_worker_password BRAID_LOCAL_TRIGGER_WORKER_PASSWORD
\getenv invocation_writer_password BRAID_LOCAL_INVOCATION_WRITER_PASSWORD
\getenv api_audit_writer_password BRAID_LOCAL_API_AUDIT_WRITER_PASSWORD
\getenv administration_password BRAID_LOCAL_ADMINISTRATION_PASSWORD
\getenv draft_reader_password BRAID_LOCAL_DRAFT_READER_PASSWORD
\getenv draft_writer_password BRAID_LOCAL_DRAFT_WRITER_PASSWORD
\getenv spec_reader_password BRAID_LOCAL_SPEC_READER_PASSWORD
\getenv spec_writer_password BRAID_LOCAL_SPEC_WRITER_PASSWORD
\getenv spec_maintenance_password BRAID_LOCAL_SPEC_MAINTENANCE_PASSWORD
\getenv creation_reader_password BRAID_LOCAL_CREATION_READER_PASSWORD
\getenv creation_writer_password BRAID_LOCAL_CREATION_WRITER_PASSWORD
\getenv creation_approver_password BRAID_LOCAL_CREATION_APPROVER_PASSWORD
\getenv discovery_password BRAID_LOCAL_ORGANIZATION_DISCOVERY_PASSWORD
CREATE ROLE braid_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'migrator_password';
CREATE ROLE braid_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'runtime_password';
CREATE ROLE braid_ingest LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'ingest_password';
CREATE ROLE braid_catalog_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'catalog_password';
CREATE ROLE braid_catalog_editor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'metadata_password';
CREATE ROLE braid_project_creator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'creator_password';
CREATE ROLE braid_report_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'report_password';
CREATE ROLE braid_deriver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'derivation_password';
CREATE ROLE braid_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'maintenance_password';
CREATE ROLE braid_identity_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'identity_password';
CREATE ROLE braid_payload_permission_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'payload_permission_password';
CREATE ROLE braid_session_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_reader_password';
CREATE ROLE braid_session_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_writer_password';
CREATE ROLE braid_chat_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_reader_password';
CREATE ROLE braid_chat_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_writer_password';
CREATE ROLE braid_chat_lifecycle LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_lifecycle_password';
CREATE ROLE braid_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_approver_password';
CREATE ROLE braid_preview_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_reader_password';
CREATE ROLE braid_preview_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_writer_password';
CREATE ROLE braid_case_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_reader_password';
CREATE ROLE braid_case_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_writer_password';
CREATE ROLE braid_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_reader_password';
CREATE ROLE braid_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_writer_password';
CREATE ROLE braid_comparison_settler LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'comparison_password';
CREATE ROLE braid_build_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_reader_password';
CREATE ROLE braid_build_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_writer_password';
CREATE ROLE braid_build_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_run_reader_password';
CREATE ROLE braid_build_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_run_writer_password';
CREATE ROLE braid_build_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_dispatcher_password';
CREATE ROLE braid_build_metadata_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_metadata_password';
CREATE ROLE braid_qualification_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'qualification_reader_password';
CREATE ROLE braid_qualification_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'qualification_writer_password';
CREATE ROLE braid_release_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'release_reader_password';
CREATE ROLE braid_release_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'release_writer_password';
CREATE ROLE braid_invocation_grant_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'invocation_grant_writer_password';
CREATE ROLE braid_trigger_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'trigger_worker_password';
CREATE ROLE braid_invocation_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'invocation_writer_password';
CREATE ROLE braid_api_audit_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'api_audit_writer_password';
CREATE ROLE braid_administration LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'administration_password';
CREATE ROLE braid_draft_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'draft_reader_password';
CREATE ROLE braid_draft_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'draft_writer_password';
-- Disabled intake still needs CONNECT-only roles for migration 0044. Empty
-- passwords are unusable; enabling intake requires separately delivered secrets.
CREATE ROLE braid_spec_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'spec_reader_password';
CREATE ROLE braid_spec_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'spec_writer_password';
CREATE ROLE braid_spec_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'spec_maintenance_password';
CREATE ROLE braid_creation_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'creation_reader_password';
CREATE ROLE braid_creation_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'creation_writer_password';
CREATE ROLE braid_creation_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'creation_approver_password';
CREATE ROLE braid_organization_discovery LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'discovery_password';
-- No password unless explicitly provisioned; never reuse the migration login.
CREATE ROLE braid_lifecycle_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
ALTER DATABASE braid OWNER TO braid_migrator;
CREATE SCHEMA braid_search_extensions AUTHORIZATION braid_migrator;
CREATE EXTENSION pg_trgm WITH SCHEMA braid_search_extensions;
REVOKE ALL ON SCHEMA braid_search_extensions FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA braid_search_extensions FROM PUBLIC;
-- Chat routines pin these scope settings in function SET clauses.
GRANT SET ON PARAMETER braid.organization_id, braid.chat_principal_id TO braid_migrator;
REVOKE ALL ON DATABASE braid FROM PUBLIC;
GRANT CONNECT ON DATABASE braid TO braid_runtime, braid_ingest, braid_catalog_reader, braid_catalog_editor, braid_project_creator, braid_report_reader, braid_deriver, braid_maintenance, braid_identity_reader, braid_payload_permission_reader, braid_session_reader, braid_session_writer, braid_chat_reader, braid_chat_writer, braid_chat_lifecycle, braid_chat_approver, braid_preview_reader, braid_preview_writer, braid_case_reader, braid_case_writer, braid_run_reader, braid_run_writer, braid_build_reader, braid_build_writer, braid_build_run_reader, braid_build_run_writer, braid_build_dispatcher, braid_build_metadata_reader;
GRANT CONNECT ON DATABASE braid TO braid_qualification_reader,braid_qualification_writer,braid_comparison_settler;
GRANT CONNECT ON DATABASE braid TO braid_release_reader,braid_release_writer;
GRANT CONNECT ON DATABASE braid TO braid_invocation_grant_writer,braid_invocation_writer,braid_trigger_worker;
GRANT CONNECT ON DATABASE braid TO braid_api_audit_writer;
GRANT CONNECT ON DATABASE braid TO braid_administration;
GRANT CONNECT ON DATABASE braid TO braid_lifecycle_worker, braid_organization_discovery;
GRANT CONNECT ON DATABASE braid TO braid_draft_reader,braid_draft_writer;
GRANT CONNECT ON DATABASE braid TO braid_spec_reader,braid_spec_writer,braid_spec_maintenance;
GRANT CONNECT ON DATABASE braid TO braid_creation_reader,braid_creation_writer,braid_creation_approver;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
