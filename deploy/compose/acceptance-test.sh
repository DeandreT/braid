#!/usr/bin/env bash
# Local P8 acceptance of the packaged Session stack (docs/hosted-acceptance.md). Builds the runtime
# and Ferrule images from this checkout, starts the hosted shape from new volumes with the Ferrule
# service, compose.features.yml and the test-only compose.acceptance.yml, provisions one external
# identity, and runs apps/web/tests/hosted-acceptance.spec.ts: real sign-in, the main workflows
# through the Angular application, and server restart and service scaling during the run. It then
# checks the stack's own evidence and removes every container, network, volume and image tag it
# created; a failed cleanup or a remaining owned object fails the run.
# Generated passwords, keys and certificates stay in a private temporary directory and are never printed.
set -euo pipefail

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
compose_directory=$repository/deploy/compose
# shellcheck source=acceptance-lifecycle.sh
source "$compose_directory/acceptance-lifecycle.sh"
# shellcheck source=test-subnets.sh
source "$compose_directory/test-subnets.sh"
project=weft-accept-$(openssl rand -hex 4)
work=$(mktemp -d "${TMPDIR:-/tmp}/weft-accept.XXXXXXXX")
chmod 0700 "$work"
built_images=()
files=(--file "$compose_directory/compose.yml" --file "$compose_directory/compose.ferrule-service.yml"
  --file "$compose_directory/compose.features.yml" --file "$compose_directory/compose.acceptance.yml")
if [[ ${WEFT_ACCEPTANCE_MODEL_FIXTURE:-false} == true ]]; then
  files+=(--file "$compose_directory/compose.model-fixture.yml")
fi
# Bearer access for the allowlisted public clients, such as the CLI stage's (cli-acceptance-test.sh).
if [[ -n ${WEFT_API_BEARER_CLIENT_IDS:-} ]]; then
  files+=(--file "$compose_directory/compose.bearer.yml")
fi

compose() {
  docker compose --project-name "$project" "${files[@]}" --profile operator "$@"
}

owned_objects() {
  local filter=label=com.docker.compose.project=$project
  docker ps --all --quiet --filter "$filter"
  docker volume ls --quiet --filter "$filter"
  docker network ls --quiet --filter "$filter"
}

# Every step is attempted; any cleanup failure or remaining owned object fails the run.
cleanup() {
  local status=$? cleanup_status=0
  set +e
  acceptance_result "$status"
  status=$?
  if ((status != 0)) && [[ -n ${WEFT_ACCEPTANCE_LOG_DIRECTORY:-} ]]; then
    # Service logs and browser failure context carry no secrets: the server and tools emit
    # fixed status lines only, and the issuer and relays log no protocol values.
    mkdir -p "$WEFT_ACCEPTANCE_LOG_DIRECTORY/$project" &&
      compose logs --no-color --timestamps >"$WEFT_ACCEPTANCE_LOG_DIRECTORY/$project/services.log" 2>&1
    [[ -d $work/playwright ]] && cp -r "$work/playwright" "$WEFT_ACCEPTANCE_LOG_DIRECTORY/$project/"
  fi
  if [[ -n $(owned_objects) ]] && ! compose down --volumes --remove-orphans --timeout 30 >/dev/null 2>&1; then
    # Compose cannot render the stack without its settings (for example after an early failure);
    # remove the project's labelled objects directly. The run still fails if any remain.
    local filter=label=com.docker.compose.project=$project
    docker ps --all --quiet --filter "$filter" | xargs -r docker rm --force --volumes >/dev/null 2>&1
    docker network ls --quiet --filter "$filter" | xargs -r docker network rm >/dev/null 2>&1
    docker volume ls --quiet --filter "$filter" | xargs -r docker volume rm >/dev/null 2>&1
  fi
  [[ -z $(owned_objects) ]] || { echo 'Owned compose objects remain.' >&2; cleanup_status=1; }
  if ((${#built_images[@]} > 0)); then
    docker image rm "${built_images[@]}" >/dev/null 2>&1 || cleanup_status=1
  fi
  rm -rf -- "$work" || cleanup_status=1
  if ((cleanup_status != 0)); then
    echo 'Cleanup of owned resources failed.' >&2
    status=1
  fi
  if ((status != 0)); then
    echo 'Acceptance failed.' >&2
  elif [[ -n ${WEFT_ACCEPTANCE_HOLD:-} ]]; then
    echo 'Held stack removed.'
  elif [[ ${WEFT_ACCEPTANCE_STAGE_ONLY:-false} == true ]]; then
    echo 'Client-stage acceptance passed (browser workflows not run).'
  else
    echo 'Acceptance passed.'
  fi
  exit "$status"
}
trap cleanup EXIT

fail() {
  printf 'FAILED: %s\n' "$1" >&2
  exit 1
}

# A dedicated client stage can qualify itself on the same packaged stack without borrowing a browser.
case ${WEFT_ACCEPTANCE_STAGE_ONLY:-false} in
  true) [[ -x ${WEFT_ACCEPTANCE_STAGE:-} && -z ${WEFT_ACCEPTANCE_HOLD:-} ]] ||
    fail 'Stage-only acceptance requires an executable WEFT_ACCEPTANCE_STAGE and no hold.' ;;
  false)
    [[ -x ${WEFT_CHROMIUM_EXECUTABLE:-} ]] || fail 'Set WEFT_CHROMIUM_EXECUTABLE to a Chromium executable.'
    [[ -d $repository/node_modules/@playwright/test ]] || fail 'Run npm ci in the repository first.' ;;
  *) fail 'WEFT_ACCEPTANCE_STAGE_ONLY must be true or false' ;;
