#!/usr/bin/env bash
# Offline tests of deploy/azure/qualify.sh. Each scenario runs the harness from a committed copy of the files it
# uses, against the recording fakes in fakes/ (az, curl, docker, date and sleep), so nothing reaches Azure or the
# network. Usage: qualify-test.sh [SCENARIO...]; without arguments every scenario runs. With
# BRAID_QUALIFY_UPDATE_EXPECTED=1 the successful run rewrites expected/successful-run.calls.
# shellcheck disable=SC2016,SC2329 # jq programs in single quotes; scenarios and their helpers run by name.
set -euo pipefail
tests=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository=$(cd -- "$tests/../../.." && pwd -P)

subscription=5f0c3a2e-0000-4000-8000-000000000001
tenant=5f0c3a2e-0000-4000-8000-000000000002
client=5f0c3a2e-0000-4000-8000-000000000003
audience=5f0c3a2e-0000-4000-8000-000000000004
caller_client=5f0c3a2e-0000-4000-8000-000000000005
group=rg-braid-qualify
FAKE_ARTIFACT=$(printf artifact | sha256sum | cut -d' ' -f1)
FAKE_TOOLCHAIN=sha256:$(printf toolchain | sha256sum | cut -d' ' -f1)
FAKE_RUNTIME_IDENTITY=sha256:$(printf runtime | sha256sum | cut -d' ' -f1)
FAKE_CALLER_SECRET=caller-secret-$(openssl rand -hex 12)
FAKE_REAL_DATE=$(command -v date)
export FAKE_SUBSCRIPTION=$subscription FAKE_TENANT=$tenant FAKE_OWNER_OBJECT=5f0c3a2e-0000-4000-8000-00000000000b \
  FAKE_AUDIENCE=$audience FAKE_CALLER_OBJECT=5f0c3a2e-0000-4000-8000-000000000006 \
  FAKE_PROJECT=5f0c3a2e-0000-4000-8000-000000000007 FAKE_MAPPING=5f0c3a2e-0000-4000-8000-000000000008 \
  FAKE_BUILD=5f0c3a2e-0000-4000-8000-000000000009 FAKE_ENVIRONMENT=5f0c3a2e-0000-4000-8000-00000000000a \
  FAKE_ARTIFACT FAKE_TOOLCHAIN FAKE_RUNTIME_IDENTITY FAKE_CALLER_SECRET FAKE_REAL_DATE
unset FAKE_AZ_FAIL FAKE_NO_HSTS FAKE_ACCOUNT_TENANT FAKE_NO_SIGNED_IN_USER FAKE_VISIBLE_SUBSCRIPTIONS

failed=false
expect() { # description command...
  if ! "${@:2}" >/dev/null 2>&1; then
    printf '  FAILED: %s\n' "$1"
    failed=true
  fi
}

contains() { grep -qF -- "$2" "$1"; }
lacks() { ! grep -qF -- "$2" "$1"; }
equals() { [[ $1 == "$2" ]]; }
not() { ! "$@"; }

