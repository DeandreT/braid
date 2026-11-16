#!/usr/bin/env bash
# Reports the state of the local instance started by dev-up.sh, without printing any secret.
# Exits nonzero when a part of the instance is not running.
set -uo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/dev-common.sh"
healthy=true
row() { printf '  %-*s %s\n' $((${#dev_name} + 9)) "$1" "$2"; }

echo "Braid local instance $dev_name"
for unit in "$dev_server_unit" "$dev_worker_unit"; do
  state=$(systemctl --user is-active "$unit" 2>/dev/null)
  row "$unit" "${state:-inactive}"
  [[ $state == active ]] || healthy=false
done
for container in "$dev_postgres" ${dev_ollama_container:+"$dev_ollama_container"}; do
  state=$(docker container inspect --format '{{.State.Status}}' "$container" 2>/dev/null || echo absent)
  row "$container" "$state"
  [[ $state == running ]] || healthy=false
done
if curl -fsS --max-time 5 "$dev_origin/api/v1/health" >/dev/null 2>&1; then row health "ok ($dev_origin)"
else row health "not answering ($dev_origin)"; healthy=false; fi
if curl -fsS --max-time 5 "$dev_ollama_url/api/tags" >/dev/null 2>&1; then row ollama "ok ($dev_ollama_url)"
else row ollama "not answering ($dev_ollama_url)"; healthy=false; fi
if [[ -r $dev_config/server.env ]]; then
  row image "$(sed -n 's/^BRAID_FERRULE_WORKER_IMAGE=//p' "$dev_config/server.env")"
fi
row url "$dev_origin/catalog"
row logs "$dev_logs"
row settings "$dev_config"
$healthy
