#!/bin/bash
# Braid container entrypoint: private delivery of secret values, then exactly one explicit command.
# Never enable shell tracing here; delivered values must not reach arguments, output or logs.
set -euo pipefail
umask 077

readonly secrets=/run/braid/secrets
readonly dotnet=/usr/bin/dotnet
readonly tools=/app/tools

refuse() {
  printf '%s\n' "$1" >&2
  exit 2
}

require_secret_directory() {
  [[ -d "$secrets" && ! -L "$secrets" && -O "$secrets" ]] || refuse 'BraidSecretDirectoryUnavailable'
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
  [[ ! -v $setting ]] || refuse 'BraidDeliveryConflict'
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
    refuse 'BraidPemBundleInvalid'
  write_private "$name.key" "$key"$'\n'
  write_private "$name.crt" "$certificates"$'\n'
}

deliver_pem() {
  local source=$1 name=$2 certificate_setting=$3 key_setting=$4
  [[ -v $source ]] || return 0
  [[ ! -v $certificate_setting && ! -v $key_setting ]] || refuse 'BraidDeliveryConflict'
  require_secret_directory
  split_pem "${!source}" "$name"
  unset "$source"
  export "$certificate_setting=$secrets/$name.crt" "$key_setting=$secrets/$name.key"
}

