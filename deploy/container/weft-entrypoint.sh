#!/bin/bash
# Weft container entrypoint: private delivery of secret values, then exactly one explicit command.
# Never enable shell tracing here; delivered values must not reach arguments, output or logs.
set -euo pipefail
umask 077

readonly secrets=/run/weft/secrets
readonly dotnet=/usr/bin/dotnet
readonly tools=/app/tools

refuse() {
  printf '%s\n' "$1" >&2
  exit 2
}

require_secret_directory() {
  [[ -d "$secrets" && ! -L "$secrets" && -O "$secrets" ]] || refuse 'WeftSecretDirectoryUnavailable'
}

# Replace one private file; the server admits only regular single-link 0600 files it owns.
write_private() {
  local path="$secrets/$1"
  rm -f -- "$path"
  printf '%s' "$2" >"$path"
}

deliver_text() {
  local source=$1 file=$2 setting=$3
  [[ -v $source ]] || return 0
  [[ ! -v $setting ]] || refuse 'WeftDeliveryConflict'
  require_secret_directory
  write_private "$file" "${!source}"
  unset "$source"
  export "$setting=$secrets/$file"
}

# Splits one PEM bundle (for example a Key Vault certificate secret) into certificate and key files.
split_pem() {
  local value=$1 name=$2 key certificates
  value=${value//$'\r'/}
  key=$(printf '%s\n' "$value" | sed -n '/^-----BEGIN [A-Z ]*PRIVATE KEY-----$/,/^-----END [A-Z ]*PRIVATE KEY-----$/p')
  certificates=$(printf '%s\n' "$value" | sed -n '/^-----BEGIN CERTIFICATE-----$/,/^-----END CERTIFICATE-----$/p')
  [[ $(printf '%s\n' "$key" | grep -c -- '-----BEGIN [A-Z ]*PRIVATE KEY-----') == 1 && -n "$certificates" ]] ||
    refuse 'WeftPemBundleInvalid'
  write_private "$name.key" "$key"$'\n'
  write_private "$name.crt" "$certificates"$'\n'
}

deliver_pem() {
  local source=$1 name=$2 certificate_setting=$3 key_setting=$4
  [[ -v $source ]] || return 0
  [[ ! -v $certificate_setting && ! -v $key_setting ]] || refuse 'WeftDeliveryConflict'
  require_secret_directory
  split_pem "${!source}" "$name"
  unset "$source"
  export "$certificate_setting=$secrets/$name.crt" "$key_setting=$secrets/$name.key"
}

# Up to eight retained decryption certificates for restart-based session key rotation.
deliver_decrypt_overlap() {
  local index source pairs=()
  for index in 1 2 3 4 5 6 7 8; do
    source=WEFT_DELIVER_SESSION_DECRYPT_PEM_$index
    [[ -v $source ]] || continue
    [[ ! -v WEFT_SESSION_DECRYPT_CERTIFICATES_FILE ]] || refuse 'WeftDeliveryConflict'
    require_secret_directory
    split_pem "${!source}" "session-decrypt-$index"
    unset "$source"
    pairs+=("{\"certificateFile\":\"$secrets/session-decrypt-$index.crt\",\"privateKeyFile\":\"$secrets/session-decrypt-$index.key\"}")
  done
  ((${#pairs[@]} > 0)) || return 0
  local IFS=,
  write_private session-decrypt-certificates.json "[${pairs[*]}]"
  export "WEFT_SESSION_DECRYPT_CERTIFICATES_FILE=$secrets/session-decrypt-certificates.json"
}

deliver_text WEFT_DELIVER_SESSION_READER_CONNECTION_STRING session-reader.connection WEFT_SESSION_READER_CONNECTION_STRING_FILE
deliver_text WEFT_DELIVER_SESSION_WRITER_CONNECTION_STRING session-writer.connection WEFT_SESSION_WRITER_CONNECTION_STRING_FILE
deliver_text WEFT_DELIVER_IDENTITY_READER_CONNECTION_STRING identity-reader.connection WEFT_IDENTITY_READER_CONNECTION_STRING_FILE
deliver_pem WEFT_DELIVER_SESSION_KEY_PEM session-key WEFT_SESSION_KEY_CERTIFICATE_FILE WEFT_SESSION_KEY_PRIVATE_KEY_FILE
deliver_pem WEFT_DELIVER_ENTRA_CLIENT_PEM entra-client WEFT_ENTRA_CLIENT_CERTIFICATE_FILE WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE
deliver_pem WEFT_DELIVER_INSPECTION_CLIENT_PEM inspection-client WEFT_INSPECTION_CLIENT_CERTIFICATE_FILE WEFT_INSPECTION_CLIENT_PRIVATE_KEY_FILE
deliver_pem WEFT_DELIVER_INSPECTION_WORKER_PEM inspection-worker WEFT_INSPECTION_SERVER_CERTIFICATE_FILE WEFT_INSPECTION_SERVER_PRIVATE_KEY_FILE
deliver_pem WEFT_DELIVER_FERRULE_SERVICE_CLIENT_PEM ferrule-service-client WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE WEFT_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE
deliver_decrypt_overlap
# Generic private files for settings that name their own path, for example
# WEFT_DELIVER_FILE_CHAT_PROFILES becomes /run/weft/secrets/chat-profiles.
for name in $(compgen -e); do
  [[ $name == WEFT_DELIVER_FILE_* ]] || continue
  file=${name#WEFT_DELIVER_FILE_}
  [[ $file =~ ^[A-Z0-9]+(_[A-Z0-9]+)*$ ]] || refuse 'WeftDeliveryUnknown'
  file=${file,,}
  file=${file//_/-}
  require_secret_directory
  write_private "$file" "${!name}"
  unset "$name"
done
owner_input=
if [[ -v WEFT_DELIVER_OWNER_INPUT ]]; then
  require_secret_directory
  write_private owner-input.stdin "$WEFT_DELIVER_OWNER_INPUT"
  unset WEFT_DELIVER_OWNER_INPUT
  owner_input=$secrets/owner-input.stdin
fi
for name in $(compgen -e); do
  [[ $name != WEFT_DELIVER_* ]] || refuse 'WeftDeliveryUnknown'
done

command=${1:-server}
(($# == 0)) || shift
case $command in
  server)
    (($# == 0)) || refuse 'WeftServerTakesNoArguments'
    # The base image's default listener port is not Weft host configuration; the session
    # profile refuses ambient listener settings.
    unset ASPNETCORE_HTTP_PORTS
    exec "$dotnet" /app/server/Weft.Server.dll
    ;;
  worker)
    (($# == 0)) || refuse 'WeftWorkerTakesNoArguments'
    # Only the worker image contains Weft.Worker. Its inspection listener is explicit configuration.
    [[ -f /app/worker/Weft.Worker.dll ]] || refuse 'WeftWorkerUnavailable'
    unset ASPNETCORE_HTTP_PORTS
    exec "$dotnet" /app/worker/Weft.Worker.dll
    ;;
  migrate)
    (($# == 0)) || refuse 'WeftMigrateTakesNoArguments'
    exec "$dotnet" "$tools/database/Weft.DatabaseTool.dll" migrate
    ;;
  database)
    if [[ -n $owner_input ]]; then
      exec "$dotnet" "$tools/database/Weft.DatabaseTool.dll" "$@" <"$owner_input"
    fi
    exec "$dotnet" "$tools/database/Weft.DatabaseTool.dll" "$@"
    ;;
  storage) exec "$dotnet" "$tools/storage/Weft.StorageTool.dll" "$@" ;;
  maintenance) exec "$dotnet" "$tools/maintenance/Weft.MaintenanceTool.dll" "$@" ;;
  session-keys) exec "$dotnet" "$tools/session-keys/Weft.SessionKeyTool.dll" "$@" ;;
  initialize-storage)
    # One-shot first-run step. An existing root is left for the server's own admission checks.
    (($# == 1)) || refuse 'Usage: initialize-storage ROOT'
    if [[ -e $1 || -L $1 ]]; then
      echo 'ExistingStorageRootRetained'
      exit 0
    fi
    exec "$dotnet" "$tools/storage/Weft.StorageTool.dll" initialize-v2 "$1" /app/server
    ;;
  bootstrap-session-keys)
    # One-shot first-run step. A nonempty ring is left for the server's own admission checks.
    (($# == 0)) || refuse 'WeftBootstrapTakesNoArguments'
    for setting in WEFT_SESSION_ORGANIZATION_ID WEFT_SESSION_APPLICATION_NAME WEFT_SESSION_KEY_DIRECTORY \
      WEFT_SESSION_KEY_CERTIFICATE_FILE WEFT_SESSION_KEY_PRIVATE_KEY_FILE; do
      [[ -n ${!setting:-} ]] || refuse 'WeftBootstrapConfigurationMissing'
    done
    ring=$WEFT_SESSION_KEY_DIRECTORY
    if [[ -d $ring && ! -L $ring ]] && [[ -n $(find "$ring" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
      echo 'ExistingKeyRingRetained'
      exit 0
    fi
    [[ -e $ring || -L $ring ]] || mkdir -m 0700 -- "$ring"
    exec "$dotnet" "$tools/session-keys/Weft.SessionKeyTool.dll" bootstrap-empty "$WEFT_SESSION_ORGANIZATION_ID" \
      "$WEFT_SESSION_APPLICATION_NAME" "$ring" "$WEFT_SESSION_KEY_CERTIFICATE_FILE" \
      "$WEFT_SESSION_KEY_PRIVATE_KEY_FILE" "${WEFT_SESSION_KEY_LIFETIME_DAYS:-90}"
    ;;
  *) refuse 'Usage: weft-entrypoint server|worker|migrate|database|storage|maintenance|session-keys|initialize-storage|bootstrap-session-keys' ;;
esac
