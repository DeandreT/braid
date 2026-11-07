#!/usr/bin/env bash
# The CLI flows of deploy/compose/cli-acceptance-test.sh, inside its runner container. They run in a private
# D-Bus session with an unlocked gnome-keyring Secret Service, where MSAL keeps the CLI's token cache. Every
# command's exit status is checked, and documents are checked with jq. Tokens never reach this script.
set -euo pipefail

if [[ -z ${DBUS_SESSION_BUS_ADDRESS:-} ]]; then
  exec dbus-run-session -- bash "$0" "$@"
fi
# A throwaway keyring for this session only; its password protects nothing outside the container.
printf 'acceptance' | gnome-keyring-daemon --unlock --components=secrets >/dev/null

cd "$HOME"
# The public origin's port 443 is the ingress's TLS port; TLS still ends at the ingress under the public name.
socat TCP-LISTEN:443,bind=127.0.0.1,fork,reuseaddr "TCP:$WEFT_CLI_INGRESS" &
weft=$WEFT_CLI
step() { printf 'ok - %s\n' "$1"; }
fail() {
  printf 'FAILED: %s\n' "$1" >&2
  exit 1
}
b64url() { basenc --base64url -w0 "$1" | tr -d '='; }

"$weft" profile set acceptance --server "https://$WEFT_PUBLIC_HOST" --tenant "$WEFT_ENTRA_TENANT_ID" \
  --client-id "$WEFT_ACCEPTANCE_CLI_CLIENT_ID" --scope "api://$WEFT_ENTRA_CLIENT_ID/weft.api" --default || fail 'CLI profile configuration'
[[ $(stat -c %a "$HOME/.config/weft/config.json") == 600 ]] || fail 'the configuration file is not owner-only'

# Device-code sign-in. The issuer's test-only approval stands in for the user entering the code.
"$weft" login 2>login.err >login.out &
login=$!
code=
for _ in $(seq 1 60); do
  code=$(grep -o 'enter the code [A-Z]*' login.err | awk '{print $4}' || true)
  [[ -n $code ]] && break
  sleep 1
done
[[ -n $code ]] || fail 'the CLI showed no device code'
curl --fail --silent --show-error --cacert "$SSL_CERT_FILE" --data-urlencode "user_code=$code" \
  "https://login.microsoftonline.com/$WEFT_ENTRA_TENANT_ID/oauth2/v2.0/deviceapprove" >/dev/null
wait "$login" || { cat login.err >&2; fail 'device-code sign-in'; }
grep -q 'Signed in to acceptance' login.out || fail 'sign-in result'
# The cache lives in the keyring, never in a file beside the configuration.
! grep -rqs 'refresh_token\|access_token\|"secret"' "$HOME/.config/weft" || fail 'a token was written to a file'
step 'device-code sign-in with the token cache in the Secret Service keyring'

"$weft" whoami --json >whoami.json
jq -e --arg principal "$WEFT_ACCEPTANCE_PRINCIPAL_ID" '.context.actorPrincipalId == $principal and (.context | has("requestToken") | not)' \
  whoami.json >/dev/null || fail 'whoami identity'
step 'the server admits the signed-in user as the bound principal'

"$weft" guide list --json >guide.json
section=$(jq -er '.sections[0].id' guide.json) || fail 'guide contents'
"$weft" guide get "$section" --json >guide-section.json
jq -e --arg section "$section" '.id == $section and (.blocks | length > 0)' guide-section.json >/dev/null || fail 'guide section'
step 'authenticated guide contents and structured section read'

"$weft" projects create --name 'CLI acceptance' --description 'Created by the weft CLI' --json >project.json
project=$(jq -r '.receipt.project.projectId' project.json)
"$weft" projects list --all --json | jq -e --arg project "$project" '[.projects[].scope.projectId] | index($project)' >/dev/null ||
  fail 'the new project is not listed'
step 'project created and listed'

# The preview-orders fixture with JSON document boundaries, as the browser flows use it.
jq '.source_options = {json_document: true} | .target_options = {json_document: true}
  | (.extra_sources[], .extra_targets[]) |= (.options = {json_document: true})' \
  "$WEFT_ACCEPTANCE_FIXTURES/preview-orders.json" >orders-1.json
"$weft" mappings create --project "$project" --name 'Orders to shipments' --backend ferrule --json orders-1.json 2>upload.err >mapping.json ||
  { cat upload.err >&2; fail 'mapping creation'; }
