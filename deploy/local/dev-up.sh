#!/usr/bin/env bash
# Starts a complete local Weft instance in the Development profile: PostgreSQL in a container,
# a private filesystem store, the Ferrule native and compiler images, chat through a local
# Ollama, and the Release server and Weft.Worker as user systemd units. Everything listens on
# loopback only. A rerun reuses the generated secrets, database, store and identity, rebuilds
# only for a new commit, and restarts a unit only when its build or settings changed.
# See docs/development.md ("Run a complete local instance").
set -euo pipefail
umask 077

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/dev-common.sh"
source "$script_dir/dev-units.sh"
source_root=$(cd "${WEFT_DEV_SOURCE:-$script_dir/../..}" && pwd)
read -r -a chat_models <<<"${WEFT_DEV_CHAT_MODELS:-qwen2.5:3b qwen2.5:1.5b}"
ollama_image=${WEFT_DEV_OLLAMA_IMAGE:-docker.io/ollama/ollama:latest}
postgres_image=docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc
build_log=$dev_logs/build.log

step() { printf '==> %s\n' "$*"; }
fail() { printf 'dev-up: %s\n' "$*" >&2; exit 1; }

for tool in docker git node npm curl systemd-run systemctl busctl ss sha256sum; do
  command -v "$tool" >/dev/null || fail "$tool is required."
done
dotnet=$(command -v dotnet) || fail 'The pinned .NET SDK (global.json) must be on PATH.'
dotnet_root=${DOTNET_ROOT:-$(dirname "$(readlink -f "$dotnet")")}
dotnet=$dotnet_root/dotnet
[[ $("$dotnet" --list-runtimes) == *"Microsoft.AspNetCore.App 10."* ]] ||
  fail "The ASP.NET Core 10 runtime is missing from $dotnet_root; set DOTNET_ROOT to the pinned SDK."
