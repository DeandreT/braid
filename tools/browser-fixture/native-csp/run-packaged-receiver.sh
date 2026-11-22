#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_CHROME:?paired executable}" "${BRAID_DRIVER:?paired package directory}" "${BRAID_CHROME_SHA256:?admitted executable digest}" "${BRAID_BUNDLE_SHA256:?admitted bundle digest}" "${BRAID_PACKET:?transfer directory}" "${BRAID_SCRATCH:?private scratch parent}"
case ${1:-} in success|missing|wrong|late|hold-challenge|lost-reply|replacement|target-close|refused-enrollment) control=catalog-receiver-control.mjs;; fault-free) control=catalog-fault-free-control.mjs;; *) exit 2;; esac
fixture=$(cd "$(dirname "$0")" && pwd); packet=$(realpath "$BRAID_PACKET"); umask 077; run=$(mktemp -d "$BRAID_SCRATCH/br.XXXXXXXX"); short=$(mktemp -d /tmp/opencode/br.XXXXXX); trap 'rm -rf -- "$short" "$run"' EXIT
mkdir "$run/h" "$run/t"; ln -s "$run/t" "$short/t"; ulimit -c 0
python3 - "$packet" <<'PY'
import hashlib,json,pathlib,sys
p=pathlib.Path(sys.argv[1]);sha=lambda path:hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
def manifest(name,pin):
 f=p/'meta'/name
 if sha(f)!=pin:raise ValueError('wrong '+name)
 return json.loads(f.read_text())
def check(root, hashes):
 actual={str(f.relative_to(root)) for f in root.rglob('*') if f.is_file()};assert actual==set(hashes),'wrong file inventory'
 for name,digest in hashes.items():
  if sha(root/name)!=digest:raise ValueError('wrong file: '+name)
check(p/'publications/server',manifest('publication-hashes.json','79a924132f63b98e77a5f8b84cdcd3e2608e7348cd8f00af3884133a1caab5c9')['server']['hashes']);prefix='runtime/dotnet/';check(p/prefix,{k.removeprefix(prefix):v for k,v in manifest('runtime-hashes.json','20f9deab3591e880b4cf3dabac795e35b7328fda2056ccacf4d77062969d085d').items() if k.startswith(prefix)})
PY
node "$fixture/build-catalog-ui-fixture.mjs" "$packet/workspace" "$run/ui.cjs"
env -i PATH=/usr/bin HOME="$run/h" TMPDIR="$short/t" DOTNET_ROOT="$packet/runtime/dotnet" DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false BRAID_DRIVER="$BRAID_DRIVER" BRAID_CHROME="$BRAID_CHROME" BRAID_BUNDLE_SHA256="$BRAID_BUNDLE_SHA256" BRAID_CHROME_SHA256="$BRAID_CHROME_SHA256" BRAID_UI_FIXTURE="$run/ui.cjs" BRAID_SERVER="$packet/publications/server/Braid.Server.dll" NODE_PATH="$packet/workspace/node_modules" unshare --user --map-current-user --net --keep-caps bash -ec 'ip link set lo up; test -z "$(ip route show default)"; exec setpriv --inh-caps=-all --ambient-caps=-all --bounding-set=-all timeout 60 node "$1" "$2"' -- "$fixture/$control" "$1"
