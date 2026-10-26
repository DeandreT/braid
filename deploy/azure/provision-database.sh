#!/usr/bin/env bash
# Database administrator step for Azure Database for PostgreSQL Flexible Server: create the
# `weft` database, the restricted logins, the migrator's database ownership and the parameter
# grant required by the chat routines. Runs once, as the server administrator (a member of
# azure_pg_admin, not a superuser), from the provisioning job. Passwords come from the
# environment and are read by psql \getenv, never from arguments. The role transaction is
# all-or-nothing and refuses existing roles; it never rotates a password or widens a grant.
set -euo pipefail

roles=(MIGRATOR RUNTIME INGEST CATALOG METADATA CREATOR REPORT DERIVATION MAINTENANCE IDENTITY
  PAYLOAD_PERMISSION SESSION_READER SESSION_WRITER CHAT_READER CHAT_WRITER CHAT_LIFECYCLE
  PREVIEW_READER PREVIEW_WRITER CASE_READER CASE_WRITER RUN_READER RUN_WRITER COMPARISON BUILD_READER BUILD_WRITER BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER RELEASE_READER RELEASE_WRITER INVOCATION_GRANT_WRITER INVOCATION_WRITER API_AUDIT_WRITER)
for role in "${roles[@]}"; do
  variable=WEFT_${role}_PASSWORD
  [[ -n ${!variable:-} ]] || { echo "Set $variable." >&2; exit 2; }
done
[[ $WEFT_CHAT_READER_PASSWORD != "$WEFT_CHAT_WRITER_PASSWORD" &&
  $WEFT_CHAT_LIFECYCLE_PASSWORD != "$WEFT_CHAT_READER_PASSWORD" &&
  $WEFT_CHAT_LIFECYCLE_PASSWORD != "$WEFT_CHAT_WRITER_PASSWORD" ]] || {
  echo 'All chat profile passwords must be independent.' >&2
  exit 2
}
[[ $WEFT_PREVIEW_READER_PASSWORD != "$WEFT_PREVIEW_WRITER_PASSWORD" ]] || {
  echo 'Preview profile passwords must be independent.' >&2
  exit 2
}
regression_passwords=()
for role in CASE_READER CASE_WRITER RUN_READER RUN_WRITER COMPARISON BUILD_READER BUILD_WRITER BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER RELEASE_READER RELEASE_WRITER INVOCATION_GRANT_WRITER INVOCATION_WRITER API_AUDIT_WRITER; do
  variable=WEFT_${role}_PASSWORD
  for previous in "${regression_passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Regression profile passwords must be independent.' >&2; exit 2; }
  done
  regression_passwords+=("${!variable}")
done
unset regression_passwords previous
export WEFT_ADMINISTRATION_PASSWORD=${WEFT_ADMINISTRATION_PASSWORD:-}
# The role is required by the schema even without administration enabled. An
# absent opt-in credential leaves its password login unusable.

if [[ -z $(psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname=postgres --tuples-only --no-align \
  --command="SELECT 1 FROM pg_database WHERE datname = 'weft'") ]]; then
  psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname=postgres --quiet \
    --command='CREATE DATABASE weft TEMPLATE template0 ENCODING UTF8'
fi

psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname=weft --quiet <<'SQL'
\getenv migrator_password WEFT_MIGRATOR_PASSWORD
\getenv runtime_password WEFT_RUNTIME_PASSWORD
\getenv ingest_password WEFT_INGEST_PASSWORD
\getenv catalog_password WEFT_CATALOG_PASSWORD
\getenv metadata_password WEFT_METADATA_PASSWORD
\getenv creator_password WEFT_CREATOR_PASSWORD
\getenv report_password WEFT_REPORT_PASSWORD
\getenv derivation_password WEFT_DERIVATION_PASSWORD
\getenv maintenance_password WEFT_MAINTENANCE_PASSWORD
\getenv identity_password WEFT_IDENTITY_PASSWORD
\getenv payload_permission_password WEFT_PAYLOAD_PERMISSION_PASSWORD
\getenv session_reader_password WEFT_SESSION_READER_PASSWORD
\getenv session_writer_password WEFT_SESSION_WRITER_PASSWORD
\getenv chat_reader_password WEFT_CHAT_READER_PASSWORD
\getenv chat_writer_password WEFT_CHAT_WRITER_PASSWORD
\getenv chat_lifecycle_password WEFT_CHAT_LIFECYCLE_PASSWORD
\getenv preview_reader_password WEFT_PREVIEW_READER_PASSWORD
\getenv preview_writer_password WEFT_PREVIEW_WRITER_PASSWORD
\getenv case_reader_password WEFT_CASE_READER_PASSWORD
\getenv case_writer_password WEFT_CASE_WRITER_PASSWORD
\getenv run_reader_password WEFT_RUN_READER_PASSWORD
\getenv run_writer_password WEFT_RUN_WRITER_PASSWORD
\getenv comparison_password WEFT_COMPARISON_PASSWORD
\getenv build_reader_password WEFT_BUILD_READER_PASSWORD
\getenv build_writer_password WEFT_BUILD_WRITER_PASSWORD
\getenv build_run_reader_password WEFT_BUILD_RUN_READER_PASSWORD
\getenv build_run_writer_password WEFT_BUILD_RUN_WRITER_PASSWORD
\getenv build_dispatcher_password WEFT_BUILD_DISPATCHER_PASSWORD
\getenv build_metadata_password WEFT_BUILD_METADATA_PASSWORD
\getenv qualification_reader_password WEFT_QUALIFICATION_READER_PASSWORD
\getenv qualification_writer_password WEFT_QUALIFICATION_WRITER_PASSWORD
\getenv release_reader_password WEFT_RELEASE_READER_PASSWORD
\getenv release_writer_password WEFT_RELEASE_WRITER_PASSWORD
\getenv invocation_grant_writer_password WEFT_INVOCATION_GRANT_WRITER_PASSWORD
\getenv invocation_writer_password WEFT_INVOCATION_WRITER_PASSWORD
\getenv api_audit_writer_password WEFT_API_AUDIT_WRITER_PASSWORD
\getenv administration_password WEFT_ADMINISTRATION_PASSWORD
BEGIN;
-- PostgreSQL 16 records a non-superuser creator as an ADMIN member of every role it creates,
-- and the migrations refuse any membership on the restricted profiles. A temporary creator
-- carries those implicit memberships and is dropped, with them, before commit.
CREATE ROLE weft_provisioner NOLOGIN NOINHERIT CREATEROLE CREATEDB;
GRANT weft_provisioner TO CURRENT_USER WITH INHERIT FALSE, SET TRUE;
REVOKE ALL ON DATABASE weft FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER DATABASE weft OWNER TO weft_provisioner;
SET ROLE weft_provisioner;
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
GRANT CONNECT ON DATABASE weft TO weft_runtime, weft_ingest, weft_catalog_reader, weft_catalog_editor, weft_project_creator, weft_report_reader, weft_deriver, weft_maintenance, weft_identity_reader, weft_payload_permission_reader, weft_session_reader, weft_session_writer, weft_chat_reader, weft_chat_writer, weft_chat_lifecycle, weft_preview_reader, weft_preview_writer, weft_case_reader, weft_case_writer, weft_run_reader, weft_run_writer, weft_build_reader, weft_build_writer, weft_build_run_reader, weft_build_run_writer, weft_build_dispatcher, weft_build_metadata_reader;
GRANT CONNECT ON DATABASE weft TO weft_qualification_reader,weft_qualification_writer,weft_comparison_settler;
GRANT CONNECT ON DATABASE weft TO weft_release_reader,weft_release_writer;
GRANT CONNECT ON DATABASE weft TO weft_invocation_grant_writer,weft_invocation_writer;
GRANT CONNECT ON DATABASE weft TO weft_api_audit_writer;
GRANT CONNECT ON DATABASE weft TO weft_administration;
GRANT weft_migrator TO weft_provisioner WITH INHERIT FALSE, SET TRUE;
CREATE SCHEMA weft_search_extensions AUTHORIZATION weft_migrator;
-- Install through the database owner, then let the DBA remove the trusted
-- extension's bootstrap-owned PUBLIC grants before admitting the migrations.
GRANT weft_migrator TO SESSION_USER WITH INHERIT FALSE, SET TRUE;
ALTER DATABASE weft OWNER TO weft_migrator;
REVOKE weft_migrator FROM weft_provisioner;
SET ROLE weft_migrator;
CREATE EXTENSION pg_trgm WITH SCHEMA weft_search_extensions;
REVOKE ALL ON SCHEMA weft_search_extensions FROM PUBLIC;
RESET ROLE;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA weft_search_extensions FROM PUBLIC;
SET ROLE weft_provisioner;
REVOKE weft_migrator FROM SESSION_USER;
RESET ROLE;
DROP ROLE weft_provisioner;
-- Chat routines pin these scope settings in function SET clauses (migration 0011).
GRANT SET ON PARAMETER weft.organization_id, weft.chat_principal_id TO weft_migrator;
COMMIT;
SQL
echo 'The weft database, restricted logins and migrator ownership are provisioned.'
