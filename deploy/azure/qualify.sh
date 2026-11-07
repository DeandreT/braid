#!/usr/bin/env bash
# Live qualification of the hosted Session stack (ROADMAP P8) and the mapping endpoint (P12) in a disposable
# Azure resource group that this harness creates, tags with its run ID and owns. It runs the procedure of
# docs/azure-deployment.md from one clean commit, checks the platform, the endpoint and restart and scale
# recovery, asks the operator to confirm the signed-in browser flows, and records every automated check and
# operator confirmation in a private receipt. A failed run keeps the group for investigation.
# See docs/azure-deployment.md#live-qualification.
#
# Run:      qualify.sh --subscription ID --resource-group NAME --location REGION --parameters FILE.bicepparam
#             --entra-tenant-id ID --entra-client-id ID --chat-profiles FILE
#             --endpoint-audience ID --caller-client-id ID (--caller-secret-file FILE | --caller-certificate-file FILE)
#             [--model-credential-file FILE] [--state-dir DIR] [--yes] [--teardown-at-end] --confirm-provisioning
# Teardown: qualify.sh --teardown RUN_ID --subscription ID --resource-group NAME [--state-dir DIR] [--yes]
set -euo pipefail
umask 077

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
# shellcheck source-path=SCRIPTDIR source=qualification/lib.sh
source "$repository/deploy/azure/qualification/lib.sh"

usage() {
  sed -n '9,14s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
}

# A refused run changes nothing. Messages never repeat an argument's value.
refuse() {
  printf 'Refused: %s\n' "$1" >&2
  exit 2
}

subscription='' group='' location='' parameters='' tenant='' client='' chat_profiles=''
model_credential_file='' audience='' caller_client='' caller_secret_file='' caller_certificate_file='' teardown_run=''
confirm_provisioning=false assume_yes=false teardown_at_end=false
state_root=${XDG_STATE_HOME:-$HOME/.local/state}/weft-qualification
while (($#)); do
  case $1 in
    --subscription | --resource-group | --location | --parameters | --entra-tenant-id | --entra-client-id | \
      --chat-profiles | --model-credential-file | --endpoint-audience | --caller-client-id | \
      --caller-secret-file | --caller-certificate-file | --state-dir | --teardown)
      (($# >= 2)) || refuse "$1 needs a value"
      case $1 in
        --subscription) subscription=$2 ;;
        --resource-group) group=$2 ;;
        --location) location=$2 ;;
        --parameters) parameters=$2 ;;
        --entra-tenant-id) tenant=$2 ;;
        --entra-client-id) client=$2 ;;
        --chat-profiles) chat_profiles=$2 ;;
        --model-credential-file) model_credential_file=$2 ;;
        --endpoint-audience) audience=$2 ;;
        --caller-client-id) caller_client=$2 ;;
        --caller-secret-file) caller_secret_file=$2 ;;
        --caller-certificate-file) caller_certificate_file=$2 ;;
        --state-dir) state_root=$2 ;;
        --teardown) teardown_run=$2 ;;
      esac
      shift 2
      ;;
    --confirm-provisioning) confirm_provisioning=true && shift ;;
    --yes) assume_yes=true && shift ;;
    --teardown-at-end) teardown_at_end=true && shift ;;
    -h | --help) usage && exit 0 ;;
    --*secret* | --*password* | --*token* | --*credential* | --*assertion* | --*key*)
      refuse "credentials are read only from private files (--caller-secret-file, --caller-certificate-file, --model-credential-file); ${1%%=*} is not accepted"
      ;;
    --*) refuse "unknown option ${1%%=*}" ;;
    *) refuse 'unexpected positional argument' ;;
  esac
done

uuid_pattern='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
run_pattern='^wq-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$'
tag_name=weft-qualification-run

required() { # value option
  [[ -n $1 ]] || refuse "$2 is required"
}

private_file() { # path option
  [[ -f $1 && ! -L $1 ]] || refuse "$2 must name a regular file"
  [[ $(stat -c '%u %a' -- "$1") =~ ^$(id -u)\ [0-7]00$ ]] ||
    refuse "$2 must be owned by you and readable by no one else (chmod 600)"
}

guard_subscription() {
  required "$subscription" --subscription
  [[ $subscription =~ $uuid_pattern ]] || refuse '--subscription must be a canonical lowercase subscription ID'
  required "$group" --resource-group
  [[ $group =~ ^[-A-Za-z0-9._()]{1,90}$ && $group != *. ]] || refuse '--resource-group is not a valid resource group name'
}

guard_account() {
  local active
  active=$(az account show --query id --output tsv) || refuse 'the Azure CLI has no signed-in account'
  [[ $active == "$subscription" ]] ||
    refuse 'the Azure CLI subscription (az account show) is not the approved --subscription'
}

