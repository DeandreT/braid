#!/usr/bin/env bash
# Local acceptance of the packaged mapping endpoint (docs/hosted-acceptance.md). It runs the hosted
# acceptance (acceptance-test.sh) with this script as its follow-on stage. The stage takes the build the
# browser flows completed, qualifies it and activates a release with the release tool, grants one machine
# caller with Braid.DatabaseTool, and starts the endpoint from compose.endpoint.yml. BRAID_ENDPOINT_ACCEPTANCE_RUNTIME
# selects its compiled runtime: service (the default, the hosted shape through the run's Ferrule service, with
# no Docker socket) or docker (local only). It then calls the endpoint with app-only tokens from the run's
# issuer: a call, a retry under the same request ID, a new activation and a rollback, expired and
# wrong-audience tokens, and a revoked grant. The stage removes the endpoint, its images and every
# compiled-runtime container before the hosted acceptance's own cleanup, which fails on any remaining owned object.
# Generated secrets and tokens stay in private files and are never printed.
set -euo pipefail

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
compose_directory=$repository/deploy/compose

if [[ ${BRAID_ENDPOINT_ACCEPTANCE_RUN:-} != stage ]]; then
  export BRAID_ENDPOINT_ACCEPTANCE_RUNTIME=${BRAID_ENDPOINT_ACCEPTANCE_RUNTIME:-service}
  [[ $BRAID_ENDPOINT_ACCEPTANCE_RUNTIME =~ ^(service|docker)$ ]] ||
    { echo 'FAILED: BRAID_ENDPOINT_ACCEPTANCE_RUNTIME must be service or docker' >&2; exit 1; }
  # The run's issuer registers two machine clients: the endpoint's caller, and one whose tokens expire at once.
  uuid() { cat /proc/sys/kernel/random/uuid; }
  digest() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
  BRAID_ENDPOINT_ACCEPTANCE_CALLER_CLIENT=$(uuid)
  BRAID_ENDPOINT_ACCEPTANCE_CALLER_OBJECT=$(uuid)
  BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET=$(openssl rand -hex 32)
  BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_CLIENT=$(uuid)
  BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_SECRET=$(openssl rand -hex 32)
  BRAID_ACCEPTANCE_MACHINE_CLIENTS=$(printf '[{"clientId":"%s","objectId":"%s","secretSha256":"%s","lifetimeSeconds":3600},{"clientId":"%s","objectId":"%s","secretSha256":"%s","lifetimeSeconds":2}]' \
    "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_CLIENT" "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_OBJECT" "$(digest "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET")" \
    "$BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_CLIENT" "$(uuid)" "$(digest "$BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_SECRET")")
  # The endpoint's own app registration: the audience its callers request.
  BRAID_ENDPOINT_ENTRA_AUDIENCE=$(uuid)
  export BRAID_ENDPOINT_ACCEPTANCE_CALLER_CLIENT BRAID_ENDPOINT_ACCEPTANCE_CALLER_OBJECT BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET \
    BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_CLIENT BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_SECRET BRAID_ACCEPTANCE_MACHINE_CLIENTS \
    BRAID_ENDPOINT_ENTRA_AUDIENCE
  export BRAID_ENDPOINT_ACCEPTANCE_RUN=stage BRAID_ACCEPTANCE_STAGE=$compose_directory/endpoint-acceptance-test.sh
  exec "$compose_directory/acceptance-test.sh"
fi

# shellcheck source=acceptance-lifecycle.sh
source "$compose_directory/acceptance-lifecycle.sh"