esac
export WEFT_ACCEPTANCE_OLLAMA_VOLUME=${WEFT_ACCEPTANCE_OLLAMA_VOLUME:-weft-ollama-models}
export WEFT_ACCEPTANCE_CHAT_MODEL=${WEFT_ACCEPTANCE_CHAT_MODEL:-qwen2.5:1.5b}
case ${WEFT_ACCEPTANCE_MODEL_FIXTURE:-false} in
  false) docker volume inspect "$WEFT_ACCEPTANCE_OLLAMA_VOLUME" >/dev/null 2>&1 ||
    fail "Model volume $WEFT_ACCEPTANCE_OLLAMA_VOLUME is missing; see docs/hosted-acceptance.md." ;;
  true) export WEFT_ACCEPTANCE_MODEL_FIXTURE=true ;;
  *) fail 'WEFT_ACCEPTANCE_MODEL_FIXTURE must be true or false' ;;
esac
export WEFT_ACCEPTANCE_NODE_IMAGE=${WEFT_ACCEPTANCE_NODE_IMAGE:-docker.io/library/node@sha256:c3de60bf2f9dd0ac6370e6117950ff62d6e339527e7472301c9c78a017978392}

build() {
  local tag=$1
  shift
  echo "Building $tag"
  docker build --quiet --tag "$tag" "$@" >/dev/null
  built_images+=("$tag")
}

if [[ -z ${WEFT_IMAGE:-} ]]; then
  for target in weft edge weft-worker; do
    build "$target:$project" --file "$repository/deploy/container/Dockerfile" --target "$target" "$repository"
  done
  export WEFT_IMAGE=weft:$project WEFT_EDGE_IMAGE=edge:$project WEFT_WORKER_IMAGE=weft-worker:$project
fi
if [[ -z ${WEFT_FERRULE_SERVICE_IMAGE:-} ]]; then
  # A supplied native image must be a tag or registry digest of an engines/ferrule-host build.
  native=${WEFT_FERRULE_NATIVE_IMAGE:-}
  if [[ -z $native ]]; then
    build "weft-ferrule-native:$project" "$repository/engines/ferrule-host"
    native=weft-ferrule-native:$project
  fi
  container=$(docker create "$native")
  docker cp "$container:/ferrule-host" "$work/ferrule-host" >/dev/null
  docker rm "$container" >/dev/null
  native_digest=$(sha256sum "$work/ferrule-host" | cut -d' ' -f1)
  rm -f "$work/ferrule-host"
  build "weft-ferrule-service:$project" --file "$repository/engines/ferrule-service/Dockerfile" \
    --build-arg "WEFT_NATIVE_IMAGE=$native" --build-arg "WEFT_NATIVE_BINARY_SHA256=$native_digest" "$repository"
  export WEFT_FERRULE_SERVICE_IMAGE=weft-ferrule-service:$project