jq -e '.outcome == "committedValid"' mapping.json >/dev/null || fail 'the first revision is not committed valid'
mapping=$(jq -r '.receipt.futureMapping.mappingId' mapping.json)
first=$(jq -r '.receipt.futureRevision.revisionId' mapping.json)
# The browser flows' later revision: the orderId target field renamed.
jq '(.target.kind.children[] | select(.name == "orderId") | .name) = "orderNumber"
  | (.root.bindings[] | select(.target_field == "orderId") | .target_field) = "orderNumber"' orders-1.json >orders-2.json
"$weft" revisions append --project "$project" --mapping "$mapping" --expected-head "$first" --json orders-2.json 2>append.err >append.json ||
  { cat append.err >&2; fail 'revision append'; }
second=$(jq -r '.receipt.futureRevision.revisionId' append.json)
"$weft" revisions list --project "$project" --mapping "$mapping" --json | jq -e '.revisions | length == 2' >/dev/null || fail 'revision list'
step 'mapping created from a native file and a second revision appended'

for revision in "$first" "$second"; do
  "$weft" reports projection ensure --project "$project" --mapping "$mapping" --revision "$revision" --json >projection.json ||
    fail 'projection ensure'
  jq -e '.state == "available"' projection.json >/dev/null || fail 'projection report'
done
"$weft" reports validation ensure --project "$project" --mapping "$mapping" --revision "$first" --json >validation.json || fail 'validation ensure'
jq -e '.state == "available" and .report.isValid == true' validation.json >/dev/null || fail 'validation report'
"$weft" compare --project "$project" --mapping "$mapping" --left-revision "$first" --right-revision "$second" --json >comparison.json ||
  fail 'comparison'
jq -e '.schemaVersion | length > 0' comparison.json >/dev/null || fail 'comparison document'
step 'reports ensured and the two revisions compared'

printf '%s' '{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}' >order.json
printf '%s' '{"currency":"EUR"}' >rates.json
"$weft" previews create --project "$project" --mapping "$mapping" --revision "$first" --input order.json --named-input rates=rates.json \
  --parameter region=west --parameter express:boolean=true --wait --json >preview.json || fail 'preview run'
run=$(jq -r '.runId' preview.json)
jq -e '.outcome == "succeeded" and (.outputs | length >= 1)' preview.json >/dev/null || fail 'preview outcome'
expected='[]'
for ordinal in $(jq -r '.outputs[].ordinal' preview.json); do
  "$weft" previews download "$run" --ordinal "$ordinal" --output "output-$ordinal.json" 2>/dev/null || fail 'preview download'
  name=$(jq -c --argjson ordinal "$ordinal" '.outputs[] | select(.ordinal == $ordinal) | if .boundaryId == "primary-output" then null else .targetName end' preview.json)
  expected=$(jq -c --argjson name "$name" --arg bytes "$(b64url "output-$ordinal.json")" '. + [{name: $name, bytesBase64Url: $bytes}]' <<<"$expected")
done
step 'preview ran and its outputs downloaded with verified digests'

"$weft" regression capabilities --project "$project" --mapping "$mapping" --json >capabilities.json
jq -n --slurpfile capabilities capabilities.json --slurpfile preview preview.json --arg revision "$first" \
  --arg primary "$(b64url order.json)" --arg rates "$(b64url rates.json)" --argjson expected "$expected" '{
    name: "CLI acceptance baseline", revisionId: $revision, runtime: $capabilities[0].runtime,
    currentDateTime: $preview[0].currentDateTime, target: {kind: "all", name: null}, limits: $capabilities[0].limits,
    primaryBase64Url: $primary, namedInputs: [{name: "rates", bytesBase64Url: $rates}],
    parameters: [{name: "region", kind: "string", value: "west"}, {name: "express", kind: "boolean", value: "true"}],
    comparisonPolicy: $capabilities[0].comparisonPolicy, expected: $expected}' >case.json
"$weft" regression cases create --project "$project" --mapping "$mapping" --definition case.json --json >case-saved.json ||
  fail 'regression case'
case_id=$(jq -r '.caseId' case-saved.json)
"$weft" regression runs create --project "$project" --mapping "$mapping" --case "$case_id" --version 1 --wait --json >case-run.json ||
  { jq -c '.run.state, .comparison' case-run.json >&2 || true; fail 'regression run'; }
jq -e '.comparison.outcome == "passed"' case-run.json >/dev/null || fail 'regression comparison'
step 'regression case saved from the preview and its run passed'