# The follow-on stage, inside the hosted acceptance run: its stack, environment and private directory.
for setting in BRAID_ACCEPTANCE_WORK BRAID_ACCEPTANCE_COMPOSE_PROJECT BRAID_ACCEPTANCE_COMPOSE_FILES BRAID_ACCEPTANCE_PRINCIPAL_ID \
  BRAID_SESSION_ORGANIZATION_ID BRAID_ENTRA_TENANT_ID BRAID_ENTRA_CLIENT_ID BRAID_FERRULE_SERVICE_IDENTITY BRAID_FERRULE_COMPILE_TOOLCHAIN \
  BRAID_ACCEPTANCE_TRUST_PEM BRAID_ENDPOINT_ENTRA_AUDIENCE BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET BRAID_ENDPOINT_ACCEPTANCE_RUNTIME \
  BRAID_FERRULE_SERVICE_IMAGE BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256 BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256; do
  [[ -n ${!setting:-} ]] || { echo "FAILED: the endpoint stage requires $setting from the hosted acceptance run" >&2; exit 1; }
done
project=$BRAID_ACCEPTANCE_COMPOSE_PROJECT
principal=$BRAID_ACCEPTANCE_PRINCIPAL_ID
runtime=$BRAID_ENDPOINT_ACCEPTANCE_RUNTIME
work=$(mktemp -d "$BRAID_ACCEPTANCE_WORK/endpoint.XXXXXXXX")
files=()
IFS=: read -r -a acceptance_files <<<"$BRAID_ACCEPTANCE_COMPOSE_FILES"
for file in "${acceptance_files[@]}" "$compose_directory/compose.endpoint.yml" "$compose_directory/compose.endpoint-$runtime.yml" \
  "$compose_directory/compose.endpoint-acceptance.yml"; do
  files+=(--file "$file")
done
built_images=()
runtime_image=
# shellcheck source=endpoint-settlement.sh
source "$compose_directory/endpoint-settlement.sh"

compose() {
  docker compose --project-name "$project" "${files[@]}" --profile operator "$@"
}