fi

for role in POSTGRES_ADMIN MIGRATOR RUNTIME INGEST CATALOG METADATA CREATOR REPORT DERIVATION MAINTENANCE \
  IDENTITY PAYLOAD_PERMISSION SESSION_READER SESSION_WRITER CHAT_READER CHAT_WRITER CHAT_LIFECYCLE \
  PREVIEW_READER PREVIEW_WRITER CASE_READER CASE_WRITER RUN_READER RUN_WRITER BUILD_READER BUILD_WRITER BUILD_RUN_READER \
  BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER RELEASE_READER RELEASE_WRITER INVOCATION_GRANT_WRITER INVOCATION_WRITER API_AUDIT_WRITER DRAFT_READER DRAFT_WRITER; do
  export "WEFT_LOCAL_${role}_PASSWORD=$(openssl rand -hex 24)"
done
new_pem() {
  openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 30 -subj "/CN=$1" \
    -keyout "$work/$1.key" -out "$work/$1.crt" 2>/dev/null
  cat "$work/$1.key" "$work/$1.crt"
}
# Each side of a pinned transport pins the other's certificate (SHA-256 of its DER encoding).
pin() { openssl x509 -in "$work/$1.crt" -outform DER | openssl dgst -sha256 -r | cut -d' ' -f1; }
# The run's own certificate authority and the two TLS names it signs. The server trusts only this
# authority; the browser admits the leaves' public keys for this run.
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 -subj '/CN=Weft acceptance test authority' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$work/authority.key" -out "$work/authority.crt" 2>/dev/null
new_leaf() {
  openssl req -new -newkey rsa:2048 -nodes -sha256 -subj "/CN=$2" -keyout "$work/$1.key" -out "$work/$1.csr" 2>/dev/null
  printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nbasicConstraints=critical,CA:FALSE\n' \
    "$2" >"$work/$1.ext"
  openssl x509 -req -in "$work/$1.csr" -CA "$work/authority.crt" -CAkey "$work/authority.key" -CAcreateserial \
    -days 2 -sha256 -extfile "$work/$1.ext" -out "$work/$1.crt" 2>/dev/null
  cat "$work/$1.key" "$work/$1.crt" "$work/authority.crt"
}
spki() { openssl x509 -in "$work/$1.crt" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | base64; }

unset WEFT_PRIVATE_STORAGE_ROOT # compose.acceptance.yml leaves it unset for Blob storage
export WEFT_PUBLIC_HOST=weft.example.test WEFT_EDGE_TRUSTED_PROXIES=
export WEFT_COMPOSE_PORT=0 WEFT_ACCEPTANCE_HTTPS_PORT=0 WEFT_ACCEPTANCE_ISSUER_PORT=0
WEFT_SESSION_KEY_PEM=$(new_pem weft-session-key)
WEFT_ENTRA_CLIENT_PEM=$(new_pem weft-entra-client)
WEFT_ACCEPTANCE_CLIENT_CERTIFICATE=$(cat "$work/weft-entra-client.crt")
WEFT_SESSION_ORGANIZATION_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ENTRA_TENANT_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ENTRA_CLIENT_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ACCEPTANCE_SUBJECT=$(openssl rand 32 | basenc --base64url | tr -d '=')
WEFT_ACCEPTANCE_OBJECT_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ACCEPTANCE_ISSUER_PEM=$(new_leaf issuer login.microsoftonline.com)
WEFT_ACCEPTANCE_INGRESS_PEM=$(new_leaf ingress "$WEFT_PUBLIC_HOST")
WEFT_ACCEPTANCE_TRUST_PEM=$(cat "$work/authority.crt")
WEFT_ACCEPTANCE_SIGNING_KEY=$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null)
export WEFT_SESSION_KEY_PEM WEFT_ENTRA_CLIENT_PEM WEFT_ACCEPTANCE_CLIENT_CERTIFICATE WEFT_SESSION_ORGANIZATION_ID \
  WEFT_ENTRA_TENANT_ID WEFT_ENTRA_CLIENT_ID WEFT_ACCEPTANCE_SUBJECT WEFT_ACCEPTANCE_OBJECT_ID WEFT_ACCEPTANCE_ISSUER_PEM \
  WEFT_ACCEPTANCE_INGRESS_PEM WEFT_ACCEPTANCE_TRUST_PEM WEFT_ACCEPTANCE_SIGNING_KEY
