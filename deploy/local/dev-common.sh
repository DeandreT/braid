# Shared settings for dev-up.sh, dev-down.sh and dev-status.sh; sourced, never executed.
# Every name, port and path derives from BRAID_DEV_NAME unless overridden, so a second instance
# with its own name, ports and directories never touches the first.

dev_name=${BRAID_DEV_NAME:-braid-dev}
[[ $dev_name =~ ^[a-z][a-z0-9-]{0,30}$ ]] || { echo 'BRAID_DEV_NAME must be lowercase letters, digits and hyphens.' >&2; exit 2; }
dev_port=${BRAID_DEV_PORT:-4100}
dev_postgres_port=${BRAID_DEV_POSTGRES_PORT:-55432}
dev_home=${BRAID_DEV_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/$dev_name}
dev_config=${BRAID_DEV_CONFIG:-${XDG_CONFIG_HOME:-$HOME/.config}/$dev_name}
dev_ollama_container=${BRAID_DEV_OLLAMA_CONTAINER-braid-ollama}
dev_ollama_url=${BRAID_DEV_OLLAMA_URL:-http://127.0.0.1:11434}
for value in "$dev_port" "$dev_postgres_port"; do
  [[ $value =~ ^[1-9][0-9]{0,4}$ ]] && ((value <= 65535)) || { echo 'Ports must be decimal TCP ports.' >&2; exit 2; }
done
[[ $dev_home == /* && $dev_config == /* ]] || { echo 'BRAID_DEV_HOME and BRAID_DEV_CONFIG must be absolute.' >&2; exit 2; }
[[ $dev_ollama_url =~ ^http://(127\.0\.0\.1|\[::1\]):[0-9]+$ ]] || { echo 'BRAID_DEV_OLLAMA_URL must be a loopback http://IP:port origin.' >&2; exit 2; }

dev_origin=http://127.0.0.1:$dev_port
dev_postgres=$dev_name-postgres
dev_postgres_volume=$dev_name-postgres-data
dev_label=io.braid.dev.instance
dev_server_unit=$dev_name-server
dev_worker_unit=$dev_name-worker
dev_logs=$dev_home/logs

# Containers this instance created carry its label; nothing else is stopped or removed.
dev_owned_container() {
  [[ $(docker container inspect --format "{{index .Config.Labels \"$dev_label\"}}" "$1" 2>/dev/null) == "$dev_name" ]]
}

dev_container_running() {
  [[ $(docker container inspect --format '{{.State.Running}}' "$1" 2>/dev/null) == true ]]
}
