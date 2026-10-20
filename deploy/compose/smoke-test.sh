#!/usr/bin/env bash
# Builds the runtime images, starts the hosted-shape stack from new volumes, checks it through
# the edge, restarts it, and removes every container, network, volume and image tag it created.
# With WEFT_FERRULE_WORKER_IMAGE set, it also checks native examples through the Docker worker.
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
[[ $examples == true ]] && files+=(--file "$compose_directory/compose.examples.yml")

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
  for target in weft edge $([[ $examples == true ]] && echo weft-local-docker); do
    echo "Building $target"
    docker build --quiet --file "$repository/deploy/container/Dockerfile" --target "$target" \
      --tag "$target:$project" "$repository" >/dev/null
    built_images+=("$target:$project")
  done
  export WEFT_IMAGE=weft:$project WEFT_EDGE_IMAGE=edge:$project WEFT_LOCAL_DOCKER_IMAGE=weft-local-docker:$project
fi

for role in POSTGRES_ADMIN MIGRATOR RUNTIME INGEST CATALOG METADATA CREATOR REPORT DERIVATION MAINTENANCE \
  IDENTITY PAYLOAD_PERMISSION SESSION_READER SESSION_WRITER CHAT_READER CHAT_WRITER CHAT_LIFECYCLE \
  PREVIEW_READER PREVIEW_WRITER; do
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
fi

env -u WEFT_FERRULE_WORKER_IMAGE -u WEFT_DOCKER_GID docker compose --project-name "$project" \
  --file "$compose_directory/compose.yml" --profile operator config --quiet ||
  fail 'base stack configuration without the examples settings'
echo 'ok - base stack renders without the examples settings'

echo "Starting $project from new volumes"
compose up --detach --wait --wait-timeout 180 server edge >/dev/null 2>"$work/up.log" ||
  { tail -n 20 "$work/up.log" >&2; compose logs --no-color migrate storage-init session-keys server edge 2>&1 | tail -n 40 >&2; fail 'stack start'; }
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

check_session_profile
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
