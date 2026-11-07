#!/usr/bin/env bash
# Operator step between the platform and application deployments. Creates, in Key Vault:
# - one random password per restricted PostgreSQL login (kept when it already exists) and the
#   matching TLS-verified connection string;
# - the self-signed session key-protection and Entra client certificates (kept when present);
# - the self-signed inspection client and worker certificates for a separately hosted inspection
#   worker (kept when present), with each certificate's SHA-256 pin written to OUTPUT_DIRECTORY;
# - the self-signed Ferrule service, service client, worker client and mapping endpoint client
#   certificates (kept when present), with each certificate's SHA-256 pin written to OUTPUT_DIRECTORY;
# - the owner-job input placeholder, and the chat profile document and model credential placeholders
#   (kept when present; replace them before enabling chat).
# Values travel only through private temporary files; the script prints secret names, never
# values. Writes the Entra client's public certificate to OUTPUT_DIRECTORY for the app registration.
# Usage: initialize-secrets.sh VAULT_NAME DATABASE_HOST OUTPUT_DIRECTORY
set -euo pipefail
umask 077

(($# == 3)) || { echo 'Usage: initialize-secrets.sh VAULT_NAME DATABASE_HOST OUTPUT_DIRECTORY' >&2; exit 2; }
vault=$1
database_host=$2
output=$3
[[ -d $output ]] || { echo 'The output directory must exist.' >&2; exit 2; }
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

# Succeeds for an existing item and fails only when Key Vault reports it absent. Any other failure
# (authorization, network, timeout) stops the script, so an unconfirmed absence never leads to a
# write that would replace a password, placeholder or certificate.
item_exists() { # secret|certificate name
  local kind=$1 name=$2
  az keyvault "$kind" show --vault-name "$vault" --name "$name" --query id --output none >/dev/null 2>"$work/lookup" &&
    return 0
  if grep -q "${kind^}NotFound" "$work/lookup"; then return 1; fi
  echo "Could not establish whether $kind $name exists; nothing was written for it." >&2
  exit 1
}

secret_exists() {
  item_exists secret "$1"
}

set_secret() { # name file content-type
  az keyvault secret set --vault-name "$vault" --name "$1" --file "$2" --encoding utf-8 \
    --content-type "$3" --output none
  echo "Set $1"
}

declare -A logins=(
  [migrator]=weft_migrator [runtime]=weft_runtime [ingest]=weft_ingest
  [catalog]=weft_catalog_reader [metadata]=weft_catalog_editor [creator]=weft_project_creator
  [report]=weft_report_reader [derivation]=weft_deriver [maintenance]=weft_maintenance
  [identity]=weft_identity_reader [payload-permission]=weft_payload_permission_reader
  [session-reader]=weft_session_reader [session-writer]=weft_session_writer
  [chat-reader]=weft_chat_reader [chat-writer]=weft_chat_writer [chat-lifecycle]=weft_chat_lifecycle
  [preview-reader]=weft_preview_reader [preview-writer]=weft_preview_writer
  [case-reader]=weft_case_reader [case-writer]=weft_case_writer
  [run-reader]=weft_run_reader [run-writer]=weft_run_writer
  [comparison]=weft_comparison_settler
  [build-reader]=weft_build_reader [build-writer]=weft_build_writer
  [build-run-reader]=weft_build_run_reader [build-run-writer]=weft_build_run_writer [build-dispatcher]=weft_build_dispatcher
  [build-metadata]=weft_build_metadata_reader
  [qualification-reader]=weft_qualification_reader [qualification-writer]=weft_qualification_writer
  [release-reader]=weft_release_reader [release-writer]=weft_release_writer
  [invocation-grant-writer]=weft_invocation_grant_writer [invocation-writer]=weft_invocation_writer
  [api-audit-writer]=weft_api_audit_writer
)

for key in "${!logins[@]}"; do
  if ! secret_exists "pg-$key-password"; then
    # Hexadecimal passwords need no quoting inside connection strings.
    printf '%s' "$(openssl rand -hex 32)" >"$work/password"
    set_secret "pg-$key-password" "$work/password" text/plain
  fi
  az keyvault secret show --vault-name "$vault" --name "pg-$key-password" --query value --output tsv >"$work/password"
  password=$(<"$work/password")
  [[ $password =~ ^[0-9a-f]{64}$ ]] || { echo "pg-$key-password is not a generated password." >&2; exit 1; }
  printf 'Host=%s;Port=5432;Database=weft;Username=%s;Password=%s;SSL Mode=VerifyFull;GSS Encryption Mode=Disable' \
    "$database_host" "${logins[$key]}" "$password" >"$work/connection"
  unset password
  set_secret "pg-$key-connection" "$work/connection" text/plain
done

create_certificate() { # name subject
  if item_exists certificate "$1"; then
    echo "Kept $1"
    return
  fi
  cat >"$work/policy.json" <<POLICY
{
  "issuerParameters": { "name": "Self" },
  "keyProperties": { "exportable": true, "keyType": "RSA", "keySize": 3072, "reuseKey": false },
  "secretProperties": { "contentType": "application/x-pem-file" },
  "x509CertificateProperties": {
    "subject": "$2",
    "keyUsage": ["digitalSignature", "keyEncipherment"],
    "validityInMonths": 24
  },
  "lifetimeActions": [
    { "trigger": { "daysBeforeExpiry": 45 }, "action": { "actionType": "EmailContacts" } }
  ]
}
POLICY
  az keyvault certificate create --vault-name "$vault" --name "$1" --policy "@$work/policy.json" --output none
  echo "Created $1"
}

create_certificate weft-session-key 'CN=Weft session key protection'
create_certificate weft-entra-client 'CN=Weft Entra client'
rm -f -- "$output/weft-entra-client.pem"
az keyvault certificate download --vault-name "$vault" --name weft-entra-client --encoding PEM \
  --file "$output/weft-entra-client.pem"
echo "Wrote $output/weft-entra-client.pem (public certificate for the app registration)"

# The Session host and the inspection worker each admit only the other's pinned certificate.
create_certificate weft-inspection-client 'CN=Weft inspection client'
create_certificate weft-inspection-worker 'CN=Weft inspection worker'
# The Session host and the Ferrule service likewise pin each other; the worker app, the mapping
# endpoints and their release job each use their own client certificate.
create_certificate weft-ferrule-service-client 'CN=Weft Ferrule service client'
create_certificate weft-ferrule-service 'CN=Weft Ferrule service'
create_certificate weft-ferrule-worker-client 'CN=Weft worker Ferrule client'
create_certificate weft-ferrule-endpoint-client 'CN=Weft mapping endpoint Ferrule client'
for certificate in weft-inspection-client weft-inspection-worker weft-ferrule-service-client weft-ferrule-service \
  weft-ferrule-worker-client weft-ferrule-endpoint-client; do
  rm -f -- "$work/$certificate.der"
  az keyvault certificate download --vault-name "$vault" --name "$certificate" --encoding DER --file "$work/$certificate.der"
  sha256sum "$work/$certificate.der" | cut -d' ' -f1 >"$output/$certificate.sha256"
  echo "Wrote $output/$certificate.sha256 (certificate pin)"
done

if ! secret_exists weft-owner-input; then
  printf '{}' >"$work/owner-input"
  set_secret weft-owner-input "$work/owner-input" application/json
fi

# Chat reads both as private files. The placeholder document refuses chat startup until it is replaced;
# the credential placeholder stays unused when the profile names a managed-identity token instead.
if ! secret_exists weft-chat-profiles; then
  printf '{}' >"$work/chat-profiles"
  set_secret weft-chat-profiles "$work/chat-profiles" application/json
fi
if ! secret_exists weft-model-provider-credential; then
  printf 'unset' >"$work/model-provider-credential"
  set_secret weft-model-provider-credential "$work/model-provider-credential" text/plain
fi
