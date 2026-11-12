#!/usr/bin/env bash
set -euo pipefail

: "${WEFT_LOCAL_CHAT_READER_PASSWORD:?Set a separate chat reader password}"
: "${WEFT_LOCAL_CHAT_WRITER_PASSWORD:?Set a separate chat writer password}"
: "${WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD:?Set a separate chat lifecycle password}"
: "${WEFT_LOCAL_CHAT_APPROVER_PASSWORD:?Set a separate chat approver password}"
[[ "$WEFT_LOCAL_CHAT_READER_PASSWORD" != "$WEFT_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$WEFT_LOCAL_CHAT_READER_PASSWORD" &&
   "$WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD" != "$WEFT_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$WEFT_LOCAL_CHAT_APPROVER_PASSWORD" != "$WEFT_LOCAL_CHAT_READER_PASSWORD" &&
   "$WEFT_LOCAL_CHAT_APPROVER_PASSWORD" != "$WEFT_LOCAL_CHAT_WRITER_PASSWORD" &&
   "$WEFT_LOCAL_CHAT_APPROVER_PASSWORD" != "$WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD" ]] || {
  echo 'All chat profile passwords must be independent.' >&2; exit 1;
}

: "${WEFT_LOCAL_PREVIEW_READER_PASSWORD:?Set a separate preview reader password}"
: "${WEFT_LOCAL_PREVIEW_WRITER_PASSWORD:?Set a separate preview writer password}"
[[ "$WEFT_LOCAL_PREVIEW_READER_PASSWORD" != "$WEFT_LOCAL_PREVIEW_WRITER_PASSWORD" ]] || {
  echo 'Preview profile passwords must be independent.' >&2; exit 1;
}

regression_passwords=()
for role in CASE_READER CASE_WRITER RUN_READER RUN_WRITER COMPARISON BUILD_READER BUILD_WRITER BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER RELEASE_READER RELEASE_WRITER INVOCATION_GRANT_WRITER INVOCATION_WRITER API_AUDIT_WRITER DRAFT_READER DRAFT_WRITER; do
  variable=WEFT_LOCAL_${role}_PASSWORD
  [[ -n ${!variable:-} ]] || { echo "Set $variable." >&2; exit 1; }
  for previous in "${regression_passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Regression profile passwords must be independent.' >&2; exit 1; }
  done
  regression_passwords+=("${!variable}")
done
unset regression_passwords previous
export WEFT_LOCAL_ADMINISTRATION_PASSWORD=${WEFT_LOCAL_ADMINISTRATION_PASSWORD:-}
# An absent opt-in credential creates an unusable password login. Existing stacks
# can migrate without enabling administration or delivering another credential.

# This runs once inside this compose project's new, dedicated database.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv migrator_password WEFT_LOCAL_MIGRATOR_PASSWORD
\getenv runtime_password WEFT_LOCAL_RUNTIME_PASSWORD
\getenv ingest_password WEFT_LOCAL_INGEST_PASSWORD
\getenv catalog_password WEFT_LOCAL_CATALOG_PASSWORD
\getenv metadata_password WEFT_LOCAL_METADATA_PASSWORD
\getenv creator_password WEFT_LOCAL_CREATOR_PASSWORD
\getenv report_password WEFT_LOCAL_REPORT_PASSWORD
\getenv derivation_password WEFT_LOCAL_DERIVATION_PASSWORD
\getenv maintenance_password WEFT_LOCAL_MAINTENANCE_PASSWORD
\getenv identity_password WEFT_LOCAL_IDENTITY_PASSWORD
\getenv payload_permission_password WEFT_LOCAL_PAYLOAD_PERMISSION_PASSWORD
\getenv session_reader_password WEFT_LOCAL_SESSION_READER_PASSWORD
\getenv session_writer_password WEFT_LOCAL_SESSION_WRITER_PASSWORD
\getenv chat_reader_password WEFT_LOCAL_CHAT_READER_PASSWORD
\getenv chat_writer_password WEFT_LOCAL_CHAT_WRITER_PASSWORD
\getenv chat_lifecycle_password WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD
\getenv chat_approver_password WEFT_LOCAL_CHAT_APPROVER_PASSWORD
\getenv preview_reader_password WEFT_LOCAL_PREVIEW_READER_PASSWORD
\getenv preview_writer_password WEFT_LOCAL_PREVIEW_WRITER_PASSWORD
\getenv case_reader_password WEFT_LOCAL_CASE_READER_PASSWORD
\getenv case_writer_password WEFT_LOCAL_CASE_WRITER_PASSWORD
\getenv run_reader_password WEFT_LOCAL_RUN_READER_PASSWORD
\getenv run_writer_password WEFT_LOCAL_RUN_WRITER_PASSWORD
\getenv comparison_password WEFT_LOCAL_COMPARISON_PASSWORD
\getenv build_reader_password WEFT_LOCAL_BUILD_READER_PASSWORD
\getenv build_writer_password WEFT_LOCAL_BUILD_WRITER_PASSWORD
\getenv build_run_reader_password WEFT_LOCAL_BUILD_RUN_READER_PASSWORD
\getenv build_run_writer_password WEFT_LOCAL_BUILD_RUN_WRITER_PASSWORD
\getenv build_dispatcher_password WEFT_LOCAL_BUILD_DISPATCHER_PASSWORD
\getenv build_metadata_password WEFT_LOCAL_BUILD_METADATA_PASSWORD
\getenv qualification_reader_password WEFT_LOCAL_QUALIFICATION_READER_PASSWORD
\getenv qualification_writer_password WEFT_LOCAL_QUALIFICATION_WRITER_PASSWORD
\getenv release_reader_password WEFT_LOCAL_RELEASE_READER_PASSWORD
\getenv release_writer_password WEFT_LOCAL_RELEASE_WRITER_PASSWORD
\getenv invocation_grant_writer_password WEFT_LOCAL_INVOCATION_GRANT_WRITER_PASSWORD
\getenv invocation_writer_password WEFT_LOCAL_INVOCATION_WRITER_PASSWORD
\getenv api_audit_writer_password WEFT_LOCAL_API_AUDIT_WRITER_PASSWORD
\getenv administration_password WEFT_LOCAL_ADMINISTRATION_PASSWORD
\getenv draft_reader_password WEFT_LOCAL_DRAFT_READER_PASSWORD
\getenv draft_writer_password WEFT_LOCAL_DRAFT_WRITER_PASSWORD
CREATE ROLE weft_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'migrator_password';
CREATE ROLE weft_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS
  PASSWORD :'runtime_password';