setup() {
  root=$(mktemp -d "${TMPDIR:-/tmp}/braid-qualify-test.XXXXXXXX")
  repo=$root/repo fake=$root/fake state=$root/state
  mkdir -p "$repo/deploy/azure" "$repo/deploy/container" "$repo/engines/ferrule-host/tests/fixtures" "$root/bin" "$fake" "$root/private"
  cp -R "$repository/deploy/azure/." "$repo/deploy/azure/"
  rm -rf "$repo/deploy/azure/tests"
  find "$repo/deploy/azure" -maxdepth 1 -name '*.bicepparam' ! -name main.example.bicepparam -delete
  cp "$repository/deploy/container/Dockerfile" "$repo/deploy/container/"
  cp "$repository/engines/ferrule-host/tests/fixtures/preview-orders.json" "$repo/engines/ferrule-host/tests/fixtures/"
  cp "$repository/.gitignore" "$repo/"
  git -C "$repo" init -q
  git -C "$repo" add -A
  git -C "$repo" -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false -c core.hooksPath=/dev/null \
    commit -qm fixture
  commit=$(git -C "$repo" rev-parse HEAD)
  # A private parameter file beside the template, ignored like a real one.
  printf "using './main.bicep'\n\nparam name = 'braid'\n" >"$repo/deploy/azure/qualify.bicepparam"
  cp "$tests"/fakes/* "$root/bin/"
  chmod +x "$root"/bin/*
  echo 0 >"$fake/clock"
  : >"$fake/calls.log"
  printf '%s\n%s\n' "$FAKE_CALLER_SECRET" "$FAKE_OWNER_OBJECT" >"$fake/secrets"
  printf '%s\n' "$FAKE_CALLER_SECRET" >"$root/private/caller.secret"
  chmod 600 "$root/private/caller.secret"
  echo '{"profiles": []}' >"$root/private/chat-profiles.json"
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj /CN=fake -keyout "$root/private/key.pem" \
    -out "$root/private/cert.pem" 2>/dev/null
  cat "$root/private/key.pem" "$root/private/cert.pem" >"$root/private/certificate.pem"
  chmod 600 "$root/private/certificate.pem"
  export FAKE_STATE=$fake FAKE_CERTIFICATE_PEM=$root/private/certificate.pem
  arguments=(--subscription "$subscription" --resource-group "$group" --location eastus2
    --parameters "$repo/deploy/azure/qualify.bicepparam" --entra-tenant-id "$tenant" --entra-client-id "$client"
    --chat-profiles "$root/private/chat-profiles.json" --endpoint-audience "$audience"
    --caller-client-id "$caller_client" --caller-secret-file "$root/private/caller.secret" --state-dir "$state"
    --confirm-provisioning)
}

# Runs the harness from the copy with the fakes first on PATH; input is its standard input. Every run must name the
# approved subscription on each Azure CLI call and never change the CLI's default.
run() { # input arguments...
  local input=$1
  shift
  : >"$fake/calls.log"
  : >"$fake/violations"
  set +e
  (cd "$repo" && PATH="$root/bin:$PATH" bash deploy/azure/qualify.sh "$@" <<<"$input" >"$root/output" 2>&1)
  code=$?
  set -e
  expect 'every Azure CLI call names the approved subscription and none changes the default' test ! -s "$fake/violations"
}

without() { # option: the default arguments without it and its value
  local index
  trial=()
  for ((index = 0; index < ${#arguments[@]}; index++)); do
    if [[ ${arguments[index]} == "$1" ]]; then
      [[ $1 == --confirm-provisioning ]] || index=$((index + 1))
      continue
    fi
    trial+=("${arguments[index]}")
  done
}

no_azure_call() {
  [[ ! -s $fake/calls.log ]]
}

# Only the read-only guard calls ran.
only_guard_calls() {
  ! grep -Ev '^az (account show|ad signed-in-user show|group exists)' "$fake/calls.log" | grep -q .
}

receipt_file() {
  find "$state" -name receipt.jsonl -print -quit
}

# No secret value (client secret, tokens, the PostgreSQL password) nor the owner's object ID reaches the output, the
# run's log, receipt or kept files, or any command's arguments.
no_secret_leaks() {
  local secret
  while read -r secret; do
    [[ -n $secret ]] || continue
    if grep -qF -- "$secret" "$root/output" "$fake/calls.log" || { [[ -d $state ]] && grep -rqF -- "$secret" "$state"; }; then
      return 1
    fi
  done <"$fake/secrets"
}

receipt_has() { # [jq-options...] filter: over the receipt's lines
  jq -se "$@" "$(receipt_file)"
}

# The initializer's Key Vault calls are left out: AzureSecretInitializationTests covers them, and their order
# follows bash's associative array.
normalize() {
  grep -Ev '^az keyvault (secret|certificate) (show|create|download)|^az keyvault secret set .* --name pg-' "$fake/calls.log" |
    perl -pe '
    BEGIN { $root = shift @ARGV; $commit = shift @ARGV; $tag = substr($commit, 0, 12); }
    s/\Q$root\E/<root>/g; s/\Q$commit\E/<commit>/g; s/\Q$tag\E/<tag>/g;
    s/wq-\d{8}-\d{6}-[0-9a-f]{6}/<run>/g; s/broken-[0-9a-f]{6}/broken-<run>/g;
    s/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ/<time>/g;
    s/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/$id{$1} \/\/= "<id-" . ++$ids . ">"/ge;
    s/(?<![0-9a-f])([0-9a-f]{64})(?![0-9a-f])/$hex{$1} \/\/= "<hex-" . ++$hexes . ">"/ge;
  ' "$root" "$commit"
}

scenario_refuses_missing_inputs() {
  local option
  for option in --subscription --resource-group --location --parameters --entra-tenant-id --entra-client-id \
    --chat-profiles --endpoint-audience --caller-client-id --caller-secret-file; do
    without "$option"
    run '' "${trial[@]}"
    expect "$option missing: exits 2" equals "$code" 2
    expect "$option missing: names the requirement" contains "$root/output" 'is required'
    expect "$option missing: no Azure call" no_azure_call
  done
}

scenario_refuses_without_confirmation() {
  without --confirm-provisioning
  run '' "${trial[@]}"
  expect 'exits 2' equals "$code" 2
  expect 'names the flag' contains "$root/output" '--confirm-provisioning is required'
  expect 'no Azure call' no_azure_call
}

scenario_refuses_credential_arguments() {
  local value=hunter2-$RANDOM$RANDOM
  for credential in --caller-secret --client-secret=$value --postgres-password --caller-token; do
    run '' "${arguments[@]}" "$credential" "$value"
    expect "$credential: exits 2" equals "$code" 2
    expect "$credential: names files as the only form" contains "$root/output" 'credentials are read only from private files'
    expect "$credential: never repeats the value" lacks "$root/output" "$value"
    expect "$credential: no Azure call" no_azure_call
  done
  chmod 644 "$root/private/caller.secret"
  run '' "${arguments[@]}"
  expect 'a readable credential file: exits 2' equals "$code" 2
  expect 'a readable credential file: is refused' contains "$root/output" 'readable by no one else'
  expect 'a readable credential file: no Azure call' no_azure_call
  chmod 600 "$root/private/caller.secret"
  run '' "${arguments[@]}" --caller-certificate-file "$root/private/certificate.pem"
  expect 'two credentials: exits 2' equals "$code" 2
  expect 'two credentials: is refused' contains "$root/output" 'use only one of'
}

# The fake itself refuses a call without the approved subscription and any change of the CLI's default.
scenario_fake_enforces_subscription() {
  local bin=$root/bin
  : >"$fake/violations"
  expect 'a call without --subscription is refused' not "$bin/az" group exists --name "$group"
  expect 'a call naming another subscription is refused' not "$bin/az" group exists --name "$group" \
    --subscription 5f0c3a2e-0000-4000-8000-0000000000ff
  expect 'az account set is refused' not "$bin/az" account set --subscription "$subscription"
  expect 'each refusal is recorded' test "$(wc -l <"$fake/violations")" -eq 3
  expect 'a call naming the approved subscription runs' "$bin/az" group exists --name "$group" --subscription "$subscription"
}

scenario_refuses_unavailable_subscription() {
  FAKE_VISIBLE_SUBSCRIPTIONS=5f0c3a2e-0000-4000-8000-0000000000ff run '' "${arguments[@]}"
  expect 'exits 2' equals "$code" 2
  expect 'names the subscription' contains "$root/output" 'cannot see the approved --subscription'
  expect 'only read the account' only_guard_calls
  expect 'checked no group' lacks "$fake/calls.log" 'group exists'
}

scenario_refuses_owner_account() {
  run '' "${arguments[@]}" --owner-subject retired-subject
  expect 'the retired subject option: exits 2' equals "$code" 2
  expect 'the retired subject option: is unknown' contains "$root/output" 'unknown option --owner-subject'
  expect 'the retired subject option: no Azure call' no_azure_call
  FAKE_ACCOUNT_TENANT=5f0c3a2e-0000-4000-8000-0000000000ff run '' "${arguments[@]}"
  expect 'another tenant: exits 2' equals "$code" 2
  expect 'another tenant: names it' contains "$root/output" 'is not in the --entra-tenant-id tenant'
  expect 'another tenant: only guard calls' only_guard_calls
  expect 'another tenant: checked no group' lacks "$fake/calls.log" 'group exists'
  FAKE_NO_SIGNED_IN_USER=1 run '' "${arguments[@]}"
  expect 'no signed-in user: exits 2' equals "$code" 2
  expect 'no signed-in user: names it' contains "$root/output" 'signed in as the user who becomes the first administrator'
  expect 'no signed-in user: only guard calls' only_guard_calls
  expect 'no signed-in user: checked no group' lacks "$fake/calls.log" 'group exists'
}

scenario_refuses_existing_group() {
  touch "$fake/group"
  run '' "${arguments[@]}"
  expect 'exits 2' equals "$code" 2
  expect 'names the group' contains "$root/output" "the resource group $group already exists"
  expect 'only guard calls' only_guard_calls
}

scenario_refuses_dirty_tree() {
  touch "$repo/untracked"
  run '' "${arguments[@]}"
  expect 'exits 2' equals "$code" 2
  expect 'names the tree' contains "$root/output" 'uncommitted or untracked changes'
  expect 'no Azure call' no_azure_call
}

scenario_declined_confirmation() {
  run no "${arguments[@]}"
  expect 'exits 2' equals "$code" 2
  expect 'showed what-if' contains "$fake/calls.log" 'az deployment group what-if'
  expect 'deployed nothing' lacks "$fake/calls.log" 'az deployment group create'
  expect 'deleted the empty group' contains "$fake/calls.log" "az group delete --name $group --yes"
  expect 'the group is gone' test ! -e "$fake/group"
  expect 'records the decline' receipt_has 'any(.[]; .kind == "guided" and .name == "provisioning" and .result == "declined")'
}

scenario_successful_run() {
  local expected=$tests/expected/successful-run.calls receipt
  run "$(printf 'yes\n%.0s' {1..11})" "${arguments[@]}"
  receipt=$(receipt_file)
  expect 'exits 0' equals "$code" 0
  if [[ ${BRAID_QUALIFY_UPDATE_EXPECTED:-} == 1 ]]; then normalize >"$expected"; fi
  if ! diff -u "$expected" <(normalize) >"$root/sequence.diff"; then
    printf '  FAILED: the command sequence differs from %s\n' "${expected#"$repository"/}"
    head -n 40 "$root/sequence.diff"
    failed=true
  fi
  expect 'initialize-secrets.sh wrote every certificate pin' \
    test "$(grep -c '^az keyvault certificate download' "$fake/calls.log")" -eq 7
  expect 'every receipt line is JSON' jq -e . "$receipt"
  expect 'the run passed' receipt_has 'last | .kind == "run" and .result == "pass"'
  expect 'no check failed' receipt_has 'all(.[]; .result != "fail" and .result != "declined")'
  expect 'every automated check passed' receipt_has '[.[] | select(.kind == "check" and .result == "pass") | .name] as $passed
    | all(("platform-deployed", "secrets-initialized", "images-built", "application-deployed", "database-roles",
      "migrations", "session-keys", "private-storage", "server-started", "health-hsts", "session-anonymous",
      "catalog-same-origin", "session-host-ready", "worker-running", "ferrule-service-ready", "no-docker-socket",
      "owner-binding", "registration", "browser-corroborated", "build-provenance", "tokens", "endpoint-deployed",
      "build-qualified", "target-registered", "release-activated", "caller-granted", "endpoint-ready", "first",
      "audit-first", "retry", "retry-replayed", "second", "rolled-back", "activation-refused", "after-refused-activation",
      "broken-revision", "during-broken-revision", "after-redeployment", "wrong-audience", "server-restarted",
      "ferrule-scaled", "worker-restarted", "retained-state", "replay-after-restart", "expired", "grant-revoked",
      "revoked", "revoked-replay", "ferrule-sandbox-admission", "no-docker-socket-final"); . as $name | $passed | index($name))'
  expect 'eleven operator confirmations, recorded apart from the checks' \
    receipt_has '[.[] | select(.kind == "guided" and .result == "confirmed")] | length == 11'
  expect 'the commit and five image digests are recorded' receipt_has --arg commit "$commit" \
    '[.[] | select(.kind == "fact" and .name == "image" and .evidence.commit == $commit) | .evidence.digest] | length == 5'
  expect 'the receipt is private' equals "$(stat -c %a "$receipt")" 600
  expect 'the run log is private' equals "$(stat -c %a "$(dirname "$receipt")/run.log")" 600
  expect 'the run directory is private' equals "$(stat -c %a "$(dirname "$receipt")")" 700
  expect 'working files are removed' test ! -e "$(dirname "$receipt")/work"
  expect 'the group is kept' lacks "$fake/calls.log" 'az group delete'
  expect 'prints the teardown command' contains "$root/output" 'deploy/azure/qualify.sh --teardown wq-'
  expect 'the owner input placeholder is restored' jq -e '. == {}' "$fake/owner-input"
  expect 'no secret leaks' no_secret_leaks
}

scenario_failure_partway() {
  FAKE_AZ_FAIL="containerapp job start --name braid-migrate" run "$(printf 'yes\n%.0s' {1..11})" "${arguments[@]}"
  expect 'exits 1' equals "$code" 1
  expect 'the receipt marks the failed step' receipt_has 'last | .kind == "run" and .result == "fail" and .evidence.step == "application"'
  expect 'the run did not pass' receipt_has 'all(.[]; .kind != "run" or .result != "pass")'
  expect 'the group is kept' lacks "$fake/calls.log" 'az group delete'
  expect 'says the group is kept for investigation' contains "$root/output" 'is kept for investigation'
  expect 'prints the teardown command' contains "$root/output" 'deploy/azure/qualify.sh --teardown wq-'
  expect 'working files are removed' test ! -e "$(dirname "$(receipt_file)")/work"
  expect 'no secret leaks' no_secret_leaks
}

scenario_failed_check_recorded() {
  FAKE_NO_HSTS=1 run "$(printf 'yes\n%.0s' {1..11})" "${arguments[@]}"
  expect 'exits 1' equals "$code" 1
  expect 'the receipt records the failed check' receipt_has 'any(.[]; .kind == "check" and .name == "health-hsts" and .result == "fail")'
  expect 'the run names it' receipt_has 'last | .result == "fail" and .evidence.check == "health-hsts"'
  expect 'no secret leaks' no_secret_leaks
}

scenario_teardown_at_end_after_failure() {
  FAKE_AZ_FAIL="containerapp job start --name braid-migrate" run "$(printf 'yes\n%.0s' {1..11})" "${arguments[@]}" --teardown-at-end
  expect 'exits 1' equals "$code" 1
  expect 'deleted the group' contains "$fake/calls.log" "az group delete --name $group --yes"
  expect 'the group is gone' test ! -e "$fake/group"
  expect 'records the teardown' receipt_has 'any(.[]; .kind == "teardown" and .result == "pass")'
}

scenario_teardown_scoping() {
  local run_id=wq-20261023-120000-0a1b2c teardown
  teardown=(--teardown "$run_id" --subscription "$subscription" --resource-group "$group" --state-dir "$state")
  touch "$fake/group"
  run '' "${teardown[@]}" --yes
  expect 'an untagged group: exits 2' equals "$code" 2
  expect 'an untagged group: is refused' contains "$root/output" "does not carry the run tag $run_id"
  expect 'an untagged group: is not deleted' lacks "$fake/calls.log" 'az group delete'
  echo wq-20261023-120000-ffffff >"$fake/group-tag"
  run '' "${teardown[@]}" --yes
  expect "another run's group: exits 2" equals "$code" 2
  expect "another run's group: is not deleted" lacks "$fake/calls.log" 'az group delete'
  echo "$run_id" >"$fake/group-tag"
  FAKE_VISIBLE_SUBSCRIPTIONS=5f0c3a2e-0000-4000-8000-0000000000ff run '' "${teardown[@]}" --yes
  expect 'another subscription: exits 2' equals "$code" 2
  expect 'another subscription: only read the account' only_guard_calls
  run nope "${teardown[@]}"
  expect 'no confirmation: exits 2' equals "$code" 2
  expect 'no confirmation: is not deleted' lacks "$fake/calls.log" 'az group delete'
  run "$group" "${teardown[@]}"
  expect 'the tagged group: exits 0' equals "$code" 0
  expect 'the tagged group: is deleted' contains "$fake/calls.log" "az group delete --name $group --yes"
  expect 'the tagged group: deletion is verified' test "$(grep -c '^az group exists' "$fake/calls.log")" -ge 2
  expect 'the tagged group: is gone' test ! -e "$fake/group"
  expect 'the teardown is recorded' receipt_has 'last | .kind == "teardown" and .result == "pass"'
}

all=(fake_enforces_subscription refuses_missing_inputs refuses_without_confirmation refuses_credential_arguments
  refuses_unavailable_subscription refuses_owner_account refuses_existing_group refuses_dirty_tree declined_confirmation
  successful_run failure_partway failed_check_recorded teardown_at_end_after_failure teardown_scoping)
status=0
scenarios=("$@")
((${#scenarios[@]})) || scenarios=("${all[@]}")
for scenario in "${scenarios[@]}"; do
  scenario=${scenario//-/_}
  declare -F "scenario_$scenario" >/dev/null || { echo "Unknown scenario $scenario" >&2 && exit 2; }
  failed=false
  setup
  "scenario_$scenario"
  if $failed; then
    printf 'not ok - %s (output: %s)\n' "$scenario" "$root/output"
    tail -n 15 "$root/output" | sed 's/^/  | /'
    status=1
  else
    printf 'ok - %s\n' "$scenario"
    rm -rf -- "$root"
  fi
done
exit "$status"