[[ ${#chat_models[@]} -ge 1 && ${#chat_models[@]} -le 16 ]] || fail 'Name one to sixteen chat models.'

mkdir -p "$dev_home" "$dev_config" "$dev_logs" "$dev_home/tmp" "$dev_home/run" "$dev_home/bin"
chmod 0700 "$dev_home" "$dev_config"
# The local sandbox keeps its private message history here.
install -d -m 0700 "$dev_home/sandbox"

# ---- Private settings: generated once, mode 0600, never printed.
secrets=$dev_config/secrets.env
instance=$dev_config/instance.env
# Every login the init script creates, so a login added there gets its password here too.
mapfile -t roles < <(sed -n 's/^\\getenv [a-z_]* WEFT_LOCAL_\([A-Z_]*\)_PASSWORD$/\1/p' "$source_root/deploy/local/postgres-init.sh")
((${#roles[@]} > 0)) || fail 'No logins found in deploy/local/postgres-init.sh.'
roles=(POSTGRES_ADMIN "${roles[@]}")
random_hex() { od -An -tx1 -N24 /dev/urandom | tr -d ' \n'; }
uuid() { cat /proc/sys/kernel/random/uuid; }
if [[ ! -f $secrets ]]; then
  docker volume inspect "$dev_postgres_volume" >/dev/null 2>&1 &&
    fail "Volume $dev_postgres_volume exists but $secrets is missing; restore it or purge the instance."
  step "Generating private credentials in $secrets"
  : >"$secrets"
fi
for role in "${roles[@]}"; do
  grep -q "^WEFT_LOCAL_${role}_PASSWORD=" "$secrets" || printf 'WEFT_LOCAL_%s_PASSWORD=%s\n' "$role" "$(random_hex)" >>"$secrets"
done
[[ -f $instance ]] || printf 'WEFT_DEV_ORGANIZATION_ID=%s\nWEFT_DEV_PRINCIPAL_ID=%s\nWEFT_DEV_PROJECT_KEY=%s\n' \
  "$(uuid)" "$(uuid)" "$(uuid)" >"$instance"
chmod 0600 "$secrets" "$instance"
# Sourced, not exported: build and container children never inherit the passwords.
source "$secrets"
source "$instance"
for role in "${roles[@]}"; do
  variable=WEFT_LOCAL_${role}_PASSWORD
  [[ ${!variable:-} =~ ^[0-9a-f]{48}$ ]] || fail "$secrets is incomplete; restore it or purge the instance."
done

connection() { # login role-variable
  local variable=WEFT_LOCAL_${2}_PASSWORD
  printf 'Host=127.0.0.1;Port=%s;Database=weft;Username=%s;Password=%s;Timeout=30;Command Timeout=60' \
    "$dev_postgres_port" "$1" "${!variable}"
}

# ---- Build: Release server, worker and owner tools, published per commit outside the checkout.
commit=$(git -C "$source_root" rev-parse --short=12 HEAD)
build_key=$commit
[[ -z $(git -C "$source_root" status --porcelain --untracked-files=no) ]] || build_key=$commit-dirty
app=$dev_home/app/$build_key
if [[ ! -f $app/.complete || $build_key == *-dirty ]]; then
  step "Building the web assets and Release hosts from $source_root ($build_key); log: $build_log"
  rm -rf "$app.partial"
  (
    cd "$source_root"
    export DOTNET_ROOT=$dotnet_root DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
    [[ -d node_modules ]] || npm ci --no-audit --no-fund
    npm run build
    "$dotnet" restore dotnet/Weft.slnx --locked-mode
    for project in apps/Weft.Server apps/Weft.Worker tools/Weft.DatabaseTool tools/Weft.StorageTool; do
      "$dotnet" publish "dotnet/$project/${project#*/}.csproj" --configuration Release --no-restore -m:2 \
        --output "$app.partial/${project#*/}"
    done
  ) >>"$build_log" 2>&1 || fail "The build failed; see $build_log."
  touch "$app.partial/.complete"
  rm -rf "$app"
  mv "$app.partial" "$app"
fi

# ---- Ferrule images: the native worker, then the combined compiler image built from its exact ID.
# The combined image serves inspection, previews, regression runs and builds through one identity.
build_docker() {
  if [[ -n ${WEFT_DEV_BUILD_DOCKER_CONFIG:-} ]]; then DOCKER_CONFIG=$WEFT_DEV_BUILD_DOCKER_CONFIG DOCKER_BUILDKIT=1 docker "$@"
  else DOCKER_BUILDKIT=1 docker "$@"; fi
}
image_id() { docker image inspect --format '{{.Id}}' "$1" 2>/dev/null; }
native_tag=$dev_name/ferrule-host:$build_key
compiler_tag=$dev_name/build-worker:$build_key
if [[ -z $(image_id "$compiler_tag") || $build_key == *-dirty ]]; then
  build_docker buildx version >/dev/null 2>&1 ||
    fail 'Docker BuildKit (buildx) is required; set WEFT_DEV_BUILD_DOCKER_CONFIG to a Docker config with the buildx plugin.'
  step "Building the Ferrule native image $native_tag; log: $build_log"
  build_docker build --progress=plain --tag "$native_tag" "$source_root/engines/ferrule-host" >>"$build_log" 2>&1 ||
    fail "The native image build failed; see $build_log."
  native_id=$(image_id "$native_tag")
  [[ $native_id =~ ^sha256:[a-f0-9]{64}$ ]] || fail 'The native image has no immutable ID.'
  # BuildKit resolves FROM by name, so bind a unique local reference to the verified ID and check the
  # copied binary's digest in the build, as tools/build-worker/build_image.py does.
  step "Building the compiler image $compiler_tag; log: $build_log"
  reference=$dev_name-compiler-source:${native_id#sha256:}-$$
  work=$(mktemp -d "$dev_home/tmp/native-source.XXXXXX")
  docker tag "$native_id" "$reference"
  source_container=$(docker container create --label "$dev_label=$dev_name" --network none --entrypoint /ferrule-host "$native_id")
  docker cp "$source_container:/ferrule-host" "$work/ferrule-host" >/dev/null
  docker container rm "$source_container" >/dev/null
  native_digest=$(sha256sum "$work/ferrule-host" | cut -d' ' -f1)
  rm -rf "$work"
  build_status=0
  build_docker build --progress=plain --tag "$compiler_tag" --build-arg "WEFT_NATIVE_IMAGE=$reference" \
    --build-arg "WEFT_NATIVE_BINARY_SHA256=$native_digest" --file "$source_root/engines/build-worker/Dockerfile" \
    "$source_root" >>"$build_log" 2>&1 || build_status=$?
  docker image rm "$reference" >/dev/null
  ((build_status == 0)) || fail "The compiler image build failed; see $build_log."
fi
worker_image=$(image_id "$compiler_tag")
[[ $worker_image =~ ^sha256:[a-f0-9]{64}$ ]] || fail 'The compiler image has no immutable ID.'

# ---- PostgreSQL: a dedicated container and volume, published on loopback only. The repository's
# init script creates every restricted login once, from the generated passwords.
postgres_env=$dev_config/postgres.env
{
  printf 'POSTGRES_DB=weft\nPOSTGRES_USER=postgres\nPOSTGRES_PASSWORD=%s\n' "$WEFT_LOCAL_POSTGRES_ADMIN_PASSWORD"
  grep -v '^WEFT_LOCAL_POSTGRES_ADMIN_PASSWORD=' "$secrets"
} >"$postgres_env"
install -D -m 0644 "$source_root/deploy/local/postgres-init.sh" "$dev_home/postgres-init/postgres-init.sh"
if docker container inspect "$dev_postgres" >/dev/null 2>&1; then
  dev_owned_container "$dev_postgres" || fail "Container $dev_postgres exists but does not belong to this instance."
  binding=$(docker container inspect --format '{{json .HostConfig.PortBindings}}' "$dev_postgres")
  [[ $binding == "{\"5432/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"$dev_postgres_port\"}]}" ]] ||
    fail "Container $dev_postgres is not published on 127.0.0.1:$dev_postgres_port."
  dev_container_running "$dev_postgres" || { step "Starting $dev_postgres"; docker start "$dev_postgres" >/dev/null; }
else
  [[ -z $(ss -ltnH "sport = :$dev_postgres_port") ]] || fail "Port $dev_postgres_port is in use; set WEFT_DEV_POSTGRES_PORT."
  step "Creating $dev_postgres on 127.0.0.1:$dev_postgres_port"
  docker run --detach --name "$dev_postgres" --label "$dev_label=$dev_name" --restart no \
    --memory 1g --cpus 2 --env-file "$postgres_env" \
    --volume "$dev_postgres_volume:/var/lib/postgresql/data" \
    --volume "$dev_home/postgres-init/postgres-init.sh:/docker-entrypoint-initdb.d/01-weft-roles.sh:ro" \
    --publish "127.0.0.1:$dev_postgres_port:5432" "$postgres_image" >/dev/null
fi
# The first start runs initdb and the init script; the server listens on TCP only afterwards.
for attempt in $(seq 1 180); do
  docker exec "$dev_postgres" pg_isready -q -h 127.0.0.1 -U postgres -d weft 2>/dev/null && break
  dev_container_running "$dev_postgres" || fail "$dev_postgres stopped; see docker logs $dev_postgres."
  ((attempt < 180)) || fail "$dev_postgres did not become ready."
  sleep 1
done

owner_tool() { # Runs Weft.DatabaseTool with only the migration login.
  (
    export DOTNET_ROOT=$dotnet_root WEFT_MIGRATION_CONNECTION_STRING
    WEFT_MIGRATION_CONNECTION_STRING=$(connection weft_migrator MIGRATOR)
    "$dotnet" "$app/Weft.DatabaseTool/Weft.DatabaseTool.dll" "$@"
  )
}
step 'Applying migrations'
owner_tool migrate >/dev/null
step 'Provisioning the Development identity'
(
  export WEFT_DEV_ORGANIZATION_NAME=${WEFT_DEV_ORGANIZATION_NAME:-Local development}
  export WEFT_DEV_ORGANIZATION_ID WEFT_DEV_PRINCIPAL_ID
  owner_tool provision-development-identity >/dev/null
)
owner_tool set-project-creation-capability "$WEFT_DEV_ORGANIZATION_ID" "$WEFT_DEV_PRINCIPAL_ID" true >/dev/null

# ---- Private storage: a new v2 filesystem root outside the application and repository trees.
storage_root=$(realpath -m "$dev_home/storage/root")
if [[ ! -d $storage_root ]]; then
  step "Initializing private storage at $storage_root"
  install -d -m 0700 "$(dirname "$storage_root")"
  DOTNET_ROOT=$dotnet_root "$dotnet" "$app/Weft.StorageTool/Weft.StorageTool.dll" initialize-v2 "$storage_root" \
    "$app/Weft.Server" >/dev/null
fi

# ---- Ollama: the named loopback container (created with its model volume if absent) or an
# externally managed endpoint when WEFT_DEV_OLLAMA_CONTAINER is empty.
if [[ -n $dev_ollama_container ]]; then
  if ! docker container inspect "$dev_ollama_container" >/dev/null 2>&1; then
    step "Creating $dev_ollama_container on ${dev_ollama_url#http://}"
    docker run --detach --name "$dev_ollama_container" --label "$dev_label=$dev_name" --restart no \
      --volume weft-ollama-models:/root/.ollama --publish "${dev_ollama_url#http://}:11434" "$ollama_image" >/dev/null
  elif ! dev_container_running "$dev_ollama_container"; then
    step "Starting $dev_ollama_container"
    docker start "$dev_ollama_container" >/dev/null
  fi
fi
for attempt in $(seq 1 60); do
  curl -fsS --max-time 5 "$dev_ollama_url/api/tags" >"$dev_home/tmp/ollama-tags.json" 2>/dev/null && break
  ((attempt < 60)) || fail "Ollama is not answering at $dev_ollama_url."
  sleep 1
done
for model in "${chat_models[@]}"; do
  if ! MODEL=$model node -e 'const t=require(process.argv[1]); process.exit(t.models.some(m=>m.name===process.env.MODEL)?0:1)' \
      "$dev_home/tmp/ollama-tags.json"; then
    [[ -n $dev_ollama_container ]] || fail "Model $model is not available at $dev_ollama_url."
    step "Pulling $model into $dev_ollama_container"
    docker exec "$dev_ollama_container" ollama pull "$model" >>"$build_log" 2>&1 || fail "Pulling $model failed; see $build_log."
  fi
done

# The first model is the default; each profile admits only this loopback endpoint.
chat_profiles=$dev_config/chat-profiles.json
MODELS=${chat_models[*]} ENDPOINT=$dev_ollama_url/api/chat node -e '
  const profiles = process.env.MODELS.split(" ").map((model) => ({
    id: "ollama-" + model.toLowerCase().replace(/[^a-z0-9.]+/g, "-"),
    version: 1,
    displayName: model + " (local Ollama)",
    provider: "ollama",
    dialect: "ollamaChatNdjson",
    model,
    endpoint: process.env.ENDPOINT,
    loopbackFixture: true,
    credentialReference: null,
    capabilities: ["text", "functionTools"],
    metadataPolicy: { id: "mapping-metadata", version: 1, mappingMetadataAllowed: true },
  }));
  const document = { version: 1, retentionDays: 30, turnSeconds: 180, maxOutputTokens: 1024, profiles, credentials: [] };
  require("fs").writeFileSync(process.argv[1], JSON.stringify(document, null, 2) + "\n", { mode: 0o600 });
' "$chat_profiles"
chmod 0600 "$chat_profiles"

# ---- Host settings: one private environment file per unit, each with only its own logins.
# Optional Docker CLI budgets for busy hosts reach both units unchanged; the hosts validate them.
docker_budgets=$(dev_docker_budgets)
server_env=$dev_config/server.env
cat >"$server_env" <<EOF
DOTNET_ENVIRONMENT=Development
ASPNETCORE_ENVIRONMENT=Development
WEFT_CATALOG_AUTH_MODE=development
WEFT_CATALOG_ENABLED=true
WEFT_DEV_IDENTITY_ENABLED=true
WEFT_DEV_ORGANIZATION_ID=$WEFT_DEV_ORGANIZATION_ID
WEFT_DEV_PRINCIPAL_ID=$WEFT_DEV_PRINCIPAL_ID
WEFT_DEV_LISTEN_ADDR=127.0.0.1:$dev_port
WEFT_DEV_BROWSER_ORIGIN=$dev_origin
WEFT_CATALOG_CONNECTION_STRING=$(connection weft_catalog_reader CATALOG)
WEFT_METADATA_EDITS_ENABLED=true
WEFT_POSTGRES_METADATA_CONNECTION_STRING=$(connection weft_catalog_editor METADATA)
WEFT_PROJECT_CREATION_ENABLED=true
WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING=$(connection weft_project_creator CREATOR)
WEFT_REVISION_INGESTION_ENABLED=true
WEFT_POSTGRES_INGESTION_CONNECTION_STRING=$(connection weft_ingest INGEST)
WEFT_REVISION_REPORTS_ENABLED=true
WEFT_POSTGRES_REPORT_CONNECTION_STRING=$(connection weft_report_reader REPORT)
WEFT_REVISION_REPORT_DERIVATION_ENABLED=true
WEFT_POSTGRES_DERIVATION_CONNECTION_STRING=$(connection weft_deriver DERIVATION)
WEFT_PRIVATE_STORAGE_ROOT=$storage_root
WEFT_FERRULE_WORKER_IMAGE=$worker_image
WEFT_CHAT_ENABLED=true
WEFT_CHAT_PROFILES_FILE=$chat_profiles
WEFT_CHAT_READER_CONNECTION_STRING=$(connection weft_chat_reader CHAT_READER)
WEFT_CHAT_WRITER_CONNECTION_STRING=$(connection weft_chat_writer CHAT_WRITER)
WEFT_PREVIEWS_ENABLED=true
WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING=$(connection weft_preview_reader PREVIEW_READER)
WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING=$(connection weft_preview_writer PREVIEW_WRITER)
WEFT_REGRESSIONS_ENABLED=true
WEFT_REGRESSION_CASE_READER_CONNECTION_STRING=$(connection weft_case_reader CASE_READER)
WEFT_REGRESSION_CASE_WRITER_CONNECTION_STRING=$(connection weft_case_writer CASE_WRITER)
WEFT_REGRESSION_RUN_READER_CONNECTION_STRING=$(connection weft_run_reader RUN_READER)
WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING=$(connection weft_run_writer RUN_WRITER)
WEFT_BUILDS_ENABLED=true
WEFT_BUILD_METADATA_CONNECTION_STRING=$(connection weft_build_metadata_reader BUILD_METADATA)
WEFT_BUILD_CONTROL_CONNECTION_STRING=$(connection weft_build_writer BUILD_WRITER)
WEFT_BUILD_ACCESS_CONNECTION_STRING=$(connection weft_runtime RUNTIME)
WEFT_BUILD_REPORT_CONNECTION_STRING=$(connection weft_report_reader REPORT)
WEFT_BUILD_CASE_READER_CONNECTION_STRING=$(connection weft_case_reader CASE_READER)
WEFT_BUILD_EVIDENCE_READER_CONNECTION_STRING=$(connection weft_run_reader RUN_READER)
WEFT_BUILD_RUN_READER_CONNECTION_STRING=$(connection weft_build_run_reader BUILD_RUN_READER)
WEFT_BUILD_RUN_WRITER_CONNECTION_STRING=$(connection weft_build_run_writer BUILD_RUN_WRITER)
WEFT_RELEASE_OPERATIONS_ENABLED=true
WEFT_RELEASE_READER_CONNECTION_STRING=$(connection weft_release_reader RELEASE_READER)
WEFT_RELEASE_WRITER_CONNECTION_STRING=$(connection weft_release_writer RELEASE_WRITER)
WEFT_QUALIFICATION_READER_CONNECTION_STRING=$(connection weft_qualification_reader QUALIFICATION_READER)
WEFT_LOCAL_SANDBOX_ENABLED=true
WEFT_LOCAL_SANDBOX_HISTORY_DIRECTORY=$dev_home/sandbox
WEFT_LOCAL_SANDBOX_RUNTIME_IMAGE=$worker_image
WEFT_LOCAL_SANDBOX_DOCKER_EXECUTABLE=$(command -v docker)
WEFT_LOCAL_SANDBOX_QUALIFICATION_WRITER_CONNECTION_STRING=$(connection weft_qualification_writer QUALIFICATION_WRITER)
WEFT_LOCAL_SANDBOX_BUILD_READER_CONNECTION_STRING=$(connection weft_build_reader BUILD_READER)
WEFT_LOCAL_SANDBOX_INVOCATION_WRITER_CONNECTION_STRING=$(connection weft_invocation_writer INVOCATION_WRITER)
WEFT_LOCAL_SANDBOX_INVOCATION_GRANT_WRITER_CONNECTION_STRING=$(connection weft_invocation_grant_writer INVOCATION_GRANT_WRITER)
$docker_budgets
EOF
worker_env=$dev_config/worker.env
cat >"$worker_env" <<EOF
DOTNET_ENVIRONMENT=Development
WEFT_PREVIEW_WORKER_ENABLED=true
WEFT_PREVIEW_WRITER_CONNECTION_STRING=$(connection weft_preview_writer PREVIEW_WRITER)
WEFT_PRIVATE_STORAGE_ROOT=$storage_root
WEFT_FERRULE_WORKER_IMAGE=$worker_image
WEFT_DOCKER_EXECUTABLE=$(command -v docker)
WEFT_REGRESSION_WORKER_ENABLED=true
WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING=$(connection weft_run_writer RUN_WRITER)
WEFT_REGRESSION_CASE_READER_CONNECTION_STRING=$(connection weft_case_reader CASE_READER)
WEFT_REGRESSION_ACCESS_CONNECTION_STRING=$(connection weft_runtime RUNTIME)
WEFT_REGRESSION_REPORT_READER_CONNECTION_STRING=$(connection weft_report_reader REPORT)
WEFT_AUTOMATION_COMPARISON_CONNECTION_STRING=$(connection weft_comparison_settler COMPARISON)
WEFT_BUILD_WORKER_ENABLED=true
WEFT_BUILD_DISPATCHER_CONNECTION_STRING=$(connection weft_build_dispatcher BUILD_DISPATCHER)
WEFT_BUILD_WRITER_CONNECTION_STRING=$(connection weft_build_writer BUILD_WRITER)
WEFT_BUILD_ACCESS_CONNECTION_STRING=$(connection weft_runtime RUNTIME)
WEFT_BUILD_REPORT_READER_CONNECTION_STRING=$(connection weft_report_reader REPORT)
WEFT_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING=$(connection weft_run_reader RUN_READER)
WEFT_BUILD_CASE_READER_CONNECTION_STRING=$(connection weft_case_reader CASE_READER)
$docker_budgets
EOF
chmod 0600 "$server_env" "$worker_env"

# ---- Units: the generated invocation, private settings and binary determine the unit stamp.
# KillMode=mixed lets the host settle Docker children within the independent stop grace.
declare -A log_offset
start_unit "$dev_server_unit" Weft.Server "$server_env"
start_unit "$dev_worker_unit" Weft.Worker "$worker_env"

for attempt in $(seq 1 180); do
  curl -fsS --max-time 5 "$dev_origin/api/v1/health" >/dev/null 2>&1 && break
  systemctl --user is-active --quiet "$dev_server_unit" || fail "$dev_server_unit stopped; see $dev_logs/$dev_server_unit.log."
  ((attempt < 180)) || fail "The server did not answer at $dev_origin; see $dev_logs/$dev_server_unit.log."
  sleep 1
done
# The worker has no listener; its generic host reports startup once Wolverine admitted its storage.
if [[ -n ${log_offset[$dev_worker_unit]:-} ]]; then
  for attempt in $(seq 1 180); do
    tail -c "+$((log_offset[$dev_worker_unit] + 1))" "$dev_logs/$dev_worker_unit.log" | grep -c 'Application started' >/dev/null && break
    systemctl --user is-active --quiet "$dev_worker_unit" || fail "$dev_worker_unit stopped; see $dev_logs/$dev_worker_unit.log."
    ((attempt < 180)) || fail "$dev_worker_unit did not report startup; see $dev_logs/$dev_worker_unit.log."
    sleep 1
  done
fi

# ---- The default project, created once through the product API (replayed by its fixed key), and
# the explicit Administrator grant on it.
if [[ -z ${WEFT_DEV_PROJECT_ID:-} ]]; then
  step 'Creating the default project'
  jar=$(mktemp "$dev_home/tmp/cookies.XXXXXX")
  context=$(curl -fsS --max-time 30 -c "$jar" -b "$jar" "$dev_origin/api/v1/catalog/context")
  token=$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).requestToken)' "$context")
  reply=$(curl -fsS --max-time 60 -c "$jar" -b "$jar" -X POST -H "Origin: $dev_origin" -H "X-XSRF-TOKEN: $token" \
    -H "Idempotency-Key: $WEFT_DEV_PROJECT_KEY" -H 'Content-Type: application/vnd.weft.project-create.v1+json' \
    --data '{"name":"Local project","description":"Created by deploy/local/dev-up.sh."}' \
    "$dev_origin/api/v1/organizations/$WEFT_DEV_ORGANIZATION_ID/projects") || { rm -f "$jar"; fail 'Project creation failed.'; }
  rm -f "$jar"
  WEFT_DEV_PROJECT_ID=$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).receipt.project.projectId)' "$reply")
  [[ $WEFT_DEV_PROJECT_ID =~ ^[0-9a-f-]{36}$ ]] || fail 'The project creation receipt is invalid.'
  printf 'WEFT_DEV_PROJECT_ID=%s\n' "$WEFT_DEV_PROJECT_ID" >>"$instance"
fi
(
  export WEFT_DEV_ORGANIZATION_NAME=${WEFT_DEV_ORGANIZATION_NAME:-Local development}
  export WEFT_DEV_ORGANIZATION_ID WEFT_DEV_PRINCIPAL_ID WEFT_DEV_PROJECT_ID WEFT_DEV_PROJECT_ROLE=administrator
  owner_tool provision-development-identity >/dev/null
)

# ---- Warm the default model so the first chat turn does not wait for it to load.
curl -fsS --max-time 120 "$dev_ollama_url/api/generate" \
  --data "{\"model\":\"${chat_models[0]}\",\"keep_alive\":\"30m\"}" >/dev/null 2>&1 || true

# ---- Control scripts bound to this instance, independent of the checkout.
for script in dev-common.sh dev-down.sh dev-status.sh; do install -m 0700 "$script_dir/$script" "$dev_home/bin/$script"; done
for action in stop:dev-down status:dev-status; do
  cat >"$dev_home/${action%%:*}.sh" <<EOF
#!/usr/bin/env bash
export WEFT_DEV_NAME=$dev_name WEFT_DEV_PORT=$dev_port WEFT_DEV_POSTGRES_PORT=$dev_postgres_port
export WEFT_DEV_HOME=$dev_home WEFT_DEV_CONFIG=$dev_config
export WEFT_DEV_OLLAMA_CONTAINER=$dev_ollama_container WEFT_DEV_OLLAMA_URL=$dev_ollama_url
exec "$dev_home/bin/${action#*:}.sh" "\$@"
EOF
  chmod 0700 "$dev_home/${action%%:*}.sh"
done

# ---- Optional demo data, added through the product API like any user; a rerun replays it.
if [[ ${WEFT_DEV_SEED:-0} == 1 ]]; then
  step 'Seeding demo data'
  "$script_dir/dev-seed.sh" || printf 'dev-up: some demo data could not be seeded; see the list above.\n' >&2
fi

cat <<EOF

Weft is running at $dev_origin/catalog (Development identity, loopback only).
  Build:    $build_key, Ferrule image $worker_image
  Chat:     ${chat_models[*]} through $dev_ollama_url (default ${chat_models[0]})
  Sandbox:  local deployment simulation on each mapping's Sandbox page (docs/local-sandbox.md)
  Logs:     $dev_logs
  Settings: $dev_config (private)
  Status:   $dev_home/status.sh
  Stop:     $dev_home/stop.sh
EOF