"$weft" builds environment-capabilities --project "$project" --json >environment-capabilities.json
jq -e '.canCreateVersions == true' environment-capabilities.json >/dev/null || fail 'build environment admission'
environment=$(cat /proc/sys/kernel/random/uuid)
"$weft" builds environments create --project "$project" "$environment" 1 \
  --profile-id "$(jq -r '.profile.id' environment-capabilities.json)" \
  --profile-version "$(jq -r '.profile.version' environment-capabilities.json)" --json >environment.json || fail 'build environment'
"$weft" builds environments get --project "$project" "$environment" 1 --json >environment-read.json
jq -e --arg environment "$environment" '.reference.environmentId == $environment and .active == true' environment-read.json >/dev/null ||
  fail 'build environment read'
"$weft" builds capabilities --project "$project" --mapping "$mapping" --json >build-capabilities.json
jq -e '.approveBuilds == true and .submitBuilds == true' build-capabilities.json >/dev/null || fail 'build admission'
"$weft" builds approvals create --project "$project" --mapping "$mapping" --revision "$first" \
  --environment "$environment" --environment-version 1 --evidence "$(jq -r '.run.runId' case-run.json)" --json >approval.json ||
  fail 'build approval'
approval=$(jq -r '.approval.approvalId' approval.json)
build=$(jq -r '.approval.buildId' approval.json)
"$weft" builds runs create --project "$project" --mapping "$mapping" --approval "$approval" --wait --json >build.json ||
  { jq -c '.state, .outcome' build.json >&2 || true; fail 'compiled build'; }
jq -e --arg build "$build" '.buildId == $build and .state == "succeeded" and .artifact.byteCount > 0 and (.artifact.sha256 | length == 64)' \
  build.json >/dev/null || fail 'compiled artifact metadata'
"$weft" builds runs get --project "$project" --mapping "$mapping" "$build" --json >build-read.json
cmp --silent build.json build-read.json || fail 'retained build read'
"$weft" builds approvals revoke --project "$project" --mapping "$mapping" "$approval" --json >approval-revoked.json
jq -e '.revoked == true' approval-revoked.json >/dev/null || fail 'build approval revocation'
step 'build environment and exact approval created; compiled build succeeded and its approval was revoked'

# The local model can need more than one turn for a grounded answer on a slow host.
answered=false
for attempt in 1 2 3; do
  if "$weft" chat ask --project "$project" --mapping "$mapping" --revision "$first" --json \
    'Which fields does this mapping write? Cite the report.' >chat.json 2>chat.err &&
    jq -e '.outcome == "completed" and (.text | length > 0) and (.citations | length > 0)' chat.json >/dev/null; then
    answered=true
    break
  fi
  echo "chat attempt $attempt did not complete" >&2
done
[[ $answered == true ]] || { cat chat.err >&2; fail 'chat'; }
step "chat answered with $(jq '.citations | length' chat.json) citation(s)"

"$weft" chat workspace availability --json >workspace-availability.json
jq -e '.profiles | any(.payloadsAllowed == false)' workspace-availability.json >/dev/null || fail 'workspace profile policy'
"$weft" chat workspace ask --focus-project "$project" --focus-mapping "$mapping" --focus-revision "$first" --json \
  'Find the order mappings in the catalog and cite them.' >workspace-chat.json 2>workspace-chat.err ||
  { cat workspace-chat.err >&2; fail 'workspace chat'; }
jq -e --arg mapping "$mapping" '.outcome == "completed" and (.text | length > 0) and
  any(.citations[]; .citation.kind == "mapping" and .citation.mappingId == $mapping) and (.sharedData | length == 0)' \
  workspace-chat.json >/dev/null || fail 'workspace mapping citation or payload policy'
workspace_conversation=$(jq -r '.conversationId' workspace-chat.json)
"$weft" chat workspace conversations get "$workspace_conversation" --json >workspace-detail.json
jq -e --arg revision "$first" '.conversation.kind == "workspace" and .conversation.focus.revisionId == $revision' \
  workspace-detail.json >/dev/null || fail 'workspace exact focus'
step 'workspace question cited the mapping with exact focus and no payload disclosure'

"$weft" logout 2>/dev/null
code=0
"$weft" projects list >/dev/null 2>&1 || code=$?
((code == 3)) || fail "after sign-out a command exited $code, not 3"
step 'signed out; later commands require sign-in'
printf 'cli-project-id=%s\n' "$project"
