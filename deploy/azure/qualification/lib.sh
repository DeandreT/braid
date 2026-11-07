# shellcheck shell=bash
# Helpers for deploy/azure/qualify.sh: the receipt, Azure CLI wrappers, job executions and their console
# lines, HTTP checks and app-only tokens. Sourced only by qualify.sh, which sets the run's variables.
# Secret values (passwords, client credentials, assertions, tokens) live only in private files under $work
# and reach commands as file references or environment values, never as arguments or output.

# Every management command names the approved subscription, whatever the CLI's default becomes. Data-plane
# commands (Key Vault items, registry repositories, Log Analytics queries) address resources by name or ID.
azs() {
  az "$@" --subscription "$subscription"
}

# Run-group commands. The PostgreSQL administrator password reaches only deployments, from its private file,
# through the parameter file's readEnvironmentVariable().
azg() {
  azs "$@" --resource-group "$group"
}

deploy() { # deployment-name az-deployment-arguments...
  local deployment=$1
  shift
  WEFT_POSTGRES_ADMIN_PASSWORD=$(<"$work/postgres-admin-password") \
    azg deployment group create --name "$deployment" "$@" --output none
}

deployment_output() { # deployment-name output-name
  jq -er --arg name "$2" '.[$name].value | if type == "string" then . else tojson end' "$work/outputs-$1.json"
}

save_outputs() { # deployment-name
  azg deployment group show --name "$1" --query properties.outputs --output json >"$work/outputs-$1.json"
}

uuid() {
  cat /proc/sys/kernel/random/uuid
}

now() {
  date -u +%Y-%m-%dT%H:%M:%SZ
}