CREATE ROLE weft_ingest LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'ingest_password';
CREATE ROLE weft_catalog_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'catalog_password';
CREATE ROLE weft_catalog_editor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'metadata_password';
CREATE ROLE weft_project_creator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'creator_password';
CREATE ROLE weft_report_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'report_password';
CREATE ROLE weft_deriver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'derivation_password';
CREATE ROLE weft_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'maintenance_password';
CREATE ROLE weft_identity_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'identity_password';
CREATE ROLE weft_payload_permission_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'payload_permission_password';
CREATE ROLE weft_session_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_reader_password';
CREATE ROLE weft_session_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'session_writer_password';
CREATE ROLE weft_chat_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_reader_password';
CREATE ROLE weft_chat_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_writer_password';
CREATE ROLE weft_chat_lifecycle LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_lifecycle_password';
CREATE ROLE weft_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'chat_approver_password';
CREATE ROLE weft_preview_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_reader_password';
CREATE ROLE weft_preview_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'preview_writer_password';
CREATE ROLE weft_case_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_reader_password';
CREATE ROLE weft_case_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_writer_password';
CREATE ROLE weft_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_reader_password';
CREATE ROLE weft_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_writer_password';
CREATE ROLE weft_comparison_settler LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'comparison_password';
CREATE ROLE weft_build_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_reader_password';
CREATE ROLE weft_build_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_writer_password';
CREATE ROLE weft_build_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_run_reader_password';
CREATE ROLE weft_build_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_run_writer_password';
CREATE ROLE weft_build_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_dispatcher_password';
CREATE ROLE weft_build_metadata_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'build_metadata_password';
CREATE ROLE weft_qualification_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'qualification_reader_password';
CREATE ROLE weft_qualification_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'qualification_writer_password';
CREATE ROLE weft_release_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'release_reader_password';
CREATE ROLE weft_release_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'release_writer_password';
CREATE ROLE weft_invocation_grant_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'invocation_grant_writer_password';
CREATE ROLE weft_invocation_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'invocation_writer_password';
CREATE ROLE weft_api_audit_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'api_audit_writer_password';
CREATE ROLE weft_administration LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'administration_password';
CREATE ROLE weft_draft_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'draft_reader_password';
CREATE ROLE weft_draft_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'draft_writer_password';
ALTER DATABASE weft OWNER TO weft_migrator;
CREATE SCHEMA weft_search_extensions AUTHORIZATION weft_migrator;
CREATE EXTENSION pg_trgm WITH SCHEMA weft_search_extensions;
REVOKE ALL ON SCHEMA weft_search_extensions FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA weft_search_extensions FROM PUBLIC;
-- Chat routines pin these scope settings in function SET clauses.
GRANT SET ON PARAMETER weft.organization_id, weft.chat_principal_id TO weft_migrator;
REVOKE ALL ON DATABASE weft FROM PUBLIC;
GRANT CONNECT ON DATABASE weft TO weft_runtime, weft_ingest, weft_catalog_reader, weft_catalog_editor, weft_project_creator, weft_report_reader, weft_deriver, weft_maintenance, weft_identity_reader, weft_payload_permission_reader, weft_session_reader, weft_session_writer, weft_chat_reader, weft_chat_writer, weft_chat_lifecycle, weft_chat_approver, weft_preview_reader, weft_preview_writer, weft_case_reader, weft_case_writer, weft_run_reader, weft_run_writer, weft_build_reader, weft_build_writer, weft_build_run_reader, weft_build_run_writer, weft_build_dispatcher, weft_build_metadata_reader;
GRANT CONNECT ON DATABASE weft TO weft_qualification_reader,weft_qualification_writer,weft_comparison_settler;
GRANT CONNECT ON DATABASE weft TO weft_release_reader,weft_release_writer;
GRANT CONNECT ON DATABASE weft TO weft_invocation_grant_writer,weft_invocation_writer;
GRANT CONNECT ON DATABASE weft TO weft_api_audit_writer;
GRANT CONNECT ON DATABASE weft TO weft_administration;
GRANT CONNECT ON DATABASE weft TO weft_draft_reader,weft_draft_writer;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