# Up to eight retained decryption certificates for restart-based session key rotation.
deliver_decrypt_overlap() {
  local index source pairs=()
  for index in 1 2 3 4 5 6 7 8; do
    source=BRAID_DELIVER_SESSION_DECRYPT_PEM_$index
    [[ -v $source ]] || continue
    [[ ! -v BRAID_SESSION_DECRYPT_CERTIFICATES_FILE ]] || refuse 'BraidDeliveryConflict'
    require_secret_directory
    split_pem "${!source}" "session-decrypt-$index"
    unset "$source"
    pairs+=("{\"certificateFile\":\"$secrets/session-decrypt-$index.crt\",\"privateKeyFile\":\"$secrets/session-decrypt-$index.key\"}")
  done
  ((${#pairs[@]} > 0)) || return 0
  local IFS=,
  write_private session-decrypt-certificates.json "[${pairs[*]}]"
  export "BRAID_SESSION_DECRYPT_CERTIFICATES_FILE=$secrets/session-decrypt-certificates.json"
}

deliver_text BRAID_DELIVER_SESSION_READER_CONNECTION_STRING session-reader.connection BRAID_SESSION_READER_CONNECTION_STRING_FILE
deliver_text BRAID_DELIVER_SESSION_WRITER_CONNECTION_STRING session-writer.connection BRAID_SESSION_WRITER_CONNECTION_STRING_FILE
deliver_text BRAID_DELIVER_IDENTITY_READER_CONNECTION_STRING identity-reader.connection BRAID_IDENTITY_READER_CONNECTION_STRING_FILE
deliver_pem BRAID_DELIVER_SESSION_KEY_PEM session-key BRAID_SESSION_KEY_CERTIFICATE_FILE BRAID_SESSION_KEY_PRIVATE_KEY_FILE
deliver_pem BRAID_DELIVER_ENTRA_CLIENT_PEM entra-client BRAID_ENTRA_CLIENT_CERTIFICATE_FILE BRAID_ENTRA_CLIENT_PRIVATE_KEY_FILE
deliver_pem BRAID_DELIVER_INSPECTION_CLIENT_PEM inspection-client BRAID_INSPECTION_CLIENT_CERTIFICATE_FILE BRAID_INSPECTION_CLIENT_PRIVATE_KEY_FILE
deliver_pem BRAID_DELIVER_INSPECTION_WORKER_PEM inspection-worker BRAID_INSPECTION_SERVER_CERTIFICATE_FILE BRAID_INSPECTION_SERVER_PRIVATE_KEY_FILE
deliver_pem BRAID_DELIVER_FERRULE_SERVICE_CLIENT_PEM ferrule-service-client BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE BRAID_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE
deliver_decrypt_overlap
# Generic private files for settings that name their own path, for example
# BRAID_DELIVER_FILE_CHAT_PROFILES becomes /run/braid/secrets/chat-profiles.
for name in $(compgen -e); do
  [[ $name == BRAID_DELIVER_FILE_* ]] || continue
  file=${name#BRAID_DELIVER_FILE_}
  [[ $file =~ ^[A-Z0-9]+(_[A-Z0-9]+)*$ ]] || refuse 'BraidDeliveryUnknown'
  file=${file,,}
  file=${file//_/-}
  require_secret_directory
  write_private "$file" "${!name}"
  unset "$name"
done
owner_input=
if [[ -v BRAID_DELIVER_OWNER_INPUT ]]; then
  require_secret_directory
  write_private owner-input.stdin "$BRAID_DELIVER_OWNER_INPUT"
  unset BRAID_DELIVER_OWNER_INPUT
  owner_input=$secrets/owner-input.stdin
fi
for name in $(compgen -e); do
  [[ $name != BRAID_DELIVER_* ]] || refuse 'BraidDeliveryUnknown'
done

command=${1:-server}
(($# == 0)) || shift
case $command in
  server)
    (($# == 0)) || refuse 'BraidServerTakesNoArguments'
    # The base image's default listener port is not Braid host configuration; the session
    # profile refuses ambient listener settings.
    unset ASPNETCORE_HTTP_PORTS
    exec "$dotnet" /app/server/Braid.Server.dll
    ;;
  worker)
    (($# == 0)) || refuse 'BraidWorkerTakesNoArguments'
    # Only the worker image contains Braid.Worker. Its inspection listener is explicit configuration.
    [[ -f /app/worker/Braid.Worker.dll ]] || refuse 'BraidWorkerUnavailable'
    unset ASPNETCORE_HTTP_PORTS
    exec "$dotnet" /app/worker/Braid.Worker.dll
    ;;
  endpoint)
    (($# == 0)) || refuse 'BraidEndpointTakesNoArguments'
    # Only the endpoint image contains the mapping endpoint. Its listener is explicit configuration.
    [[ -f /app/endpoint/braid-mapping-endpoint.dll ]] || refuse 'BraidEndpointUnavailable'
    unset ASPNETCORE_HTTP_PORTS
    exec "$dotnet" /app/endpoint/braid-mapping-endpoint.dll
    ;;
  release)
    [[ -f $tools/release/Braid.ReleaseTool.dll ]] || refuse 'BraidReleaseToolUnavailable'
    exec "$dotnet" "$tools/release/Braid.ReleaseTool.dll" "$@"
    ;;
  migrate)
    (($# == 0)) || refuse 'BraidMigrateTakesNoArguments'
    exec "$dotnet" "$tools/database/Braid.DatabaseTool.dll" migrate
    ;;
  database)
    if [[ -n $owner_input ]]; then
      exec "$dotnet" "$tools/database/Braid.DatabaseTool.dll" "$@" <"$owner_input"
    fi
    exec "$dotnet" "$tools/database/Braid.DatabaseTool.dll" "$@"
    ;;
  storage) exec "$dotnet" "$tools/storage/Braid.StorageTool.dll" "$@" ;;
  maintenance) exec "$dotnet" "$tools/maintenance/Braid.MaintenanceTool.dll" "$@" ;;
  session-keys) exec "$dotnet" "$tools/session-keys/Braid.SessionKeyTool.dll" "$@" ;;
  initialize-storage)
    # One-shot first-run step. An existing root is left for the server's own admission checks.
    (($# == 1)) || refuse 'Usage: initialize-storage ROOT'
    if [[ -e $1 || -L $1 ]]; then
      echo 'ExistingStorageRootRetained'
      exit 0
    fi
    exec "$dotnet" "$tools/storage/Braid.StorageTool.dll" initialize-v2 "$1" /app/server
    ;;
  bootstrap-session-keys)
    # One-shot first-run step. A nonempty ring is left for the server's own admission checks.
    (($# == 0)) || refuse 'BraidBootstrapTakesNoArguments'
    for setting in BRAID_SESSION_ORGANIZATION_ID BRAID_SESSION_APPLICATION_NAME BRAID_SESSION_KEY_DIRECTORY \
      BRAID_SESSION_KEY_CERTIFICATE_FILE BRAID_SESSION_KEY_PRIVATE_KEY_FILE; do
      [[ -n ${!setting:-} ]] || refuse 'BraidBootstrapConfigurationMissing'
    done
    ring=$BRAID_SESSION_KEY_DIRECTORY
    if [[ -d $ring && ! -L $ring ]] && [[ -n $(find "$ring" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
      echo 'ExistingKeyRingRetained'
      exit 0
    fi
    [[ -e $ring || -L $ring ]] || mkdir -m 0700 -- "$ring"
    exec "$dotnet" "$tools/session-keys/Braid.SessionKeyTool.dll" bootstrap-empty "$BRAID_SESSION_ORGANIZATION_ID" \
      "$BRAID_SESSION_APPLICATION_NAME" "$ring" "$BRAID_SESSION_KEY_CERTIFICATE_FILE" \
      "$BRAID_SESSION_KEY_PRIVATE_KEY_FILE" "${BRAID_SESSION_KEY_LIFETIME_DAYS:-90}"
    ;;
  *) refuse 'Usage: braid-entrypoint server|worker|endpoint|release|migrate|database|storage|maintenance|session-keys|initialize-storage|bootstrap-session-keys' ;;
esac