# A flat JSON object of string facts: evidence name value name value ...
evidence() {
  local arguments=()
  while (($# >= 2)); do
    arguments+=(--arg "$1" "$2")
    shift 2
  done
  jq -cn "${arguments[@]}" '$ARGS.named'
}

# One receipt line per step, check or operator confirmation; the receipt holds only non-secret facts.
record() { # kind name result [evidence-json]
  jq -cn --arg time "$(now)" --arg run "$run_id" --arg kind "$1" --arg name "$2" --arg result "$3" \
    --argjson evidence "${4:-"{}"}" '{time: $time, run: $run, kind: $kind, name: $name, result: $result, evidence: $evidence}' \
    >>"$receipt"
}

step() { # name description
  current_step=$1
  record step "$1" started
  printf '\n== %s\n' "$2"
}

pass() { # check-name [evidence-json]
  record check "$1" pass "${2:-"{}"}"
  checks_passed=$((checks_passed + 1))
  printf 'ok - %s\n' "$1"
}

# A failed check fails the run: the receipt names it, and the group is kept for investigation.
fail() { # check-name message [evidence-json]
  record check "$1" fail "${3:-"{}"}"
  failed_check=$1
  printf 'FAILED: %s: %s\n' "$1" "$2" >&2
  exit 1
}

# One operator confirmation. Anything but yes fails the run; the receipt separates these from automated checks.
confirm_item() { # name line...
  local name=$1 answer
  shift
  printf '\n[operator] %s\n' "$name"
  printf '  %s\n' "$@"
  printf 'Confirm with yes, or anything else if it did not hold: '
  read -r answer || answer=
  if [[ $answer == yes ]]; then
    record guided "$name" confirmed "$(evidence method operator)"
    guided_confirmed=$((guided_confirmed + 1))
  else
    record guided "$name" declined "$(evidence method operator)"
    failed_check=$name
    printf 'FAILED: %s was not confirmed by the operator\n' "$name" >&2
    exit 1
  fi
}

# Waits on a condition command, polling every interval seconds until the deadline.
wait_for() { # seconds interval command...
  local deadline=$(($(date +%s) + $1)) interval=$2
  shift 2
  until "$@"; do
    (($(date +%s) < deadline)) || return 1
    sleep "$interval"
  done
}

# ---- Job executions and console logs ----

# Starts one execution of a manual job with `az containerapp job start` options and waits for its end state.
run_job() { # label job [start-options...]
  local label=$1 job=$2
  shift 2
  execution=$(azg containerapp job start --name "$job" "$@" --query name --output tsv)
  [[ -n $execution ]] || fail "$label" "the job $job did not report an execution"
  await_execution "$label" "$job" "$execution"
}

# Starts the database-roles job with a replacement command: the PostgreSQL client, signed in as the
# migration login, reads evidence in a read-only transaction and prints one tagged line per row.
query_database() { # label sql
  local label=$1 sql=$2 job="$prefix-database-roles" script
  script='set -euo pipefail
export PGUSER=weft_migrator PGPASSWORD="$WEFT_MIGRATOR_PASSWORD" PGDATABASE=weft PGOPTIONS="-c default_transaction_read_only=on"
psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --field-separator=" " --command="$1" | sed "s/^/weft-qualify-row /"
echo weft-qualify-end'
  azg containerapp job show --name "$job" --query properties.template --output json >"$work/$label.template.json"
  jq --arg script "$script" --arg sql "$sql" \
    '{containers: [.containers[0] | {image, name, env, resources, command: ["bash", "-c", $script, "weft-qualify", $sql], args: []}]}' \
    "$work/$label.template.json" >"$work/$label.start.json"
  execution=$(az rest --method post --url "$arm/resourceGroups/$group/providers/Microsoft.App/jobs/$job/start?api-version=2024-03-01" \
    --body "@$work/$label.start.json" --query name --output tsv)
  [[ -n $execution ]] || fail "$label" 'the evidence query did not report an execution'
  await_execution "$label" "$job" "$execution"
  [[ $execution_status == Succeeded ]] || fail "$label" "the evidence query ended $execution_status"
  await_lines "$label" "$execution" '^weft-qualify-end'
  sed -n 's/^weft-qualify-row //p' "$work/$label.lines" >"$work/$label.rows"
}

await_execution() { # label job execution
  local label=$1 job=$2 execution=$3 deadline=$(($(date +%s) + 1800))
  execution_status=
  while :; do
    # A failed status read is retried like a running execution.
    execution_status=$(azg containerapp job execution show --name "$job" --job-execution-name "$execution" \
      --query properties.status --output tsv) || execution_status=Unknown
    [[ $execution_status == Succeeded || $execution_status == Failed || $execution_status == Stopped ||
      $execution_status == Degraded ]] && break
    (($(date +%s) < deadline)) || fail "$label" "the execution $execution did not finish"
    sleep 15
  done
  last_execution=$execution
}

# Console lines of one execution's replicas from Log Analytics, polled until one matches the pattern
# (ingestion takes minutes). Leaves them in $work/<label>.lines.
await_lines() { # label execution pattern
  local label=$1 execution=$2 pattern=$3
  if ! wait_for 1200 20 execution_lines "$execution" "$work/$label.lines" "$pattern"; then
    fail "$label" "no console line of $execution matched within 20 minutes"
  fi
}

execution_lines() { # execution output pattern
  log_query "ContainerAppConsoleLogs_CL | where ContainerGroupName_s startswith '$1' | project TimeGenerated, Log_s | order by TimeGenerated asc" >"$2" &&
    grep -Eq "$3" "$2"
}

app_lines() { # app since pattern output
  log_query "ContainerAppConsoleLogs_CL | where ContainerAppName_s == '$1' and TimeGenerated >= datetime($2) | project TimeGenerated, Log_s | order by TimeGenerated asc" >"$4" &&
    grep -Eq "$3" "$4"
}

log_query() { # kql → one console line per output line
  az monitor log-analytics query --workspace "$workspace" --analytics-query "$1" --output json | jq -r '.[].Log_s'
}

# The single JSON line a release or grant command prints, with the execution's end state.
tool_outcome() { # label job container expected-status args...
  local label=$1 job=$2 container=$3 expected=$4
  shift 4
  run_job "$label" "$job" --container-name "$container" --args "$@"
  await_lines "$label" "$last_execution" '^\{.*"(outcome|error)"'
  grep -E '^\{.*"(outcome|error)"' "$work/$label.lines" | tail -n 1 >"$work/$label.json"
  outcome=$(jq -r '.outcome // .error' "$work/$label.json")
  [[ $execution_status == "$expected" ]] ||
    fail "$label" "the execution ended $execution_status with $outcome" "$(evidence execution "$last_execution" outcome "$outcome")"
}

# ---- HTTP ----

http_get() { # name url [curl-options...]; leaves status, $work/<name>.headers and .body
  local name=$1 url=$2
  shift 2
  status=$(curl --silent --show-error --max-time 30 --dump-header "$work/$name.headers" --output "$work/$name.body" \
    --write-out '%{http_code}' "$@" "$url") || status=000
}

healthy() {
  http_get health "https://$public_host/api/v1/health"
  [[ $status == 200 ]]
}

# ---- App-only tokens for the machine caller ----

base64url() {
  base64 -w0 | tr '+/' '-_' | tr -d '='
}

# A certificate credential signs a short-lived client assertion; it stays in a private file.
client_assertion() { # output-file
  local issued header payload signature thumbprint
  issued=$(date +%s)
  thumbprint=$(openssl x509 -in "$caller_certificate_file" -outform DER | openssl dgst -sha1 -binary | base64url)
  header=$(jq -cn --arg x5t "$thumbprint" '{alg: "RS256", typ: "JWT", x5t: $x5t}' | base64url)
  payload=$(jq -cn --arg aud "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" --arg client "$caller_client" \
    --arg jti "$(uuid)" --argjson issued "$issued" \
    '{aud: $aud, iss: $client, sub: $client, jti: $jti, nbf: $issued, iat: $issued, exp: ($issued + 300)}' | base64url)
  signature=$(printf '%s.%s' "$header" "$payload" | openssl dgst -sha256 -sign "$caller_certificate_file" -binary | base64url)
  printf '%s.%s.%s' "$header" "$payload" "$signature" >"$1"
}

# Requests one token through the client-credentials grant and keeps its decoded claims, which are not secret.
request_token() { # name scope
  local name=$1 scope=$2 form
  form=(--data-urlencode grant_type=client_credentials --data-urlencode "client_id=$caller_client" --data-urlencode "scope=$scope")
  if [[ -n $caller_secret_file ]]; then
    form+=(--data-urlencode "client_secret@$work/caller.secret")
  else
    client_assertion "$work/$name.assertion"
    form+=(--data-urlencode client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
      --data-urlencode "client_assertion@$work/$name.assertion")
  fi
  curl --silent --show-error --fail --max-time 60 "${form[@]}" --output "$work/$name.response" \
    "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" || fail "token-$name" 'the token request failed'
  rm -f -- "$work/$name.assertion"
  jq -je 'select(.token_type == "Bearer") | .access_token | strings' "$work/$name.response" >"$work/$name.token" ||
    fail "token-$name" 'the token response has no bearer token'
  rm -f -- "$work/$name.response"
  jq -Re 'split(".")[1] | gsub("-"; "+") | gsub("_"; "/") | . + ("=" * ((4 - length % 4) % 4)) | @base64d | fromjson' \
    "$work/$name.token" >"$work/$name.claims" || fail "token-$name" 'the token is not a JSON web token'
}

claim() { # name claim
  jq -r --arg claim "$2" '.[$claim] // empty | tostring' "$work/$1.claims"
}
