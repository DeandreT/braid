#!/usr/bin/env bash
# Builds the runtime images, starts the hosted-shape stack from new volumes, checks it through
# the edge, restarts it, and removes every container, network, volume and image tag it created.
# With WEFT_FERRULE_WORKER_IMAGE set, it also checks native examples through the Docker worker and
# the hosted inspection worker: mutual pinning and one exact native validation frame.
# Generated passwords and certificates stay in a private temporary directory and are never printed.
set -euo pipefail

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
compose_directory=$repository/deploy/compose
project=weft-smoke-$(openssl rand -hex 4)
work=$(mktemp -d "${TMPDIR:-/tmp}/weft-smoke.XXXXXXXX")
chmod 0700 "$work"
examples=false
[[ -n ${WEFT_FERRULE_WORKER_IMAGE:-} ]] && examples=true
built_images=()
files=(--file "$compose_directory/compose.yml")
[[ $examples == true ]] && files+=(--file "$compose_directory/compose.examples.yml" --file "$compose_directory/compose.worker.yml")

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
  compose down --volumes --remove-orphans --timeout 20 >/dev/null 2>&1 || cleanup_status=1
  [[ -z $(owned_objects) ]] || { echo 'Owned compose objects remain.' >&2; cleanup_status=1; }
  if ((${#built_images[@]} > 0)); then
    docker image rm "${built_images[@]}" >/dev/null 2>&1 || cleanup_status=1
  fi
  rm -rf -- "$work" || cleanup_status=1
  if ((cleanup_status != 0)); then
    echo 'Cleanup of owned resources failed.' >&2
    status=1
  fi
  if ((status == 0)); then echo 'Smoke test passed.'; else echo 'Smoke test failed.' >&2; fi
  exit "$status"
}
trap cleanup EXIT

fail() {
  printf 'FAILED: %s\n' "$1" >&2
  exit 1
}

if [[ -z ${WEFT_IMAGE:-} ]]; then
  for target in weft edge $([[ $examples == true ]] && echo weft-local-docker weft-worker); do
    echo "Building $target"
    docker build --quiet --file "$repository/deploy/container/Dockerfile" --target "$target" \
      --tag "$target:$project" "$repository" >/dev/null
    built_images+=("$target:$project")
  done
  export WEFT_IMAGE=weft:$project WEFT_EDGE_IMAGE=edge:$project WEFT_LOCAL_DOCKER_IMAGE=weft-local-docker:$project \
    WEFT_WORKER_IMAGE=weft-worker:$project
fi

for role in POSTGRES_ADMIN MIGRATOR RUNTIME INGEST CATALOG METADATA CREATOR REPORT DERIVATION MAINTENANCE \
  IDENTITY PAYLOAD_PERMISSION SESSION_READER SESSION_WRITER CHAT_READER CHAT_WRITER CHAT_LIFECYCLE \
  PREVIEW_READER PREVIEW_WRITER CASE_READER CASE_WRITER RUN_READER RUN_WRITER BUILD_READER BUILD_WRITER BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER BUILD_METADATA QUALIFICATION_READER QUALIFICATION_WRITER; do
  export "WEFT_LOCAL_${role}_PASSWORD=$(openssl rand -hex 24)"
done
new_pem() {
  openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 30 -subj "/CN=$1" \
    -keyout "$work/$1.key" -out "$work/$1.crt" 2>/dev/null
  cat "$work/$1.key" "$work/$1.crt"
}
WEFT_SESSION_KEY_PEM=$(new_pem weft-session-key)
WEFT_ENTRA_CLIENT_PEM=$(new_pem weft-entra-client)
export WEFT_SESSION_KEY_PEM WEFT_ENTRA_CLIENT_PEM
# Local placeholders: sign-in is not exercised without an actual Entra registration.
WEFT_SESSION_ORGANIZATION_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ENTRA_TENANT_ID=$(cat /proc/sys/kernel/random/uuid)
WEFT_ENTRA_CLIENT_ID=$(cat /proc/sys/kernel/random/uuid)
export WEFT_SESSION_ORGANIZATION_ID WEFT_ENTRA_TENANT_ID WEFT_ENTRA_CLIENT_ID
export WEFT_PUBLIC_HOST=weft.example.test WEFT_EDGE_TRUSTED_PROXIES=
export WEFT_COMPOSE_PORT=0 WEFT_COMPOSE_EXAMPLES_PORT=0
if [[ $examples == true ]]; then
  WEFT_DOCKER_GID=$(stat -c %g /var/run/docker.sock)
  export WEFT_DOCKER_GID
  # Each side of the inspection transport pins the other's certificate (SHA-256 of its DER encoding).
  pin() { openssl x509 -in "$work/$1.crt" -outform DER | openssl dgst -sha256 -r | cut -d' ' -f1; }
  WEFT_INSPECTION_WORKER_PEM=$(new_pem weft-inspection-worker)
  WEFT_INSPECTION_CLIENT_PEM=$(new_pem weft-inspection-client)
  WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256=$(pin weft-inspection-worker)
  WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256=$(pin weft-inspection-client)
  export WEFT_INSPECTION_WORKER_PEM WEFT_INSPECTION_CLIENT_PEM WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256 \
    WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256
fi

env -u WEFT_FERRULE_WORKER_IMAGE -u WEFT_DOCKER_GID docker compose --project-name "$project" \
  --file "$compose_directory/compose.yml" --profile operator config --quiet ||
  fail 'base stack configuration without the examples settings'
echo 'ok - base stack renders without the examples settings'

runtime_services=(server edge)
[[ $examples == true ]] && runtime_services+=(worker)
echo "Starting $project from new volumes"
compose up --detach --wait --wait-timeout 180 "${runtime_services[@]}" >/dev/null 2>"$work/up.log" ||
  { tail -n 20 "$work/up.log" >&2; compose logs --no-color migrate storage-init session-keys "${runtime_services[@]}" 2>&1 | tail -n 40 >&2; fail 'stack start'; }
base=http://$(compose port server 8080)

# Prints the status code; the body goes to $work/body.
request() {
  curl --silent --show-error --max-time "${request_seconds:-10}" --output "$work/body" --dump-header "$work/headers" \
    --write-out '%{http_code}' "$@"
}
expect() {
  local expected=$1 label=$2 status
  shift 2
  status=$(request "$@") || fail "$label (transport)"
  [[ $status == "$expected" ]] || fail "$label returned $status, expected $expected"
  echo "ok - $label"
}
body_has() { grep -q -- "$1" "$work/body" || fail "$2"; }

check_session_profile() {
  expect 200 'health through the edge' "$base/api/v1/health"
  body_has '"status":"ok"' 'health body'
  grep -qi '^strict-transport-security: max-age=31536000' "$work/headers" || fail 'HSTS header'
  expect 200 'examples page from the same origin' "$base/examples"
  body_has '<weft-root>' 'Angular shell'
  script=$(grep -o 'src="main-[A-Za-z0-9_-]*\.js"' "$work/body" | head -n 1 | cut -d'"' -f2)
  [[ -n $script ]] || fail 'Angular entry script reference'
  expect 200 'Angular entry script' "$base/$script"
  expect 200 'session status' --header 'X-Weft-Session: 1' "$base/api/auth/session"
  body_has '"mode":"session"' 'session mode'
  body_has '"state":"anonymous"' 'anonymous session state'
  expect 401 'catalog refuses anonymous access' "$base/api/v1/catalog/context"
  expect 404 'Development examples API is not mapped' "$base/api/v1/examples/mappings"
  expect 200 'client forwarding fields are replaced by the edge' --header 'Forwarded: for=192.0.2.1' \
    --header 'X-Forwarded-For: 192.0.2.1' --header 'X-Forwarded-Host: attacker.example' \
    --header 'X-Forwarded-Port: 80' --header 'X-Original-URL: /admin' "$base/api/v1/health"
}

# Big-endian 32-bit frame length, written directly so NUL bytes survive.
be32() { printf "$(printf '\\x%02x\\x%02x\\x%02x\\x%02x' $(($1 >> 24 & 255)) $(($1 >> 16 & 255)) $(($1 >> 8 & 255)) $(($1 & 255)))"; }

# The Session server reaches the worker only on the internal network; the host checks the same listener.
check_inspection_worker() {
  local frame=$1 container ip url key pinned status native header uuid
  container=$(compose ps --quiet worker)
  [[ -n $container && $(docker inspect --format '{{.State.Running}} {{.RestartCount}}' "$container") == 'true 0' ]] ||
    { compose logs --no-color worker 2>&1 | tail -n 20 >&2; fail 'hosted inspection worker running'; }
  ip=$(docker inspect --format "{{with index .NetworkSettings.Networks \"${project}_inspection\"}}{{.IPAddress}}{{end}}" "$container")
  [[ -n $ip ]] || fail 'worker address on the inspection network'
  url=https://$ip:8443
  key=$(openssl x509 -in "$work/weft-inspection-worker.crt" -pubkey -noout | openssl pkey -pubin -outform DER |
    openssl dgst -sha256 -binary | base64)
  pinned=(--insecure --pinnedpubkey "sha256//$key")
  local client=(--cert "$work/weft-inspection-client.crt" --key "$work/weft-inspection-client.key")
  expect 204 'worker admits the pinned server client' "${pinned[@]}" "${client[@]}" "$url/v1/inspection/ready"
  grep -qi "^weft-worker-image: $WEFT_FERRULE_WORKER_IMAGE" "$work/headers" || fail 'worker image identity'
  for refused in none other; do
    local identity=()
    [[ $refused == other ]] && identity=(--cert "$work/weft-entra-client.crt" --key "$work/weft-entra-client.key")
    if status=$(request "${pinned[@]}" "${identity[@]}" "$url/v1/inspection/ready" 2>/dev/null); then
      [[ $status != 204 ]] || fail "worker admitted a client with $refused certificate"
    fi
  done
  echo 'ok - worker refuses clients without the pinned certificate'
  [[ $frame == frame ]] || return 0
  native=$repository/engines/ferrule-host/tests/fixtures/valid-basic.json
  uuid() { cat /proc/sys/kernel/random/uuid; }
  header=$(printf '{"protocolVersion":"weft.ferrule-host.v1","requestId":"%s","operation":"validate","revision":{"organizationId":"%s","projectId":"%s","mappingId":"%s","revisionId":"%s"},"nativeDigest":"%s","documentId":"entry.native.json","limits":{"maximumDiagnostics":128,"maximumItems":8192,"maximumTextBytes":2048}}' \
    "$(uuid)" "$(uuid)" "$(uuid)" "$(uuid)" "$(uuid)" "$(sha256sum "$native" | cut -d' ' -f1)")
  { be32 "${#header}"; printf '%s' "$header"; be32 "$(stat -c %s "$native")"; cat "$native"; } >"$work/frame"
  request_seconds=60 expect 200 'native validation frame through the hosted worker' "${pinned[@]}" "${client[@]}" \
    --header 'Content-Type: application/octet-stream' --header "Weft-Worker-Image: $WEFT_FERRULE_WORKER_IMAGE" \
    --data-binary "@$work/frame" "$url/v1/inspection/operations/$(openssl rand -hex 16)"
  grep -qi '^weft-worker-result: none' "$work/headers" || fail "worker result $(grep -i '^weft-worker-result' "$work/headers")"
  grep -q '"isValid":true' "$work/body" || fail 'worker validation result'
}

check_session_profile
[[ $examples == true ]] && check_inspection_worker frame
for service in migrate storage-init session-keys; do
  [[ $(docker inspect --format '{{.State.ExitCode}}' "$(compose ps --all --quiet "$service")") == 0 ]] ||
    fail "$service exit status"
done
echo 'ok - one-shot migration, storage and key-ring steps completed'

# Maintenance opens the private storage root from a separate container before any project work.
maintenance_status=0
compose run --rm --no-deps maintenance run-once "$WEFT_SESSION_ORGANIZATION_ID" \
  "$(cat /proc/sys/kernel/random/uuid)" /var/lib/weft/storage/root /app/server \
  >"$work/maintenance.out" 2>"$work/maintenance.err" || maintenance_status=$?
grep -q -E 'Storage|Unsafe|InvalidConfiguration|Usage' "$work/maintenance.err" &&
  fail "maintenance could not open the shared storage root (exit $maintenance_status)"
((maintenance_status == 0)) || fail "maintenance exit $maintenance_status"
echo "ok - maintenance opened the shared storage root: $(head -c 200 "$work/maintenance.out" | tr -d '\n')"

echo 'Restarting the replica'
compose stop --timeout 20 edge server >/dev/null 2>&1
compose up --detach --wait --wait-timeout 180 server edge >/dev/null 2>"$work/up.log" ||
  { tail -n 20 "$work/up.log" >&2; fail 'stack restart'; }
base=http://$(compose port server 8080)
check_session_profile
echo 'ok - restart reopened the persistent key ring and storage configuration'
if [[ $examples == true ]]; then
  # The restarted server admitted its native settings again; the worker keeps its pinned admission.
  check_inspection_worker admission
fi

if [[ $examples == true ]]; then
  compose up --detach --wait --wait-timeout 120 examples examples-edge >/dev/null 2>"$work/up.log" ||
    { tail -n 20 "$work/up.log" >&2; compose logs --no-color examples 2>&1 | tail -n 40 >&2; fail 'examples start'; }
  examples_base=http://$(compose port examples 8080)
  expect 200 'examples health' "$examples_base/api/v1/health"
  expect 200 'examples page' "$examples_base/examples"
  body_has '<weft-root>' 'examples Angular shell'
  expect 200 'Disabled status' --header 'X-Weft-Session: 1' "$examples_base/api/auth/session"
  body_has '"mode":"disabled"' 'disabled mode'
  expect 200 'example list' "$examples_base/api/v1/examples/mappings"
  body_has '"basic"' 'basic example listed'
  # Projection and validation each run one worker within its own ten-second wall deadline.
  request_seconds=60 expect 200 'native basic example through the Docker worker' "$examples_base/api/v1/examples/mappings/basic"
  outcomes=$(grep -o '"\(projection\|validation\)":{"status":"[A-Za-z]*"\(,"failure":"[A-Za-z]*"\)\?' "$work/body" | tr '\n' ' ')
  body_has '"projection":{"status":"succeeded"' "native projection by the worker: $outcomes"
  body_has '"validation":{"status":"succeeded"' "native validation by the worker: $outcomes"
fi
