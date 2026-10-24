#!/usr/bin/env bash
# Local acceptance of the weft CLI against the packaged Session stack (docs/cli.md, docs/hosted-acceptance.md).
# It runs the hosted acceptance (acceptance-test.sh) with this script as its follow-on stage. The run's issuer
# registers the CLI as a public client, and the stage runs the published linux-x64 single-file CLI in a
# container that resolves login.microsoftonline.com to the issuer and trusts only the run's certificate
# authority: no authority override and no instance-discovery switch. The container has a D-Bus session and an
# unlocked Secret Service keyring, so MSAL keeps its token cache in the operating-system store as it would for
# a user. The CLI signs in with a device code, which the issuer's test-only approval confirms in place of the
# user. It then lists and creates projects, creates a mapping, appends a revision, ensures and compares reports,
# runs and downloads a preview, saves and runs a regression case from it, approves and runs a compiled build,
# revokes its approval, asks chat, and signs out. The stage
# removes its container and image before the hosted acceptance's own cleanup.
set -euo pipefail

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
compose_directory=$repository/deploy/compose

if [[ ${WEFT_CLI_ACCEPTANCE_RUN:-} != stage ]]; then
  if [[ ${1:-} == --cli-only && $# == 1 ]]; then
    export WEFT_ACCEPTANCE_STAGE_ONLY=true
  elif (($# != 0)); then
    echo 'Usage: cli-acceptance-test.sh [--cli-only]' >&2
    exit 2
  fi
  # The CLI's own public client registration, with the run's API audience and the delegated scope.
  WEFT_ACCEPTANCE_CLI_CLIENT_ID=$(cat /proc/sys/kernel/random/uuid)
  WEFT_ACCEPTANCE_PUBLIC_CLIENTS=$(printf '[{"clientId":"%s","scope":"weft.api"}]' "$WEFT_ACCEPTANCE_CLI_CLIENT_ID")
  # The server admits bearer requests from this client only (compose.bearer.yml).
  WEFT_API_BEARER_CLIENT_IDS=$WEFT_ACCEPTANCE_CLI_CLIENT_ID WEFT_API_BEARER_SCOPE=weft.api
  export WEFT_ACCEPTANCE_CLI_CLIENT_ID WEFT_ACCEPTANCE_PUBLIC_CLIENTS WEFT_API_BEARER_CLIENT_IDS WEFT_API_BEARER_SCOPE
  export WEFT_CLI_ACCEPTANCE_RUN=stage WEFT_ACCEPTANCE_STAGE=$compose_directory/cli-acceptance-test.sh
  exec "$compose_directory/acceptance-test.sh"
fi

# shellcheck source=acceptance-lifecycle.sh
source "$compose_directory/acceptance-lifecycle.sh"

for setting in WEFT_ACCEPTANCE_WORK WEFT_ACCEPTANCE_COMPOSE_PROJECT WEFT_ACCEPTANCE_PRINCIPAL_ID WEFT_ENTRA_TENANT_ID \
  WEFT_ENTRA_CLIENT_ID WEFT_ACCEPTANCE_CLI_CLIENT_ID WEFT_ACCEPTANCE_TRUST_PEM WEFT_PUBLIC_HOST WEFT_ACCEPTANCE_FIXTURES; do
  [[ -n ${!setting:-} ]] || { echo "FAILED: the CLI stage requires $setting from the hosted acceptance run" >&2; exit 1; }
done
project=$WEFT_ACCEPTANCE_COMPOSE_PROJECT
work=$(mktemp -d "$WEFT_ACCEPTANCE_WORK/cli.XXXXXXXX")
chmod 0700 "$work"
image=weft-cli-runner:$project
image_built=false
container=

cleanup() {
  local status=$? cleanup_status=0
  set +e
  acceptance_result "$status"
  status=$?
  if [[ -n $container ]]; then
    docker rm --force "$container" >/dev/null 2>&1 || cleanup_status=1
  fi
  if [[ $image_built == true ]]; then
    docker image rm "$image" >/dev/null 2>&1 || cleanup_status=1
  fi
  ((cleanup_status == 0)) || { echo 'CLI stage cleanup failed.' >&2; status=1; }
  exit "$status"
}
trap cleanup EXIT

fail() {
  printf 'FAILED: %s\n' "$1" >&2
  exit 1
}

echo 'Publishing the linux-x64 single-file CLI'
dotnet publish "$repository/dotnet/apps/Weft.Cli/Weft.Cli.csproj" --configuration Release -p:WeftCliRuntime=linux-x64 \
  -p:RestoreLockedMode=true --output "$work/cli" >"$work/publish.log" 2>&1 ||
  { tail -n 20 "$work/publish.log" >&2; fail 'CLI publish'; }
[[ -x $work/cli/weft ]] || fail 'the published CLI is missing'

# The runner: a pinned Ubuntu with the CLI's native dependencies (OpenSSL) and a Secret Service keyring.
echo "Building $image"
docker build --quiet --tag "$image" --label "com.docker.compose.project=$project" - >/dev/null <<'DOCKERFILE'
FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates libssl3t64 curl jq socat dbus gnome-keyring libsecret-1-0 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 65532 --user-group --home-dir /home/cli --no-create-home --shell /bin/bash cli
DOCKERFILE
image_built=true

printf '%s\n' "$WEFT_ACCEPTANCE_TRUST_PEM" >"$work/trust.pem"
chmod 0644 "$work/trust.pem"
chmod 0755 "$work" "$work/cli"
ingress=$(docker ps --quiet --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=ingress' | head -n 1)
[[ -n $ingress ]] || fail 'the stack has no ingress'
ingress_address=$(docker inspect --format "{{(index .NetworkSettings.Networks \"${project}_egress\").IPAddress}}" "$ingress")
[[ $ingress_address =~ ^[0-9.]+$ ]] || fail 'ingress address'

# The identity network resolves login.microsoftonline.com to the issuer. The public host resolves to loopback,
# where the steps relay port 443 to the ingress, so the CLI uses the public origin exactly as configured.
container=$(docker create --label "com.docker.compose.project=$project" --network "${project}_identity" \
  --add-host "$WEFT_PUBLIC_HOST:127.0.0.1" --sysctl net.ipv4.ip_unprivileged_port_start=443 \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  --user 65532:65532 --tmpfs /home/cli:size=64m,mode=0700,uid=65532,gid=65532 --tmpfs /tmp:size=64m,mode=1777 \
  --memory 1g --pids-limit 256 \
  -e HOME=/home/cli -e SSL_CERT_FILE=/opt/trust.pem -e WEFT_CLI=/opt/weft/weft -e "WEFT_CLI_INGRESS=$ingress_address:8443" \
  -e "WEFT_PUBLIC_HOST=$WEFT_PUBLIC_HOST" -e "WEFT_ENTRA_TENANT_ID=$WEFT_ENTRA_TENANT_ID" \
  -e "WEFT_ENTRA_CLIENT_ID=$WEFT_ENTRA_CLIENT_ID" -e "WEFT_ACCEPTANCE_CLI_CLIENT_ID=$WEFT_ACCEPTANCE_CLI_CLIENT_ID" \
  -e "WEFT_ACCEPTANCE_PRINCIPAL_ID=$WEFT_ACCEPTANCE_PRINCIPAL_ID" -e WEFT_ACCEPTANCE_FIXTURES=/fixtures \
  -v "$work/cli:/opt/weft:ro" -v "$work/trust.pem:/opt/trust.pem:ro" -v "$WEFT_ACCEPTANCE_FIXTURES:/fixtures:ro" \
  -v "$compose_directory/cli-acceptance-steps.sh:/opt/steps.sh:ro" \
  "$image" bash /opt/steps.sh)
docker network connect "${project}_egress" "$container"
echo 'Running the CLI flows'
docker start --attach "$container" | tee "$work/flows.log" || fail 'CLI flows'
sed -nE 's/^cli-project-id=([0-9a-f-]{36})$/\1/p' "$work/flows.log" >"$WEFT_ACCEPTANCE_WORK/project-id"
[[ $(wc -l <"$WEFT_ACCEPTANCE_WORK/project-id") == 1 ]] || fail 'the CLI recorded no project'
echo 'ok - the CLI signed in with a device code and completed every flow as the signed-in user'

# The issuer saw the CLI's grants and refused nothing.
docker logs "$(docker ps --quiet --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=issuer' | head -n 1)" \
  >"$work/issuer.log" 2>&1
grep -q 'issuer public-device-code' "$work/issuer.log" || fail 'the CLI did not use the device authorization flow'
grep -q 'issuer public-token' "$work/issuer.log" || fail 'the issuer issued the CLI no token'
! grep -q 'issuer refused' "$work/issuer.log" || fail "issuer refusals: $(grep -c 'issuer refused' "$work/issuer.log")"
echo 'ok - the CLI redeemed its grants at the issuer without a refusal'
acceptance_complete=true