# Every step is attempted. A failed step fails the stage, and so does any runtime container that had to
# be removed here or earlier: settlement keeps its evidence but never turns the run into a pass.
cleanup() {
  local status=$? cleanup_status=0
  set +e
  acceptance_result "$status"
  status=$?
  if ((status != 0)) && [[ -n ${BRAID_ACCEPTANCE_LOG_DIRECTORY:-} ]]; then
    # The endpoint and the tools write fixed status lines only.
    mkdir -p "$BRAID_ACCEPTANCE_LOG_DIRECTORY/$project" &&
      compose logs --no-color --timestamps endpoint >"$BRAID_ACCEPTANCE_LOG_DIRECTORY/$project/endpoint.log" 2>&1
  fi
  if [[ -n $(compose ps --all --quiet endpoint 2>/dev/null) ]]; then
    compose rm --stop --force endpoint >/dev/null 2>&1 || cleanup_status=1
  fi
  if [[ -n $runtime_image ]]; then
    settle "$project" cleanup || cleanup_status=1
  fi
  if ((${#built_images[@]} > 0)); then
    docker image rm "${built_images[@]}" >/dev/null 2>&1 || cleanup_status=1
  fi
  ((cleanup_status == 0)) || { echo 'Endpoint stage cleanup failed.' >&2; status=1; }
  settlement_status || status=1
  exit "$status"
}
trap cleanup EXIT

fail() {
  printf 'FAILED: %s\n' "$1" >&2
  exit 1
}

build() {
  local tag=$1
  shift
  echo "Building $tag"
  docker build --quiet --tag "$tag" "$@" >/dev/null
  built_images+=("$tag")
}

if [[ $runtime == service ]]; then
  # The hosted shape: the endpoint image without a Docker CLI, executing through the run's Ferrule service.
  if [[ -z ${BRAID_ENDPOINT_IMAGE:-} ]]; then
    build "braid-mapping-endpoint:$project" --file "$repository/deploy/container/Dockerfile" --target braid-mapping-endpoint "$repository"
    export BRAID_ENDPOINT_IMAGE=braid-mapping-endpoint:$project
  fi
  # The execution identity the target registers, measured from the trusted local service image.
  docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
    --label "com.docker.compose.project=$project" --tmpfs /tmp:size=256m,mode=1777 \
    --memory 3g --pids-limit 256 "$BRAID_FERRULE_SERVICE_IMAGE" runtime-identity >"$work/runtime-identity.json" ||
    fail 'service runtime identity'
  BRAID_COMPILED_RUNTIME_IMAGE=$(bun -e '
    const value = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
    const pin = /^sha256:[0-9a-f]{64}$/;
    if (value.schema !== "braid.compiled-runtime-image-identity.v1" || value.native !== process.argv[2] || !pin.test(value.runtime))
      process.exit(1);
    process.stdout.write(value.runtime);
  ' "$work/runtime-identity.json" "$BRAID_FERRULE_SERVICE_IDENTITY") || fail 'service runtime identity'
  # The endpoint's own client certificate, which the service admits beside its other clients.
  openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 -subj '/CN=braid-endpoint-ferrule-client' \
    -keyout "$work/endpoint-client.key" -out "$work/endpoint-client.crt" 2>/dev/null
  BRAID_ENDPOINT_FERRULE_SERVICE_CLIENT_PEM=$(cat "$work/endpoint-client.key" "$work/endpoint-client.crt")
  endpoint_pin=$(openssl x509 -in "$work/endpoint-client.crt" -outform DER | openssl dgst -sha256 -r | cut -d' ' -f1)
  BRAID_ENDPOINT_SERVICE_CLIENT_PINS=$BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256${BRAID_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256:+,$BRAID_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256},$endpoint_pin
  export BRAID_COMPILED_RUNTIME_IMAGE BRAID_ENDPOINT_FERRULE_SERVICE_CLIENT_PEM BRAID_ENDPOINT_SERVICE_CLIENT_PINS
else
  # The local profile: the endpoint image with the Docker CLI, and the compiled runtime image its target names.
  if [[ -z ${BRAID_ENDPOINT_IMAGE:-} ]]; then
    build "braid-mapping-endpoint-local-docker:$project" --file "$repository/deploy/container/Dockerfile" \
      --target braid-mapping-endpoint-local-docker "$repository"
    export BRAID_ENDPOINT_IMAGE=braid-mapping-endpoint-local-docker:$project
  fi
  if [[ -z ${BRAID_COMPILED_RUNTIME_IMAGE:-} ]]; then
    native=${BRAID_FERRULE_NATIVE_IMAGE:-braid-ferrule-native:$project}
    docker image inspect "$native" >/dev/null 2>&1 ||
      fail 'set BRAID_FERRULE_NATIVE_IMAGE or BRAID_COMPILED_RUNTIME_IMAGE when the service image is supplied'
    container=$(docker create "$native")
    docker cp "$container:/ferrule-host" "$work/ferrule-host" >/dev/null
    docker rm "$container" >/dev/null
    native_digest=$(sha256sum "$work/ferrule-host" | cut -d' ' -f1)
    rm -f "$work/ferrule-host"
    build "braid-compiled-runtime-base:$project" --file "$repository/engines/build-worker/Dockerfile" \
      --build-arg "BRAID_NATIVE_IMAGE=$native" --build-arg "BRAID_NATIVE_BINARY_SHA256=$native_digest" "$repository"
    base=braid-compiled-runtime-base:$project
  else
    base=$BRAID_COMPILED_RUNTIME_IMAGE
  fi
  # A reproducible build can share its image ID with other users of the same sources. The stage registers a
  # run-unique child image instead, which differs only by a label. Its runtime containers inherit that label,
  # and only containers carrying it are ever checked or removed.
  build "braid-compiled-runtime:$project" - <<<"$(printf 'FROM %s\nLABEL io.braid.acceptance.run=%s\n' "$base" "$project")"
  BRAID_COMPILED_RUNTIME_IMAGE=$(docker image inspect --format '{{.Id}}' "braid-compiled-runtime:$project")
  [[ $BRAID_COMPILED_RUNTIME_IMAGE =~ ^sha256:[0-9a-f]{64}$ ]] || fail 'the compiled runtime image ID'
  runtime_image=$BRAID_COMPILED_RUNTIME_IMAGE
  export BRAID_COMPILED_RUNTIME_IMAGE
  [[ -z $(runtime_containers "$project") ]] || fail 'compiled-runtime containers exist before the stage'
fi

# The build the browser flows completed, read from the database only to name it.
sql() {
  compose exec -T postgres psql -U postgres -d braid -At -c "$1"
}
mapfile -t builds < <(sql "SELECT a.project_id||' '||a.mapping_id||' '||a.build_id||' '||a.environment_id||' '||a.environment_version||' '||r.artifact_digest
  FROM braid.build_runs r JOIN braid.build_approvals a USING (organization_id,build_id)
  WHERE r.state='succeeded' AND r.organization_id='$BRAID_SESSION_ORGANIZATION_ID' AND r.actor_id='$principal'")
((${#builds[@]} == 1)) || fail "expected one completed build, found ${#builds[@]}"
read -r project_id mapping_id build_id environment_id environment_version artifact_digest <<<"${builds[0]}"
# The builds compiled through the Ferrule service name its combined compiler identity, which the run
# also measured from the service image before the stack started.
BRAID_ENDPOINT_COMPILER_IDENTITY=sha256:$(printf 'braid.compiler-identity.v1\nnative=%s\ntoolchain=%s\n' \
  "$BRAID_FERRULE_SERVICE_IDENTITY" "$BRAID_FERRULE_COMPILE_TOOLCHAIN" | sha256sum | cut -d' ' -f1)
[[ $(bun -e 'process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).build))' \
  "$BRAID_ACCEPTANCE_WORK/compiler-identity.json") == "$BRAID_ENDPOINT_COMPILER_IDENTITY" ]] ||
  fail 'the measured compiler identity differs from the combined service identity'
[[ $(sql "SELECT count(*) FROM braid.build_approvals WHERE organization_id='$BRAID_SESSION_ORGANIZATION_ID' AND build_id='$build_id'
  AND position('$BRAID_ENDPOINT_COMPILER_IDENTITY' in convert_from(intent_body,'UTF8'))>0") == 1 ]] ||
  fail 'the completed build does not name the service compiler identity'
echo "ok - completed build $build_id with artifact $artifact_digest"

uuid() { cat /proc/sys/kernel/random/uuid; }
target_id=$(uuid)
export BRAID_ENDPOINT_COMPILER_IDENTITY BRAID_ENDPOINT_ORGANIZATION_ID=$BRAID_SESSION_ORGANIZATION_ID BRAID_ENDPOINT_PROJECT_ID=$project_id \
  BRAID_ENDPOINT_MAPPING_ID=$mapping_id BRAID_ENDPOINT_ENVIRONMENT_ID=$environment_id \
  BRAID_ENDPOINT_ENVIRONMENT_VERSION=$environment_version BRAID_ENDPOINT_TARGET_ID=$target_id \
  BRAID_ENDPOINT_ENTRA_TENANT_ID=$BRAID_ENTRA_TENANT_ID BRAID_ENDPOINT_PORT=0 BRAID_ENDPOINT_CONCURRENCY=2
BRAID_DOCKER_GID=$(stat -c %g /var/run/docker.sock)
export BRAID_DOCKER_GID
compose config --quiet || fail 'endpoint stack configuration'
if [[ $runtime == service ]]; then
  # Every service replica restarts with the execute profile enabled and the endpoint's pin admitted.
  compose up --detach --wait --wait-timeout 300 --no-deps --force-recreate ferrule >/dev/null 2>"$work/ferrule.log" ||
    { tail -n 20 "$work/ferrule.log" >&2; fail 'Ferrule service with the execute profile'; }
  echo 'ok - the Ferrule service replicas restarted healthy with the execute profile'
fi

# One operator command as the signed-in principal; its single JSON line is kept for the checks.
operator() {
  local service=$1 output=$2
  shift 2
  compose run --rm --no-deps -T "$service" "$@" >"$work/$output.json" 2>"$work/$output.err" ||
    { cat "$work/$output.json" "$work/$output.err" >&2; fail "$service $1"; }
}
field() {
  bun -e 'process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))[process.argv[2]]))' \
    "$work/$1.json" "$2"
}
expect() {
  [[ $(field "$1" outcome) == "$2" ]] || fail "$1 outcome $(field "$1" outcome)"
}
scope=("$principal" "$BRAID_SESSION_ORGANIZATION_ID" "$project_id" "$mapping_id")
target=("${scope[@]}" "$environment_id" "$environment_version" "$target_id")

# Qualification replays the build's saved regression evidence through the compiled runtime. Only an
# unavailable runtime that left no container behind is tried again with the same qualification ID; any
# other failure, including unconfirmed runtime cleanup, fails the run after settling its containers.
qualification=$(uuid)
for attempt in 1 2 3; do
  if compose run --rm --no-deps -T endpoint-release qualify-build "${scope[@]}" "$build_id" "$qualification" \
    >"$work/qualify.json" 2>"$work/qualify.err"; then
    break
  fi
  if ((attempt < 3)) && [[ $(field qualify outcome 2>/dev/null) == unavailable && -z $(runtime_containers "$project") ]]; then
    continue
  fi
  cat "$work/qualify.json" "$work/qualify.err" >&2
  settle "$project" 'failed qualification attempt' || true
  fail 'qualify-build'
done
expect qualify qualified
[[ $(field qualify artifactSha256) == "$artifact_digest" && $(field qualify runtimeImage) == "$BRAID_COMPILED_RUNTIME_IMAGE" ]] ||
  fail 'qualification provenance'
operator endpoint-release register register-release-target "${target[@]}"
expect register registered
first_release=$(uuid)
operator endpoint-release activate-first activate-release "${target[@]}" "$first_release" "$qualification" none
expect activate-first activated
grant=$(uuid)
operator endpoint-grants grant grant-machine-invocation "${target[@]}" "$grant" "$BRAID_ENTRA_TENANT_ID" \
  "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_OBJECT"
expect grant granted
echo 'ok - the build qualified in the compiled runtime; target registered, release activated, one caller granted'

echo 'Starting the endpoint'
compose up --detach --wait --wait-timeout 300 --no-deps endpoint >/dev/null 2>"$work/up.log" ||
  { tail -n 20 "$work/up.log" >&2; compose logs --no-color endpoint 2>&1 | tail -n 20 >&2; fail 'endpoint start'; }
endpoint=$(compose ps --quiet endpoint)
[[ $(docker inspect --format '{{.Config.User}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.HostConfig.SecurityOpt}}' "$endpoint") == \
  '1654:1654 true [ALL] [no-new-privileges:true]' ]] || fail 'endpoint container hardening'
[[ $(docker inspect --format '{{.State.Health.Status}}' "$endpoint") == healthy ]] || fail 'endpoint health'
compose logs --no-color endpoint >"$work/endpoint.log" 2>&1
grep -q 'BraidMappingEndpointReady' "$work/endpoint.log" || fail 'endpoint readiness line'
if [[ $runtime == service ]]; then
  # The hosted shape: no container of the stack, the endpoint included, receives the Docker socket.
  for container in $(compose ps --all --quiet); do
    if docker inspect --format '{{range .Mounts}}{{.Source}} {{end}}' "$container" | grep -q 'docker.sock'; then
      fail 'a stack container mounts the Docker socket'
    fi
  done
  echo 'ok - no container mounts the Docker socket'
fi
port=$(compose port endpoint 8080 | cut -d: -f2)
echo 'ok - the endpoint is healthy, non-root, read-only and holds no capability'

# App-only tokens from the run's issuer through its client-credentials grant, kept as private header files.
printf '%s\n' "$BRAID_ACCEPTANCE_TRUST_PEM" >"$work/trust.pem"
issuer_port=$(compose port issuer 443 | cut -d: -f2)
token() {
  local name=$1 client=$2 secret=$3 resource=$4
  printf '%s' "$secret" >"$work/$name.secret"
  curl --silent --show-error --fail --max-time 60 --cacert "$work/trust.pem" \
    --connect-to "login.microsoftonline.com:443:127.0.0.1:$issuer_port" \
    --data-urlencode grant_type=client_credentials --data-urlencode "client_id=$client" \
    --data-urlencode "client_secret@$work/$name.secret" --data-urlencode "scope=api://$resource/.default" \
    --output "$work/$name.token" "https://login.microsoftonline.com/$BRAID_ENTRA_TENANT_ID/oauth2/v2.0/token" ||
    fail "token for $name"
  bun -e '
    const fs = require("fs");
    const token = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
    if (token.token_type !== "Bearer" || typeof token.access_token !== "string") process.exit(1);
    fs.writeFileSync(process.argv[2], `Authorization: Bearer ${token.access_token}\n`, { mode: 0o600 });
  ' "$work/$name.token" "$work/$name.header" || fail "token response for $name"
  rm -f "$work/$name.secret" "$work/$name.token"
}
token caller "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_CLIENT" "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET" "$BRAID_ENDPOINT_ENTRA_AUDIENCE"
# The same caller's token for another resource: the Session application's registration.
token foreign "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_CLIENT" "$BRAID_ENDPOINT_ACCEPTANCE_CALLER_SECRET" "$BRAID_ENTRA_CLIENT_ID"

# The browser run's regression inputs: the primary order, the named rates document and both parameters.
bun -e '
  const b64 = (text) => Buffer.from(text, "utf8").toString("base64");
  process.stdout.write(JSON.stringify({
    inputs: [
      { content: b64("{\"id\":\"SO-1\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":9007199254740993}]}") },
      { name: "rates", content: b64("{\"currency\":\"EUR\"}") },
    ],
    parameters: [
      { name: "region", kind: "string", value: "west" },
      { name: "express", kind: "boolean", value: "true" },
    ],
  }));
' >"$work/request.json"

retries=0
call() {
  local name=$1 request=$2 credential=$3 attempt
  for attempt in 1 2 3 4 5 6 7 8; do
    status=$(curl --silent --show-error --max-time 120 --output "$work/$name.body" --dump-header "$work/$name.headers" \
      --write-out '%{http_code}' --header @"$work/$credential.header" --header "Braid-Request-Id: $request" \
      --header 'Content-Type: application/json' --data-binary @"$work/request.json" \
      "http://127.0.0.1:$port/v1/invocations") || fail "call $name"
    # Busy, unavailable or past its deadline: nothing final was recorded, so the same request is retried.
    [[ $status == 503 ]] || return 0
    retries=$((retries + 1))
    sleep 3
  done
}
# A completed call: the release and build that ran, and outputs equal to independently authored bytes.
completed() {
  local name=$1 release=$2 replayed=$3
  [[ $status == 200 ]] || fail "$name answered $status"
  bun -e '
    const fs = require("fs");
    const { createHash } = require("crypto");
    const [body, headers, release, build, artifact, replayed] = process.argv.slice(1);
    const fail = (message) => { console.error(message); process.exit(1); };
    const response = JSON.parse(fs.readFileSync(body, "utf8"));
    if (!fs.readFileSync(headers, "utf8").toLowerCase().includes(`braid-invocation-replayed: ${replayed}`)) fail("replay header");
    if (response.schema !== "braid.mapping-invocation-response.v1" || response.releaseId !== release ||
        response.buildId !== build || response.artifactDigest !== artifact) fail("provenance");
    if (response.outputs.length !== 2) fail("output count");
    const output = (name) => {
      const item = response.outputs.find((candidate) => candidate.name === name) ?? fail(`output ${name}`);
      const bytes = Buffer.from(item.content, "base64");
      if (createHash("sha256").update(bytes).digest("hex") !== item.sha256) fail(`output digest ${name}`);
      return bytes.toString("utf8");
    };
    const shipment = output(null);
    const generatedAt = /"generatedAt": "([^"]+)"/.exec(shipment)?.[1] ?? fail("clock");
    if (generatedAt.slice(0, 19) !== response.currentDateTime.slice(0, 19)) fail("clock");
    const expected = `{\n  "orderId": "SO-1",\n  "region": "west",\n  "generatedAt": "${generatedAt}",\n  "currency": "EUR",\n  "express": true,\n  "items": [\n    {\n      "sku": "A",\n      "quantity": 2\n    },\n    {\n      "sku": "B",\n      "quantity": 9007199254740993\n    }\n  ]\n}\n`;
    if (shipment !== expected) fail("shipment");
    if (output("summary") !== `{\n  "lineCount": 2,\n  "region": "west"\n}\n`) fail("summary");
  ' "$work/$name.body" "$work/$name.headers" "$release" "$build_id" "$artifact_digest" "$replayed" || fail "$name response"
}
refused() {
  local name=$1 expected=$2 outcome=$3
  [[ $status == "$expected" ]] || fail "$name answered $status"
  [[ $(bun -e 'process.stdout.write(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).outcome)' "$work/$name.body") == "$outcome" ]] ||
    fail "$name outcome"
}
audit() {
  sql "SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id='$1'"
}

first_request=$(uuid)
call first "$first_request" caller
completed first "$first_release" false
recorded=$(audit "$first_request")
call retry "$first_request" caller
completed retry "$first_release" true
cmp -s "$work/first.body" "$work/retry.body" || fail 'the retry returned different bytes'
[[ $(audit "$first_request") == "$recorded" ]] || fail 'the retry ran the call again'
echo 'ok - a call completed, and its retry replayed the original bytes without running again'

second_release=$(uuid)
operator endpoint-release activate-second activate-release "${target[@]}" "$second_release" "$qualification" "$first_release"
expect activate-second activated
call second "$(uuid)" caller
completed second "$second_release" false
rollback_release=$(uuid)
operator endpoint-release rollback rollback-release "${target[@]}" "$rollback_release" "$first_release" "$second_release"
expect rollback activated
call rolled-back "$(uuid)" caller
completed rolled-back "$rollback_release" false
echo 'ok - a new activation, then a rollback to the first release, each served the next call'

call wrong-audience "$(uuid)" foreign
refused wrong-audience 401 unauthorized
grep -qi '^www-authenticate: bearer' "$work/wrong-audience.headers" || fail 'bearer challenge'
token expiring "$BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_CLIENT" "$BRAID_ENDPOINT_ACCEPTANCE_EXPIRING_SECRET" "$BRAID_ENDPOINT_ENTRA_AUDIENCE"
sleep 4
call expired "$(uuid)" expiring
refused expired 401 unauthorized
echo 'ok - expired and wrong-audience tokens were refused'

operator endpoint-grants revoke revoke-machine-invocation "${target[@]}" "$grant"
expect revoke revoked
call revoked "$(uuid)" caller
refused revoked 403 refused
call revoked-replay "$first_request" caller
refused revoked-replay 403 refused
echo 'ok - after revocation the caller is refused, also for a replay of its earlier call'

# A stop drains, joins the compiled runtime's work and exits 0 only once its cleanup is confirmed.
compose stop --timeout 100 endpoint >/dev/null 2>&1 || fail 'endpoint stop'
[[ $(docker inspect --format '{{.State.ExitCode}}' "$endpoint") == 0 ]] || fail 'endpoint stop without confirmed cleanup'
# The stop joined every runtime container the endpoint started; any that remains is settled and fails the run.
if [[ -n $(runtime_containers "$project") ]]; then
  settle "$project" 'after the endpoint stopped' || true
  fail 'compiled-runtime containers remained after the stop'
fi
if [[ $runtime == service ]]; then
  # A replica whose child cleanup was unconfirmed fails readiness; every one must still be healthy.
  for container in $(compose ps --quiet ferrule); do
    [[ $(docker inspect --format '{{.State.Health.Status}}' "$container") == healthy ]] || fail 'Ferrule service replica health'
  done
fi
compose rm --force endpoint >/dev/null 2>&1 || fail 'endpoint removal'
compose logs --no-color issuer >"$work/issuer.log" 2>&1
grep -q 'issuer machine-token' "$work/issuer.log" || fail 'no machine token was issued'
echo "ok - the endpoint stopped cleanly and joined its compiled runtime ($runtime; 503 retries: $retries)"
acceptance_complete=true