state_root=$(realpath -m -- "$state_root")
[[ $state_root != "$repository" && $state_root != "$repository"/* ]] || refuse '--state-dir must be outside the repository'

group_absent() {
  [[ $(azs group exists --name "$group") == false ]]
}

# Deletes the run's group after checking its run tag, and waits until Azure no longer reports it.
delete_run_group() { # run-id
  local tag
  tag=$(azs group show --name "$group" --query "tags.\"$tag_name\"" --output tsv) || return 1
  [[ $tag == "$1" ]] || { printf 'The group %s does not carry the run tag %s; nothing was deleted.\n' "$group" "$1" >&2; return 2; }
  azs group delete --name "$group" --yes || return 1
  wait_for 1800 30 group_absent
}

# ---- Teardown ----

if [[ -n $teardown_run ]]; then
  [[ $teardown_run =~ $run_pattern ]] || refuse '--teardown needs a run ID such as wq-20261023-120000-0a1b2c'
  guard_subscription
  for tool in az jq; do
    command -v "$tool" >/dev/null || refuse "$tool is required"
  done
  guard_account
  [[ $(azs group exists --name "$group") == true ]] || refuse "the resource group $group does not exist"
  [[ $(azs group show --name "$group" --query "tags.\"$tag_name\"" --output tsv) == "$teardown_run" ]] ||
    refuse "the resource group $group does not carry the run tag $teardown_run; only a group this harness created for that run is deleted"
  run_id=$teardown_run
  mkdir -p -- "$state_root/$run_id"
  chmod 700 -- "$state_root" "$state_root/$run_id"
  receipt=$state_root/$run_id/receipt.jsonl
  touch -- "$receipt" && chmod 600 -- "$receipt"
  if [[ $assume_yes != true ]]; then
    printf 'Type the resource group name (%s) to delete it and everything in it: ' "$group"
    read -r answer || answer=''
    [[ $answer == "$group" ]] || refuse 'teardown was not confirmed; nothing was deleted'
  fi
  record teardown "$group" started "$(evidence subscription "$subscription" group "$group")"
  if ! delete_run_group "$run_id"; then
    record teardown "$group" fail "$(evidence subscription "$subscription" group "$group")"
    printf 'FAILED: the resource group %s was not confirmed deleted\n' "$group" >&2
    exit 1
  fi
  record teardown "$group" pass "$(evidence subscription "$subscription" group "$group")"
  printf 'Deleted %s. Its Key Vault stays soft-deleted, with purge protection, until its retention period ends.\n' "$group"
  printf 'Receipt: %s\n' "$receipt"
  exit 0
fi

# ---- Guards: nothing changes until every one has passed ----

guard_subscription
required "$location" --location
[[ $location =~ ^[a-z0-9]+$ ]] || refuse '--location must be an Azure region name such as eastus2'
required "$parameters" --parameters
required "$tenant" --entra-tenant-id
required "$client" --entra-client-id
required "$chat_profiles" --chat-profiles
required "$audience" --endpoint-audience
required "$caller_client" --caller-client-id
[[ -n $caller_secret_file || -n $caller_certificate_file ]] || refuse '--caller-secret-file or --caller-certificate-file is required'
[[ -z $caller_secret_file || -z $caller_certificate_file ]] || refuse 'use only one of --caller-secret-file and --caller-certificate-file'
$confirm_provisioning || refuse '--confirm-provisioning is required: the run creates billable Azure resources'
for option in tenant:--entra-tenant-id client:--entra-client-id audience:--endpoint-audience caller_client:--caller-client-id; do
  name=${option%%:*}
  [[ ${!name} =~ $uuid_pattern ]] || refuse "${option#*:} must be a canonical lowercase ID"
done
if [[ -n $caller_secret_file ]]; then
  private_file "$caller_secret_file" --caller-secret-file
else
  private_file "$caller_certificate_file" --caller-certificate-file
fi
[[ -z $model_credential_file ]] || private_file "$model_credential_file" --model-credential-file
[[ -f $chat_profiles ]] || refuse '--chat-profiles must name the chat profile document'
for tool in az jq curl openssl git docker sha256sum base64 realpath; do
  command -v "$tool" >/dev/null || refuse "$tool is required"
done
jq -e 'type == "object"' "$chat_profiles" >/dev/null 2>&1 || refuse '--chat-profiles must be a JSON profile document'
[[ $parameters == *.bicepparam && -f $parameters ]] || refuse '--parameters must name a .bicepparam file'
using=$(sed -nE "s/^using '([^']+)'.*/\1/p" "$parameters" | head -n 1)
[[ -n $using && $(cd -- "$(dirname -- "$parameters")" && realpath -m -- "$using") == "$repository/deploy/azure/main.bicep" ]] ||
  refuse "--parameters must use this repository's deploy/azure/main.bicep"
[[ -z $(git -C "$repository" status --porcelain) ]] ||
  refuse 'the repository has uncommitted or untracked changes; the run deploys one clean commit'
commit=$(git -C "$repository" rev-parse HEAD)
guard_account
# The first administrator is the Azure CLI's signed-in user, bound by tenant and object ID as sign-in resolves them.
[[ $(az account show --query tenantId --output tsv) == "$tenant" ]] ||
  refuse 'the Azure CLI account (az account show) is not in the --entra-tenant-id tenant'
owner_object=$(az ad signed-in-user show --query id --output tsv) ||
  refuse 'the Azure CLI must be signed in as the user who becomes the first administrator (az ad signed-in-user show)'
[[ $owner_object =~ $uuid_pattern ]] || refuse 'az ad signed-in-user show did not return a canonical object ID'
group_absent || refuse "the resource group $group already exists; the harness creates and owns a new group"

# ---- The run ----

run_id=wq-$(date -u +%Y%m%d-%H%M%S)-$(openssl rand -hex 3)
state=$state_root/$run_id
mkdir -p -- "$state"
chmod 700 -- "$state_root" "$state"
receipt=$state/receipt.jsonl
log=$state/run.log
touch -- "$receipt" "$log"
chmod 600 -- "$receipt" "$log"
work=$state/work
mkdir -m 700 -- "$work" "$work/calls"
arm=/subscriptions/$subscription
arm="https://management.azure.com$arm"
current_step=group failed_check='' group_created=false checks_passed=0 guided_confirmed=0 built_images=()

finish() {
  local status=$?
  set +e
  # An interrupted run still records its result, even if the terminal has gone.
  trap '' PIPE
  if ((status == 0)); then
    record run "$run_id" pass "$(evidence checks "$checks_passed" confirmations "$guided_confirmed" commit "$commit")"
  else
    record run "$run_id" fail "$(evidence step "$current_step" check "$failed_check" commit "$commit")"
  fi
  # Console lines and query rows are the only kept working files; they hold no secret.
  mkdir -p -- "$state/evidence"
  find "$work" -maxdepth 2 -type f \( -name '*.lines' -o -name '*.rows' -o -path '*/calls/*.json' \) \
    -exec cp -t "$state/evidence" -- {} + 2>/dev/null
  rm -rf -- "$work"
  ((${#built_images[@]} == 0)) || docker image rm "${built_images[@]}" >/dev/null 2>&1
  if $group_created; then
    if $teardown_at_end; then
      printf '\nTearing down %s as requested.\n' "$group"
      if delete_run_group "$run_id"; then
        record teardown "$group" pass "$(evidence group "$group")"
        printf 'Deleted %s.\n' "$group"
      else
        record teardown "$group" fail "$(evidence group "$group")"
        printf 'FAILED: %s was not confirmed deleted; tear it down with --teardown %s.\n' "$group" "$run_id" >&2
        status=1
      fi
    else
      printf '\nThe resource group %s (run tag %s) is kept%s. Tear it down with:\n' "$group" "$run_id" \
        "$( ((status == 0)) || printf ' for investigation')"
      printf '  deploy/azure/qualify.sh --teardown %s --subscription %s --resource-group %s\n' "$run_id" "$subscription" "$group"
    fi
  fi
  printf 'Receipt: %s\n' "$receipt"
  exec 1>&3 2>&4
  wait "$tee_pid" 2>/dev/null
  exit "$status"
}
exec 3>&1 4>&2
exec > >(trap '' INT TERM && exec tee -a -- "$log") 2>&1
tee_pid=$!
trap finish EXIT
trap 'exit 130' INT TERM

organization=$(uuid)
tags_json=$(jq -cn --arg tag "$tag_name" --arg run "$run_id" '{($tag): $run}')
openssl rand -hex 32 | tr -d '\n' >"$work/postgres-admin-password"
[[ -z $caller_secret_file ]] || printf '%s' "$(<"$caller_secret_file")" >"$work/caller.secret"
record fact inputs recorded "$(evidence subscription "$subscription" group "$group" location "$location" commit "$commit" \
  tenant "$tenant" sessionClient "$client" endpointAudience "$audience" callerClient "$caller_client" \
  callerCredential "$([[ -n $caller_secret_file ]] && echo secret || echo certificate)" organization "$organization")"
printf 'Run %s: commit %s, subscription %s, resource group %s in %s\n' "$run_id" "$commit" "$subscription" "$group" "$location"

base_parameters=(--parameters "$parameters" --parameters "location=$location" "entraTenantId=$tenant"
  "entraClientId=$client" "organizationId=$organization" "tags=$tags_json")
# The platform stage switches every application feature off: their requirements name pins that exist only later.
platform_switches=(deployApplication=false nativeWorkEnabled=false chatEnabled=false ferruleRunner=worker 'mappingEndpoints=[]')

step group 'Resource group and what-if'
# What-if needs the group, so it is created empty first; a declined confirmation deletes it again.
azs group create --name "$group" --location "$location" --tags "$tag_name=$run_id" "weft-qualification-commit=$commit" --output none
group_created=true
record fact group created "$(evidence group "$group" tag "$run_id")"
WEFT_POSTGRES_ADMIN_PASSWORD=$(<"$work/postgres-admin-password") \
  azg deployment group what-if --name weft-qualify-platform "${base_parameters[@]}" "${platform_switches[@]}"
printf '\nThe run then deploys the application with native work, chat and the Ferrule service, builds the images,\n'
printf 'provisions the database, and deploys one mapping endpoint and a qualification caller job in %s.\n' "$group"
if $assume_yes; then
  record guided provisioning confirmed "$(evidence method flag)"
else
  printf 'Type yes to create these resources: '
  read -r answer || answer=''
  if [[ $answer != yes ]]; then
    record guided provisioning declined "$(evidence method operator)"
    teardown_at_end=true
    printf 'Not confirmed; the empty group is deleted again.\n' >&2
    exit 2
  fi
  record guided provisioning confirmed "$(evidence method operator)"
fi

step platform 'Platform deployment'
deploy weft-qualify-platform "${base_parameters[@]}" "${platform_switches[@]}"
save_outputs weft-qualify-platform
registry=$(deployment_output weft-qualify-platform registryName)
login_server=$(deployment_output weft-qualify-platform registryLoginServer)
vault=$(deployment_output weft-qualify-platform keyVaultName)
database_host=$(deployment_output weft-qualify-platform databaseHost)
prefix=$(deployment_output weft-qualify-platform applicationName)
prefix=${prefix%-app}
workspace=$(azg monitor log-analytics workspace show --workspace-name "$prefix-logs" --query customerId --output tsv)
pass platform-deployed "$(evidence registry "$registry" vault "$vault" databaseHost "$database_host" prefix "$prefix")"

step secrets 'Key Vault secrets and certificates'
certificates=$state/certificates
mkdir -p -- "$certificates"
# The operator's Key Vault roles can take minutes to apply after the platform deployment; the script is rerunnable.
for attempt in 1 2 3 4 5 6; do
  if "$repository/deploy/azure/initialize-secrets.sh" "$vault" "$database_host" "$certificates"; then break; fi
  ((attempt < 6)) || fail secrets-initialized 'initialize-secrets.sh did not complete'
  sleep 60
done
declare -A pins
for certificate in weft-ferrule-service weft-ferrule-service-client weft-ferrule-worker-client weft-ferrule-endpoint-client; do
  pins[$certificate]=$(<"$certificates/$certificate.sha256")
  [[ ${pins[$certificate]} =~ ^[0-9a-f]{64}$ ]] || fail secrets-initialized "the $certificate pin is malformed"
done
az keyvault secret set --vault-name "$vault" --name weft-chat-profiles --file "$chat_profiles" --encoding utf-8 \
  --content-type application/json --output none
if [[ -n $model_credential_file ]]; then
  az keyvault secret set --vault-name "$vault" --name weft-model-provider-credential --file "$model_credential_file" \
    --encoding utf-8 --content-type text/plain --output none
fi
entra_der() {
  openssl x509 -in "$certificates/weft-entra-client.pem" -outform DER
}
entra_thumbprint=$(entra_der | openssl dgst -sha1 -r | cut -d' ' -f1)
entra_thumbprint_b64=$(entra_der | openssl dgst -sha1 -binary | base64 -w0)
pass secrets-initialized "$(evidence ferruleService "${pins[weft-ferrule-service]}" sessionClient "${pins[weft-ferrule-service-client]}" \
  workerClient "${pins[weft-ferrule-worker-client]}" endpointClient "${pins[weft-ferrule-endpoint-client]}" \
  entraClientThumbprint "$entra_thumbprint" chatProfiles replaced modelCredential "$([[ -n $model_credential_file ]] && echo replaced || echo placeholder)")"

step images "Images from commit $commit"
source_tree=$work/source
mkdir -- "$source_tree"
git -C "$repository" archive --format=tar "$commit" | tar -x -C "$source_tree"
tag=${commit:0:12}
for image in weft:weft edge:weft-edge weft-worker:weft-worker weft-mapping-endpoint:weft-mapping-endpoint; do
  target=${image%%:*} repository_name=${image#*:}
  azs acr build --registry "$registry" --file deploy/container/Dockerfile --target "$target" --image "$repository_name:$tag" \
    "$source_tree" >"$work/acr-$repository_name.log" 2>&1 ||
    { tail -n 30 "$work/acr-$repository_name.log" >&2; fail images-built "the registry build of $target failed"; }
  digest=$(az acr repository show --name "$registry" --image "$repository_name:$tag" --query digest --output tsv)
  [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]] || fail images-built "no digest for $repository_name:$tag"
  record fact image recorded "$(evidence repository "$repository_name" tag "$tag" digest "$digest" commit "$commit")"
done
# The Ferrule images need BuildKit, so they are built here from the same tree and the service image is pushed.
docker build --quiet --tag "weft-ferrule-native:$run_id" "$source_tree/engines/ferrule-host" >/dev/null
built_images+=("weft-ferrule-native:$run_id")
container=$(docker create "weft-ferrule-native:$run_id")
docker cp "$container:/ferrule-host" "$work/ferrule-host" >/dev/null
docker rm "$container" >/dev/null
native_digest=$(sha256sum "$work/ferrule-host" | cut -d' ' -f1)
rm -f -- "$work/ferrule-host"
docker build --quiet --tag "weft-ferrule-service:$run_id" --file "$source_tree/engines/ferrule-service/Dockerfile" \
  --build-arg "WEFT_NATIVE_IMAGE=weft-ferrule-native:$run_id" --build-arg "WEFT_NATIVE_BINARY_SHA256=$native_digest" \
  "$source_tree" >/dev/null
built_images+=("weft-ferrule-service:$run_id")
[[ $(docker image inspect --format '{{index .Config.Labels "io.weft.ferrule.worker-sha256"}}' "weft-ferrule-service:$run_id") == "$native_digest" ]] ||
  fail ferrule-identity 'the service image label differs from its native worker'
service_identity=sha256:$native_digest
# The trusted local image is measured with no network, mounts or capabilities.
measure() { # command output
  docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp:size=256m,mode=1777 --memory 3g --pids-limit 256 "weft-ferrule-service:$run_id" "$1" >"$work/$2"
}
measure identity compiler-identity.json
toolchain=$(jq -er --arg native "$service_identity" '
  select(.schema == "weft.compiler-image-identity.v1" and .native == $native and (.toolchain | test("^sha256:[0-9a-f]{64}$"))
    and .build != .native) | .toolchain' "$work/compiler-identity.json") || fail ferrule-identity 'the compiler identity'
compiler=sha256:$(printf 'weft.compiler-identity.v1\nnative=%s\ntoolchain=%s\n' "$service_identity" "$toolchain" | sha256sum | cut -d' ' -f1)
[[ $(jq -r .build "$work/compiler-identity.json") == "$compiler" ]] || fail ferrule-identity 'the combined compiler identity'
measure runtime-identity runtime-identity.json
runtime_identity=$(jq -er --arg native "$service_identity" '
  select(.schema == "weft.compiled-runtime-image-identity.v1" and .native == $native and (.runtime | test("^sha256:[0-9a-f]{64}$")))
  | .runtime' "$work/runtime-identity.json") || fail ferrule-identity 'the compiled runtime identity'
mkdir -m 700 -- "$work/docker"
DOCKER_CONFIG=$work/docker az acr login --name "$registry"
docker tag "weft-ferrule-service:$run_id" "$login_server/weft-ferrule-service:$tag"
built_images+=("$login_server/weft-ferrule-service:$tag")
DOCKER_CONFIG=$work/docker docker push --quiet "$login_server/weft-ferrule-service:$tag" >/dev/null
rm -rf -- "$work/docker"
service_digest=$(az acr repository show --name "$registry" --image "weft-ferrule-service:$tag" --query digest --output tsv)
[[ $service_digest =~ ^sha256:[0-9a-f]{64}$ ]] || fail images-built 'no digest for the Ferrule service image'
record fact image recorded "$(evidence repository weft-ferrule-service tag "$tag" digest "$service_digest" commit "$commit")"
pass images-built "$(evidence commit "$commit" tag "$tag" ferruleServiceIdentity "$service_identity" \
  compileToolchain "$toolchain" compilerIdentity "$compiler" compiledRuntimeIdentity "$runtime_identity")"

step application 'Application, worker, database and storage'
app_parameters=("${base_parameters[@]}" deployApplication=true "imageTag=$tag" ferruleRunner=service
  "ferruleServiceImage=weft-ferrule-service@$service_digest" "ferruleServiceIdentity=$service_identity"
  "ferruleServiceCertificateSha256=${pins[weft-ferrule-service]}" "ferruleServiceClientCertificateSha256=${pins[weft-ferrule-service-client]}"
  nativeWorkEnabled=true ferruleServiceCompile=true "ferruleCompileToolchain=$toolchain"
  "ferruleServiceWorkerClientCertificateSha256=${pins[weft-ferrule-worker-client]}" chatEnabled=true 'mappingEndpoints=[]')
deploy weft-qualify-application "${app_parameters[@]}"
save_outputs weft-qualify-application
public_host=$(deployment_output weft-qualify-application publicHost)
worker_name=$(deployment_output weft-qualify-application workerName)
[[ -n $public_host && -n $worker_name ]] || fail application-deployed 'the deployment did not report the public host and the worker'
pass application-deployed "$(evidence publicHost "$public_host" worker "$worker_name")"

job_step() { # label job pattern [start-options...]
  local label=$1 job=$2 pattern=$3
  shift 3
  run_job "$label" "$job" "$@"
  if [[ $execution_status != Succeeded ]]; then
    # Its console lines are kept with the evidence for investigation.
    wait_for 600 30 execution_lines "$last_execution" "$work/$label.lines" . || true
    fail "$label" "the execution ended $execution_status" "$(evidence execution "$last_execution")"
  fi
  await_lines "$label" "$last_execution" "$pattern"
  pass "$label" "$(evidence execution "$last_execution" line "$(grep -Em1 "$pattern" "$work/$label.lines")")"
}
job_step database-roles "$prefix-database-roles" 'restricted logins and migrator ownership are provisioned'
job_step migrations "$prefix-migrate" 'migrations are current'
job_step session-keys "$prefix-session-keys" '^Bootstrapped'
job_step private-storage "$prefix-private-storage" '^InitializedBlob'

restart_active() { # app
  local revision
  revision=$(azg containerapp revision list --name "$1" --query '[?properties.active].name | [0]' --output tsv)
  [[ -n $revision ]] || fail "restart-$1" "no active revision of $1"
  azg containerapp revision restart --name "$1" --revision "$revision" --output none
}
started=$(now)
restart_active "$prefix-app"
restart_active "$worker_name"
wait_for 900 20 healthy || fail server-started 'the server did not report health within 15 minutes'
pass server-started "$(evidence publicHost "$public_host")"

step platform-checks 'Platform checks'
http_get health "https://$public_host/api/v1/health"
hsts=$(sed -nE 's/^[Ss]trict-[Tt]ransport-[Ss]ecurity: *([^\r]*)\r?$/\1/p' "$work/health.headers")
if [[ $status != 200 || -z $hsts ]] || ! jq -e '.status == "ok"' "$work/health.body" >/dev/null; then
  fail health-hsts "health answered $status${hsts:+ with HSTS}" "$(evidence status "$status" hsts "$hsts")"
fi
pass health-hsts "$(evidence status "$status" hsts "$hsts")"
http_get session "https://$public_host/api/auth/session" --header 'X-Weft-Session: 1'
jq -c . "$work/session.body" >"$work/session.compact" 2>/dev/null || : >"$work/session.compact"
if [[ $status != 200 ]] || ! grep -q '"mode":"session"' "$work/session.compact" ||
  ! grep -q '"state":"anonymous"' "$work/session.compact"; then
  fail session-anonymous "the session status answered $status" "$(evidence status "$status")"
fi
pass session-anonymous "$(evidence status "$status" mode session state anonymous)"
http_get catalog "https://$public_host/catalog"
content_type=$(sed -nE 's/^[Cc]ontent-[Tt]ype: *([^\r;]*).*$/\1/p' "$work/catalog.headers" | tail -n 1)
foreign_origins=$(grep -Eo '(src|href)="https?://[^/"]+' "$work/catalog.body" | grep -v "://$public_host\$" | sort -u || true)
[[ $status == 200 && $content_type == text/html && -z $foreign_origins ]] ||
  fail catalog-same-origin "the catalog answered $status $content_type" "$(evidence status "$status" contentType "$content_type")"
pass catalog-same-origin "$(evidence status "$status" contentType "$content_type")"
wait_for 1200 30 app_lines "$prefix-app" "$started" 'WeftSessionHostReady' "$work/session-host.lines" ||
  fail session-host-ready 'no WeftSessionHostReady line after the start'
pass session-host-ready "$(evidence app "$prefix-app" since "$started")"

replicas_ready() { # app count
  local ready
  ready=$(azg containerapp replica list --name "$1" --query 'length([?properties.containers[0].ready])' --output tsv) || return 1
  [[ $ready =~ ^[0-9]+$ ]] && ((ready >= $2))
}
worker_status=$(azg containerapp show --name "$worker_name" --query properties.runningStatus --output tsv)
if [[ $worker_status != Running* ]] || ! wait_for 600 20 replicas_ready "$worker_name" 1; then
  fail worker-running "the worker app is $worker_status" "$(evidence status "$worker_status")"
fi
pass worker-running "$(evidence app "$worker_name" status "$worker_status")"
wait_for 600 20 replicas_ready "$prefix-ferrule" 1 || fail ferrule-service-ready 'no ready Ferrule service replica'
pass ferrule-service-ready "$(evidence app "$prefix-ferrule")"

# No container app or job in the group may mount the Docker socket or run a Docker image.
docker_free() { # check-name
  azg containerapp list --output json >"$work/apps.json"
  azg containerapp job list --output json >"$work/jobs.json"
  jq -s '[.[][] | {name, volumes: [.properties.template.volumes[]?.storageType],
      mounts: [.properties.template.containers[]?.volumeMounts[]?.mountPath],
      images: [.properties.template.containers[]?.image]}]' "$work/apps.json" "$work/jobs.json" >"$work/mounts.json"
  if jq -e 'any(.[]; any(.volumes[]; . != "AzureFile" and . != "EmptyDir" and . != "Secret")
      or any(.mounts[]; test("docker"; "i")) or any(.images[]; test("(^|/)docker[:@]|docker\\.sock"; "i")))' \
    "$work/mounts.json" >/dev/null; then
    fail "$1" 'a container app or job mounts a host volume or runs a Docker image' "$(jq -c '{resources: .}' "$work/mounts.json")"
  fi
  pass "$1" "$(jq -c '{resources: [.[] | {name, volumes}]}' "$work/mounts.json")"
}
docker_free no-docker-socket

step administrator 'First administrator'
principal=$(uuid)
owner_env=(WEFT_MIGRATION_CONNECTION_STRING=secretref:migrator-connection WEFT_DELIVER_OWNER_INPUT=secretref:owner-input)
job_step owner-identity "$prefix-owner" 'requested admission are current' --container-name owner \
  --args database provision-development-identity --env-vars "${owner_env[@]}" "WEFT_DEV_ORGANIZATION_ID=$organization" \
  "WEFT_DEV_PRINCIPAL_ID=$principal" "WEFT_DEV_ORGANIZATION_NAME=Weft qualification $run_id"
job_step owner-project-creation "$prefix-owner" 'Project creation capability granted' --container-name owner \
  --args database set-project-creation-capability "$organization" "$principal" true
# The binding key reaches the job only through Key Vault; the placeholder is restored afterwards.
jq -cn --arg tenant "$tenant" --arg object "$owner_object" '{tenantId: $tenant, objectId: $object}' >"$work/owner-input.json"
az keyvault secret set --vault-name "$vault" --name weft-owner-input --file "$work/owner-input.json" --encoding utf-8 \
  --content-type application/json --output none
rm -f -- "$work/owner-input.json"
vault_uri=$(azg keyvault show --name "$vault" --query properties.vaultUri --output tsv)
identity_id=$(azg identity show --name "$prefix-identity" --query id --output tsv)
azg containerapp job secret set --name "$prefix-owner" \
  --secrets "owner-input=keyvaultref:${vault_uri}secrets/weft-owner-input,identityref:$identity_id" --output none
job_step owner-binding "$prefix-owner" '"outcome":"(created|existing)"' --container-name owner \
  --args database bind-entra-user "$principal"
printf '{}' >"$work/owner-input.json"
az keyvault secret set --vault-name "$vault" --name weft-owner-input --file "$work/owner-input.json" --encoding utf-8 \
  --content-type application/json --output none
record fact administrator provisioned "$(evidence principal "$principal" organization "$organization")"

step registration 'Session host app registration'
redirect=https://$public_host/api/auth/callback/entra
registration_ready() {
  az ad app show --id "$client" --query '{uris: web.redirectUris, keys: keyCredentials[].customKeyIdentifier}' \
    --output json >"$work/registration.json" &&
    jq -e --arg uri "$redirect" --arg hex "$entra_thumbprint" --arg b64 "$entra_thumbprint_b64" \
      '(.uris // [] | index($uri)) != null and any(.keys[]?; . == $b64 or ascii_upcase == ($hex | ascii_upcase))' \
      "$work/registration.json" >/dev/null
}
if ! registration_ready; then
  confirm_item registration-updated \
    "Before sign-in, the app registration $client needs:" \
    "- the Web redirect URI $redirect" \
    "- the certificate $certificates/weft-entra-client.pem, for example:" \
    "  az ad app credential reset --id $client --cert @$certificates/weft-entra-client.pem --append" \
    'Answer yes once both are in place.'
  registration_ready || fail registration 'the redirect URI or the certificate credential is still missing'
fi
pass registration "$(evidence client "$client" redirectUri "$redirect" certificateThumbprint "$entra_thumbprint")"

step browser 'Signed-in viewer, chat and preview (operator)'
materials=$state/materials
mkdir -p -- "$materials"
# The preview-orders fixture with every input and output a JSON document, as builds require.
jq '.source_options = {json_document: true} | .target_options = {json_document: true}
  | .extra_sources[].options = {json_document: true} | .extra_targets[].options = {json_document: true}' \
  "$source_tree/engines/ferrule-host/tests/fixtures/preview-orders.json" >"$materials/orders-to-shipments.json"
printf '%s' '{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}' >"$materials/order.json"
printf '%s' '{"currency":"EUR"}' >"$materials/rates.json"
shipment() { # generatedAt
  printf '{\n  "orderId": "SO-1",\n  "region": "west",\n  "generatedAt": "%s",\n  "currency": "EUR",\n  "express": true,\n  "items": [\n    {\n      "sku": "A",\n      "quantity": 2\n    },\n    {\n      "sku": "B",\n      "quantity": 9007199254740993\n    }\n  ]\n}\n' "$1"
}
summary() {
  printf '{\n  "lineCount": 2,\n  "region": "west"\n}\n'
}
shipment 2026-10-20T12:00:00Z >"$materials/expected-shipment.json"
summary >"$materials/expected-summary.json"
printf 'Materials for the browser flows: %s\n' "$materials"
confirm_item signed-in \
  "Open https://$public_host/catalog and sign in through the session panel with the Entra account that was bound (the Azure CLI's signed-in user)." \
  'The panel reports an authenticated Session.'
confirm_item project-created 'Create a project.'
confirm_item mapping-uploaded "Upload $materials/orders-to-shipments.json as a native mapping; the mapping is saved."
confirm_item reports-read 'Generate the explanation and validation reports; both open with their saved results.'
confirm_item chat-answered \
  'Ask chat: Call search_mapping with {"query": "orderId"} and then say in one sentence which source field fills orderId.' \
  'The answer completes with verified citations.'
confirm_item preview-matched \
  "Try a sample: primary input $materials/order.json, named input rates from $materials/rates.json," \
  'parameters region (string) west and express (boolean) true. The Shipment output equals' \
  "$materials/expected-shipment.json except generatedAt, which is the preview's own clock."
confirm_item regression-passed \
  'Add the test case "SO-1 west express" at 2026-10-20T12:00:00Z with the same inputs and parameters and the' \
  "expected outputs Shipment ($materials/expected-shipment.json) and summary ($materials/expected-summary.json)." \
  'Save it and run it; the comparison passes.'
confirm_item build-completed \
  'Register the offered build environment, verify the regression evidence, approve and request a build.' \
  'The build completes and shows its artifact digest.'
query_database browser-evidence "SELECT (SELECT count(*) FROM weft.projects), (SELECT count(*) FROM weft.mapping_revisions),
  (SELECT count(*) FROM weft.revision_derivations), (SELECT count(*) FROM weft.chat_turns), (SELECT count(*) FROM weft.preview_runs),
  (SELECT count(*) FROM weft.regression_runs), (SELECT count(*) FROM weft.build_runs WHERE state = 'succeeded')"
read -r projects revisions derivations turns previews runs builds <"$work/browser-evidence.rows"
counts=$(evidence projects "$projects" revisions "$revisions" derivations "$derivations" chatTurns "$turns" \
  previewRuns "$previews" regressionRuns "$runs" succeededBuilds "$builds")
for count in "$projects" "$revisions" "$derivations" "$turns" "$previews" "$runs" "$builds"; do
  [[ $count =~ ^[1-9][0-9]*$ ]] || fail browser-corroborated 'the database lacks a record of a confirmed flow' "$counts"
done
pass browser-corroborated "$counts"
query_database build "SELECT a.project_id, a.mapping_id, a.build_id, a.environment_id, a.environment_version, r.artifact_digest,
  (position('$compiler' in convert_from(a.intent_body, 'UTF8')) > 0)
  FROM weft.build_runs r JOIN weft.build_approvals a USING (organization_id, build_id)
  WHERE r.state = 'succeeded' AND r.organization_id = '$organization' AND r.actor_id = '$principal'"
(($(wc -l <"$work/build.rows") == 1)) || fail build-provenance "expected one completed build, found $(wc -l <"$work/build.rows")"
read -r project_id mapping_id build_id environment_id environment_version artifact_digest names_compiler <"$work/build.rows"
build_evidence=$(evidence build "$build_id" artifactDigest "$artifact_digest" compilerIdentity "$compiler")
[[ $names_compiler == t ]] || fail build-provenance 'the build does not name the Ferrule service compiler identity' "$build_evidence"
pass build-provenance "$build_evidence"

step endpoint 'Mapping endpoint'
request_token caller "$audience/.default"
[[ $(claim caller aud) == "$audience" && $(claim caller ver) == 2.0 && $(claim caller tid) == "$tenant" &&
  $(claim caller azp) == "$caller_client" && -z $(claim caller scp) ]] ||
  fail token-caller 'the caller token must be an app-only v2 token for the endpoint audience'
caller_object=$(claim caller oid)
[[ $caller_object =~ $uuid_pattern ]] || fail token-caller 'the caller token has no object ID'
# The first token is kept until it expires, for the expired-token check.
cp -- "$work/caller.token" "$work/expiring.token"
expiring_until=$(claim caller exp)
request_token foreign "$client/.default"
[[ $(claim foreign aud) != "$audience" ]] || fail token-foreign 'the wrong-audience token names the endpoint audience'
pass tokens "$(evidence callerObject "$caller_object" audience "$audience" foreignAudience "$(claim foreign aud)" \
  firstTokenExpires "$expiring_until")"

target=$(uuid) grant=$(uuid)
endpoint_name=qualify endpoint_app=$prefix-qualify caller_job=$prefix-qualify-caller
endpoints=$(jq -cn --arg project "$project_id" --arg mapping "$mapping_id" --arg environment "$environment_id" \
  --argjson version "$environment_version" --arg target "$target" --arg grant "$grant" --arg caller "$caller_object" \
  --arg name "$endpoint_name" '[{name: $name, projectId: $project, mappingId: $mapping, environmentId: $environment,
    environmentVersion: $version, targetId: $target, callers: [{grantId: $grant, objectId: $caller}]}]')
endpoint_parameters=("${app_parameters[@]}" "mappingEndpoints=$endpoints" "mappingEndpointAudience=$audience"
  "mappingEndpointAdministratorId=$principal" "ferruleServiceEndpointClientCertificateSha256=${pins[weft-ferrule-endpoint-client]}"
  "compiledRuntimeIdentity=$runtime_identity")
endpoints_started=$(now)
deploy weft-qualify-endpoints "${endpoint_parameters[@]}"
save_outputs weft-qualify-endpoints
origin=$(deployment_output weft-qualify-endpoints mappingEndpointOrigins | jq -r '.[0] // empty')
read -r -a grant_arguments <<<"$(deployment_output weft-qualify-endpoints mappingEndpointGrantArguments | jq -r '.[0] // empty')"
[[ $origin == https://* && ${grant_arguments[1]:-} == grant-machine-invocation ]] ||
  fail endpoint-deployed 'the deployment did not report the endpoint origin and its grant'
pass endpoint-deployed "$(evidence app "$endpoint_app" origin "$origin" target "$target")"

node_image=$(sed -nE 's/^ARG NODE_IMAGE=(.+)$/\1/p' "$source_tree/deploy/container/Dockerfile")
# The caller job holds the current tokens as secure deployment parameters, written to a private file.
deploy_caller() {
  jq -n --rawfile caller "$work/caller.token" --rawfile foreign "$work/foreign.token" --rawfile expiring "$work/expiring.token" \
    '{"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#", contentVersion: "1.0.0.0",
      parameters: {callerToken: {value: $caller}, foreignToken: {value: $foreign}, expiringToken: {value: $expiring}}}' \
    >"$work/caller-parameters.json"
  azg deployment group create --name weft-qualify-caller \
    --template-file "$source_tree/deploy/azure/qualification/caller.bicep" --parameters "@$work/caller-parameters.json" \
    --parameters "name=$caller_job" "environmentName=$prefix-env" "image=$node_image" "tags=$tags_json" --output none
  rm -f -- "$work/caller-parameters.json"
}
deploy_caller

# Replaces a token that expires within 15 minutes; succeeds only when it did.
fresh() { # name scope
  (($(date +%s) > $(claim "$1" exp) - 900)) || return 1
  request_token "$1" "$2"
}

# Calls run as executions of the caller job; a caller or wrong-audience token close to expiry is replaced first.
call() { # name request credential
  local refreshed=false
  case $3 in
    caller) fresh caller "$audience/.default" && refreshed=true ;;
    foreign) fresh foreign "$client/.default" && refreshed=true ;;
  esac
  ! $refreshed || deploy_caller
  run_job "call-$1" "$caller_job" --container-name caller --args invoke "$1" "$origin" "$2" "$3"
  await_lines "call-$1" "$last_execution" '^weft-qualify-call '
  sed -n 's/^weft-qualify-call //p' "$work/call-$1.lines" | tail -n 1 >"$work/calls/$1.json"
}

# A completed call names its release, build and artifact, and both outputs equal independently authored bytes.
completed() { # name release replayed
  local file=$work/calls/$1.json generated
  jq -e --arg release "$2" --arg build "$build_id" --arg artifact "$artifact_digest" --arg replayed "$3" '
    .status == 200 and .replayed == $replayed and .body.schema == "weft.mapping-invocation-response.v1"
    and .body.releaseId == $release and .body.buildId == $build and .body.artifactDigest == $artifact
    and (.body.outputs | length) == 2' "$file" >/dev/null ||
    fail "$1" "the call did not complete with release $2" "$(jq -c '{status, replayed, outcome: .body.outcome?, releaseId: .body.releaseId?}' "$file")"
  for output in shipment summary; do
    jq -r --arg name "$output" '.body.outputs[] | select(.name == (if $name == "shipment" then null else $name end)) | .content' \
      "$file" | base64 -d >"$work/$1.$output"
    [[ $(sha256sum "$work/$1.$output" | cut -d' ' -f1) == $(jq -r --arg name "$output" \
      '.body.outputs[] | select(.name == (if $name == "shipment" then null else $name end)) | .sha256' "$file") ]] ||
      fail "$1" "the $output digest differs from its bytes"
  done
  generated=$(sed -nE 's/^  "generatedAt": "([^"]+)",$/\1/p' "$work/$1.shipment")
  [[ -n $generated && ${generated:0:19} == "$(jq -r '.body.currentDateTime[0:19]' "$file")" ]] ||
    fail "$1" "the shipment's clock is not the call's clock"
  cmp -s "$work/$1.shipment" <(shipment "$generated") || fail "$1" 'the shipment differs from the expected bytes'
  cmp -s "$work/$1.summary" <(summary) || fail "$1" 'the summary differs from the expected bytes'
  pass "$1" "$(jq -c '{status, replayed, releaseId: .body.releaseId, buildId: .body.buildId, artifactDigest: .body.artifactDigest,
    revisionId: .body.revisionId, currentDateTime: .body.currentDateTime, bodySha256}' "$file")"
}

refused() { # name status outcome
  local file=$work/calls/$1.json
  jq -e --argjson status "$2" --arg outcome "$3" \
    '.status == $status and .body.outcome == $outcome and ($status != 401 or .bearerChallenge)' "$file" >/dev/null ||
    fail "$1" "expected $2 $3" "$(jq -c '{status, outcome: .body.outcome?, bearerChallenge}' "$file")"
  pass "$1" "$(jq -c '{status, outcome: .body.outcome, bearerChallenge}' "$file")"
}

same_body() { # check first second
  [[ $(jq -r .bodySha256 "$work/calls/$2.json") == $(jq -r .bodySha256 "$work/calls/$3.json") ]] ||
    fail "$1" "$3 returned different bytes than $2"
}

audit() { # label request
  query_database "$1" "SELECT sequence, kind, coalesce(release_id::text, '-') FROM weft.machine_invocation_audit
    WHERE organization_id = '$organization' AND target_id = '$target' AND request_id = '$2' ORDER BY sequence"
}

release=("$principal" "$organization" "$project_id" "$mapping_id")
release_target=("${release[@]}" "$environment_id" "$environment_version" "$target")
qualification=$(uuid)
# Qualification replays the build's regression evidence through the service's execute profile; only an
# unavailable runtime is tried again with the same qualification ID.
for attempt in 1 2 3; do
  run_job qualify "$prefix-endpoint-release" --container-name release \
    --args release qualify-build "${release[@]}" "$build_id" "$qualification"
  await_lines qualify "$last_execution" '^\{.*"(outcome|error)"'
  grep -E '^\{.*"(outcome|error)"' "$work/qualify.lines" | tail -n 1 >"$work/qualify.json"
  outcome=$(jq -r '.outcome // .error' "$work/qualify.json")
  [[ $outcome == unavailable && $attempt -lt 3 ]] || break
done
[[ $outcome == qualified ]] || fail build-qualified "qualification answered $outcome"
[[ $(jq -r .artifactSha256 "$work/qualify.json") == "$artifact_digest" &&
  $(jq -r .runtimeImage "$work/qualify.json") == "$runtime_identity" ]] || fail build-qualified 'qualification provenance'
pass build-qualified "$(evidence qualification "$qualification" build "$build_id" artifactDigest "$artifact_digest" runtimeImage "$runtime_identity")"
tool_outcome register "$prefix-endpoint-release" release Succeeded release register-release-target "${release_target[@]}"
[[ $outcome == registered ]] || fail target-registered "registration answered $outcome"
pass target-registered "$(evidence target "$target")"
first_release=$(uuid)
tool_outcome activate-first "$prefix-endpoint-release" release Succeeded \
  release activate-release "${release_target[@]}" "$first_release" "$qualification" none
[[ $outcome == activated ]] || fail release-activated "activation answered $outcome"
pass release-activated "$(evidence release "$first_release" qualification "$qualification")"
tool_outcome grant "$prefix-endpoint-grants" grants Succeeded "${grant_arguments[@]}"
[[ $outcome == granted ]] || fail caller-granted "the grant answered $outcome"
pass caller-granted "$(evidence grant "$grant" callerObject "$caller_object")"

wait_for 1200 30 app_lines "$endpoint_app" "$endpoints_started" 'WeftMappingEndpointReady' "$work/endpoint.lines" ||
  fail endpoint-ready 'no WeftMappingEndpointReady line'
run_job ready "$caller_job" --container-name caller --args ready ready "$origin"
await_lines ready "$last_execution" '^weft-qualify-call '
grep -q '"status":204' "$work/ready.lines" || fail endpoint-ready 'the endpoint is not ready'
pass endpoint-ready "$(evidence app "$endpoint_app" ready 204)"

first_request=$(uuid)
call first "$first_request" caller
completed first "$first_release" false
audit audit-first "$first_request"
[[ $(awk '{print $2, $3}' "$work/audit-first.rows" | paste -sd ' ') == "claimed - completed $first_release" ]] ||
  fail audit-first 'the ledger does not record one claimed and one completed attempt'
pass audit-first "$(evidence request "$first_request" events "$(paste -sd ';' "$work/audit-first.rows")")"
call retry "$first_request" caller
completed retry "$first_release" true
same_body retry-replayed first retry
audit audit-retry "$first_request"
cmp -s "$work/audit-first.rows" "$work/audit-retry.rows" || fail retry-replayed 'the retry added ledger events: it ran again'
pass retry-replayed "$(evidence request "$first_request" events "$(paste -sd ';' "$work/audit-retry.rows")")"

second_release=$(uuid)
tool_outcome activate-second "$prefix-endpoint-release" release Succeeded \
  release activate-release "${release_target[@]}" "$second_release" "$qualification" "$first_release"
[[ $outcome == activated ]] || fail second-activated "activation answered $outcome"
call second "$(uuid)" caller
completed second "$second_release" false
rollback_release=$(uuid)
tool_outcome rollback "$prefix-endpoint-release" release Succeeded \
  release rollback-release "${release_target[@]}" "$rollback_release" "$first_release" "$second_release"
[[ $outcome == activated ]] || fail rolled-back "rollback answered $outcome"
call rolled-back "$(uuid)" caller
completed rolled-back "$rollback_release" false

step recovery 'Failed deployment recovery'
# An activation of a qualification that was never stored is refused, and the current release keeps serving.
unqualified=$(uuid)
tool_outcome activate-unqualified "$prefix-endpoint-release" release Failed \
  release activate-release "${release_target[@]}" "$(uuid)" "$unqualified" "$rollback_release"
[[ $outcome != activated ]] || fail activation-refused 'an unqualified activation succeeded'
pass activation-refused "$(evidence qualification "$unqualified" outcome "$outcome")"
call after-refused-activation "$(uuid)" caller
completed after-refused-activation "$rollback_release" false
# A revision whose configuration the endpoint refuses never becomes ready, and the previous one keeps serving.
serving=$(azg containerapp revision list --name "$endpoint_app" --query '[?properties.active].name | [0]' --output tsv)
broken=$endpoint_app--broken-${run_id##*-}
azg containerapp update --name "$endpoint_app" --revision-suffix "broken-${run_id##*-}" \
  --set-env-vars WEFT_ENDPOINT_CONCURRENCY=0 --output none || true
revision_failed() {
  azg containerapp revision show --name "$endpoint_app" --revision "$broken" \
    --query '{health: properties.healthState, running: properties.runningState}' --output json >"$work/broken.json" &&
    jq -e '.health == "Unhealthy" or (.running | test("Failed|Degraded"))' "$work/broken.json" >/dev/null
}
wait_for 1200 30 revision_failed || fail broken-revision 'the refused revision did not report a failure'
azg containerapp revision show --name "$endpoint_app" --revision "$serving" \
  --query '{active: properties.active, traffic: properties.trafficWeight, health: properties.healthState}' --output json \
  >"$work/serving.json"
jq -e '.active == true and .traffic == 100 and .health == "Healthy"' "$work/serving.json" >/dev/null ||
  fail broken-revision 'the previous revision no longer serves all traffic' "$(jq -c . "$work/serving.json")"
call during-broken-revision "$(uuid)" caller
completed during-broken-revision "$rollback_release" false
pass broken-revision "$(evidence refused "$broken" serving "$serving" brokenHealth "$(jq -r .health "$work/broken.json")" \
  brokenRunning "$(jq -r .running "$work/broken.json")")"
deploy weft-qualify-endpoints "${endpoint_parameters[@]}"
call after-redeployment "$(uuid)" caller
completed after-redeployment "$rollback_release" false

step authorization 'Token refusals'
call wrong-audience "$(uuid)" foreign
refused wrong-audience 401 unauthorized

step restart 'Restart and scale recovery'
snapshot="SELECT (SELECT count(*) FROM weft.projects), (SELECT count(*) FROM weft.mapping_revisions),
  (SELECT count(*) FROM weft.chat_conversations), (SELECT count(*) FROM weft.chat_turns), (SELECT count(*) FROM weft.preview_runs),
  (SELECT count(*) FROM weft.regression_runs), (SELECT count(*) FROM weft.build_runs)"
query_database before-restart "$snapshot"
confirm_item work-open \
  'Keep the signed-in browser open on the conversation and on the completed preview while the server restarts,' \
  'the Ferrule service scales and the worker restarts. Start no new work until the next question.'
restarted=$(now)
restart_active "$prefix-app"
wait_for 900 20 healthy || fail server-restarted 'health did not return after the restart'
wait_for 1200 30 app_lines "$prefix-app" "$restarted" 'WeftSessionHostReady' "$work/restart.lines" ||
  fail server-restarted 'no WeftSessionHostReady line after the restart'
pass server-restarted "$(evidence since "$restarted")"
read -r scale_min scale_max <<<"$(azg containerapp show --name "$prefix-ferrule" \
  --query '{min: properties.template.scale.minReplicas, max: properties.template.scale.maxReplicas}' --output json |
  jq -r '"\(.min) \(.max)"')"
azg containerapp update --name "$prefix-ferrule" --min-replicas 3 --max-replicas $((scale_max > 3 ? scale_max : 3)) --output none
wait_for 1200 20 replicas_ready "$prefix-ferrule" 3 || fail ferrule-scaled 'three Ferrule service replicas did not become ready'
call while-scaled "$(uuid)" caller
completed while-scaled "$rollback_release" false
azg containerapp update --name "$prefix-ferrule" --min-replicas "$scale_min" --max-replicas "$scale_max" --output none
wait_for 1200 20 replicas_ready "$prefix-ferrule" 1 || fail ferrule-scaled 'the Ferrule service did not return to service'
pass ferrule-scaled "$(evidence app "$prefix-ferrule" scaledTo 3 restoredMin "$scale_min" restoredMax "$scale_max")"
restart_active "$worker_name"
wait_for 600 20 replicas_ready "$worker_name" 1 || fail worker-restarted 'the worker did not return'
pass worker-restarted "$(evidence app "$worker_name")"
confirm_item work-reopened \
  'Reload the browser: you are still signed in without a new sign-in, and the conversation and the preview' \
  'reopen with the same content.'
query_database after-restart "$snapshot"
cmp -s "$work/before-restart.rows" "$work/after-restart.rows" ||
  fail retained-state 'recorded work changed across the restart and scaling'
pass retained-state "$(evidence counts "$(<"$work/after-restart.rows")")"
call replay-after-restart "$first_request" caller
completed replay-after-restart "$first_release" true
same_body replay-after-restart first replay-after-restart

step revocation 'Expired token and revoked grant'
# Validation allows no clock skew; the margin covers clock differences between this machine and Azure.
expired_now() {
  (($(date +%s) > expiring_until + 60))
}
if ! expired_now; then
  printf 'Waiting until the first caller token has expired (%s).\n' "$(date -u -d "@$expiring_until" +%Y-%m-%dT%H:%M:%SZ)"
  wait_for $((expiring_until + 120 - $(date +%s))) 60 expired_now || fail expired-token 'the first token did not expire'
fi
call expired "$(uuid)" expiring
refused expired 401 unauthorized
tool_outcome revoke "$prefix-endpoint-grants" grants Succeeded \
  database revoke-machine-invocation "${release_target[@]}" "$grant"
[[ $outcome == revoked ]] || fail grant-revoked "revocation answered $outcome"
pass grant-revoked "$(evidence grant "$grant")"
call revoked "$(uuid)" caller
refused revoked 403 refused
call revoked-replay "$first_request" caller
refused revoked-replay 403 refused

step isolation 'Isolation evidence'
azg containerapp show --name "$prefix-ferrule" --output json >"$work/ferrule.json"
jq -e '.properties.configuration.ingress.external == false
  and ([.properties.template.containers[0].env[] | select(.name == "WEFT_FERRULE_SERVICE_COMPILE_ENABLED" or .name == "WEFT_FERRULE_SERVICE_EXECUTION_ENABLED") | .value] == ["true", "true"])
  and any(.properties.template.containers[0].probes[]; .type == "Readiness" and .httpGet.path == "/v1/health/ready")' \
  "$work/ferrule.json" >/dev/null || fail ferrule-sandbox-admission 'the Ferrule service configuration'
replicas_ready "$prefix-ferrule" 1 || fail ferrule-sandbox-admission 'no Ferrule service replica is ready'
pass ferrule-sandbox-admission "$(evidence app "$prefix-ferrule" ingress internal profiles 'compile,execute' readiness /v1/health/ready \
  compilerIdentity "$compiler" compiledRuntimeIdentity "$runtime_identity")"
docker_free no-docker-socket-final

step summary 'Result'
printf 'Qualified: %d automated checks passed and %d operator confirmations recorded.\n' "$checks_passed" "$guided_confirmed"
