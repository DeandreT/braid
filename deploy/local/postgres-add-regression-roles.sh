#!/usr/bin/env bash
set -euo pipefail

# Explicit administrator step for an existing local volume before stop/migrate/start.
: "${POSTGRES_USER:?Set the database administrator user}"
: "${POSTGRES_DB:?Set the local database name}"
[[ "$POSTGRES_DB" == weft ]] || { echo 'This script targets only the local weft database.' >&2; exit 1; }
regression_passwords=()
for role in CASE_READER CASE_WRITER RUN_READER RUN_WRITER; do
  variable=WEFT_LOCAL_${role}_PASSWORD
  [[ -n ${!variable:-} ]] || { echo "Set $variable." >&2; exit 1; }
  for previous in "${regression_passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Regression profile passwords must be independent.' >&2; exit 1; }
  done
  regression_passwords+=("${!variable}")
done
unset regression_passwords previous

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv case_reader_password WEFT_LOCAL_CASE_READER_PASSWORD
\getenv case_writer_password WEFT_LOCAL_CASE_WRITER_PASSWORD
\getenv run_reader_password WEFT_LOCAL_RUN_READER_PASSWORD
\getenv run_writer_password WEFT_LOCAL_RUN_WRITER_PASSWORD
BEGIN;
CREATE ROLE weft_case_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_reader_password';
CREATE ROLE weft_case_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'case_writer_password';
CREATE ROLE weft_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_reader_password';
CREATE ROLE weft_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'run_writer_password';
GRANT CONNECT ON DATABASE weft TO weft_case_reader,weft_case_writer,weft_run_reader,weft_run_writer;
COMMIT;
SQL