# The service's build identity is the digest of the exact worker binary recorded in its image.
worker_digest=$(docker image inspect --format '{{index .Config.Labels "io.weft.ferrule.worker-sha256"}}' "$WEFT_FERRULE_SERVICE_IMAGE")
[[ $worker_digest =~ ^[0-9a-f]{64}$ ]] || fail 'service image identity label'
WEFT_FERRULE_SERVICE_IDENTITY=sha256:$worker_digest
# Measure the trusted local image, including its installed compiler implementation. This is
# not an automatic adoption of an unknown remote replica's advertised identity. The browser run
# checks the build's provenance against the combined identity recorded here.
docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
  --label "com.docker.compose.project=$project" --tmpfs /tmp:size=256m,mode=1777 \
  --memory 3g --pids-limit 256 "$WEFT_FERRULE_SERVICE_IMAGE" identity >"$work/compiler-identity.json"
WEFT_FERRULE_COMPILE_TOOLCHAIN=$(node -e '
  const fs = require("node:fs");
  const value = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
  const pin = /^sha256:[a-f0-9]{64}$/;
  if (value.schema !== "weft.compiler-image-identity.v1" || Object.keys(value).length !== 4 ||
    value.native !== process.argv[2] || !pin.test(value.toolchain) || !pin.test(value.build) ||
    value.build === value.native) process.exit(1);
  process.stdout.write(value.toolchain);
' "$work/compiler-identity.json" "$WEFT_FERRULE_SERVICE_IDENTITY") || fail 'service compiler identity'
export WEFT_FERRULE_COMPILE_TOOLCHAIN
WEFT_FERRULE_SERVICE_PEM=$(new_pem weft-ferrule-service)
WEFT_FERRULE_SERVICE_CLIENT_PEM=$(new_pem weft-ferrule-service-client)
WEFT_RUNS_FERRULE_SERVICE_CLIENT_PEM=$(new_pem weft-ferrule-runs-client)
WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256=$(pin weft-ferrule-service)
WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256=$(pin weft-ferrule-service-client)
WEFT_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256=$(pin weft-ferrule-runs-client)
export WEFT_FERRULE_SERVICE_IDENTITY WEFT_FERRULE_SERVICE_PEM WEFT_FERRULE_SERVICE_CLIENT_PEM \
  WEFT_RUNS_FERRULE_SERVICE_CLIENT_PEM WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256 \
  WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256 WEFT_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256
export WEFT_FERRULE_SERVICE_REPLICAS=${WEFT_FERRULE_SERVICE_REPLICAS:-2}
WEFT_CHAT_PROFILES_JSON=$(printf '{"version":1,"retentionDays":7,"turnSeconds":180,"maxOutputTokens":512,"profiles":[{"id":"local-acceptance","version":1,"displayName":"Local acceptance model","provider":"ollama","dialect":"ollamaChatNdjson","model":"%s","endpoint":"http://127.0.0.1:11434/api/chat","loopbackFixture":true,"requestThinkFalse":true,"credentialReference":null,"capabilities":["text","functionTools"],"metadataPolicy":{"id":"mapping-metadata","version":1,"mappingMetadataAllowed":true}}],"credentials":[]}' \
  "$WEFT_ACCEPTANCE_CHAT_MODEL")
export WEFT_CHAT_PROFILES_JSON

compose config --quiet || fail 'acceptance stack configuration'
networks=$(compose config --networks) || fail 'acceptance stack networks'
files+=(--file "$work/subnets.yml")
runtime_services=(server edge ferrule runs server-relay runs-relay issuer ingress model)
echo "Starting $project from new volumes"
start_on_test_subnets "$work/subnets.yml" "$work/up.log" "$networks" --detach --wait --wait-timeout 300 "${runtime_services[@]}" ||
  { tail -n 20 "$work/up.log" >&2; compose logs --no-color 2>&1 | tail -n 60 >&2; fail 'stack start'; }
for service in migrate blob-container storage-init session-keys; do
  [[ $(docker inspect --format '{{.State.ExitCode}}' "$(compose ps --all --quiet "$service")") == 0 ]] ||
    fail "$service exit status"
done
echo 'ok - migration, Blob container, Blob store and key-ring steps completed'
if [[ ${WEFT_ACCEPTANCE_MODEL_FIXTURE:-false} != true ]]; then
  compose exec -T model ollama list | grep -q -F "$WEFT_ACCEPTANCE_CHAT_MODEL" ||
    fail "chat model $WEFT_ACCEPTANCE_CHAT_MODEL is not in $WEFT_ACCEPTANCE_OLLAMA_VOLUME"
fi

# No container of the stack receives the Docker socket.
for container in $(compose ps --all --quiet); do
  if docker inspect --format '{{range .Mounts}}{{.Source}} {{end}}' "$container" | grep -q 'docker.sock'; then
    fail 'a stack container mounts the Docker socket'
  fi
done
echo 'ok - no container mounts the Docker socket'

# Owner preparation for the one signed-in user: the organization, an active principal with
# organization admission, the exact external identity binding and project creation permission.
# Nothing here grants a project role; creating a project makes the user its administrator.
principal=$(cat /proc/sys/kernel/random/uuid)
owner() {
  compose run --rm --no-deps -T "$@" >"$work/owner.out" 2>&1 ||
    { cat "$work/owner.out" >&2; fail "owner command $*"; }
}
owner -e "WEFT_DEV_ORGANIZATION_ID=$WEFT_SESSION_ORGANIZATION_ID" -e "WEFT_DEV_PRINCIPAL_ID=$principal" \
  -e 'WEFT_DEV_ORGANIZATION_NAME=Acceptance organization' migrate database provision-development-identity
owner -e "WEFT_DELIVER_OWNER_INPUT={\"tenantId\":\"$WEFT_ENTRA_TENANT_ID\",\"objectId\":\"$WEFT_ACCEPTANCE_OBJECT_ID\"}" \
  migrate database bind-entra-user "$principal"
grep -q '"outcome":"created"' "$work/owner.out" || fail 'identity binding'
owner migrate database set-project-creation-capability "$WEFT_SESSION_ORGANIZATION_ID" "$principal" true
echo 'ok - one external identity bound to an admitted principal that may create projects'

https_port=$(compose port ingress 8443 | cut -d: -f2)
issuer_port=$(compose port issuer 443 | cut -d: -f2)
export WEFT_ACCEPTANCE_BASE_URL=https://$WEFT_PUBLIC_HOST
export WEFT_ACCEPTANCE_HOST_RULES="MAP $WEFT_PUBLIC_HOST:443 127.0.0.1:$https_port, MAP login.microsoftonline.com:443 127.0.0.1:$issuer_port, MAP * ~NOTFOUND"
WEFT_ACCEPTANCE_SPKI=$(spki issuer),$(spki ingress)
export WEFT_ACCEPTANCE_SPKI
export WEFT_ACCEPTANCE_COMPOSE_PROJECT=$project
WEFT_ACCEPTANCE_COMPOSE_FILES=$(printf '%s:' "$compose_directory"/compose.{yml,ferrule-service.yml,features.yml,acceptance.yml})
export WEFT_ACCEPTANCE_COMPOSE_FILES=${WEFT_ACCEPTANCE_COMPOSE_FILES%:}
if [[ ${WEFT_ACCEPTANCE_MODEL_FIXTURE:-false} == true ]]; then
  export WEFT_ACCEPTANCE_COMPOSE_FILES="$WEFT_ACCEPTANCE_COMPOSE_FILES:$compose_directory/compose.model-fixture.yml"
fi
export WEFT_ACCEPTANCE_COMPOSE_FILES="$WEFT_ACCEPTANCE_COMPOSE_FILES:$work/subnets.yml"
if [[ -n ${WEFT_API_BEARER_CLIENT_IDS:-} ]]; then
  export WEFT_ACCEPTANCE_COMPOSE_FILES="$WEFT_ACCEPTANCE_COMPOSE_FILES:$compose_directory/compose.bearer.yml"
fi
export WEFT_ACCEPTANCE_PRINCIPAL_ID=$principal
export WEFT_ACCEPTANCE_WORK=$work
export WEFT_ACCEPTANCE_FIXTURES=$repository/engines/ferrule-host/tests/fixtures

if [[ -n ${WEFT_ACCEPTANCE_HOLD:-} ]]; then
  # Debugging aid: keep the stack for manual browser runs until the release file appears.
  export -p | grep -E ' (WEFT_|DOCKER_CONFIG=)' >"$work/environment"
  chmod 0600 "$work/environment"
  echo "Holding $project: source $work/environment, then touch $work/release to clean up."
  until [[ -e $work/release ]]; do sleep 5; done
  acceptance_complete=true
  exit 0
fi

if [[ ${WEFT_ACCEPTANCE_STAGE_ONLY:-false} == true ]]; then
  echo "Running the standalone client stage $(basename -- "$WEFT_ACCEPTANCE_STAGE")"
  "$WEFT_ACCEPTANCE_STAGE" || fail 'standalone client stage'
else
  echo 'Running the browser flows'
  # An optional command prefix for the browser run, for example a virtual display or a lock.
  read -r -a wrapper <<<"${WEFT_ACCEPTANCE_BROWSER_WRAPPER:-}"
  (cd "$repository/apps/web" && "${wrapper[@]}" npx playwright test --config playwright.hosted-acceptance.config.ts) ||
    fail 'browser flows'
  # Each use of the application's own recovery controls on a slow host, the chat attempts the
  # local model needed for a turn with a tool call and verified citations, and the attempt that
  # completed the build.
  echo "ok - browser flows passed: $(cat "$work/acceptance-facts.json")"
fi

# The run's own evidence: sign-in went through the issuer without refusals, every service replica
# is still healthy (a replica with unconfirmed cleanup fails readiness) and nothing restarted on its own.
compose logs --no-color issuer >"$work/issuer.log" 2>&1
if [[ ${WEFT_ACCEPTANCE_STAGE_ONLY:-false} == true ]]; then
  grep -q 'issuer public-token' "$work/issuer.log" || fail 'no public-client token was redeemed at the issuer'
else
  grep -q 'issuer token' "$work/issuer.log" || fail 'no token was redeemed at the issuer'
fi
! grep -q 'issuer refused' "$work/issuer.log" || fail "issuer refusals: $(grep -c 'issuer refused' "$work/issuer.log")"
echo 'ok - every sign-in redeemed its code at the issuer without a refusal'
for container in $(compose ps --quiet ferrule); do
  [[ $(docker inspect --format '{{.State.Health.Status}}' "$container") == healthy ]] || fail 'service replica health'
done
echo 'ok - every Ferrule service replica is healthy'
project_id=$(cat "$work/project-id" 2>/dev/null) || fail 'the client run recorded no project'
compose run --rm --no-deps -T maintenance run-once "$WEFT_SESSION_ORGANIZATION_ID" "$project_id" \
  >"$work/maintenance.out" 2>"$work/maintenance.err" || { tail -n 5 "$work/maintenance.err" >&2; fail 'maintenance over the Blob store'; }
grep -q '"outcome":"finished","failure":null' "$work/maintenance.out" || fail 'maintenance outcome'
grep -q '"filesystemTraversalComplete":true' "$work/maintenance.out" || fail 'maintenance observation of the Blob store'
echo "ok - maintenance observed the project's Blob objects: $(grep -o '"publicationSlotsExamined":[0-9]*,"boundPublicationMatchesObserved":[0-9]*' "$work/maintenance.out")"

# An optional follow-on stage on this run's stack before cleanup, for example
# deploy/compose/endpoint-acceptance-test.sh. It inherits the run's environment, and its failure
# fails the run; the run's cleanup still removes the stack.
if [[ -n ${WEFT_ACCEPTANCE_STAGE:-} && ${WEFT_ACCEPTANCE_STAGE_ONLY:-false} != true ]]; then
  echo "Running the follow-on stage $(basename -- "$WEFT_ACCEPTANCE_STAGE")"
  "$WEFT_ACCEPTANCE_STAGE" || fail 'follow-on stage'
fi
acceptance_complete=true
