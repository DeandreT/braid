#!/usr/bin/env bash
# Stops the local instance started by dev-up.sh: the worker and server units, then its PostgreSQL
# container and the configured Ollama container. Data, settings and images are kept.
#   --keep-ollama  leave the Ollama container running (for example when another instance uses it)
#   --purge        also remove this instance's PostgreSQL container and volume, image tags, state
#                  directory and private settings; never the shared Ollama container or models
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/dev-common.sh"
keep_ollama=false purge=false
for argument in "$@"; do
  case $argument in
    --keep-ollama) keep_ollama=true ;;
    --purge) purge=true ;;
    *) echo "Usage: $0 [--keep-ollama] [--purge]" >&2; exit 2 ;;
  esac
done

# The worker executes runs and the server owns their submission; each host settles its own children.
for unit in "$dev_worker_unit" "$dev_server_unit"; do
  if systemctl --user is-active --quiet "$unit"; then
    echo "Stopping $unit"
    systemctl --user stop "$unit"
  fi
  systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
done

if docker container inspect "$dev_postgres" >/dev/null 2>&1; then
  if ! dev_owned_container "$dev_postgres"; then
    echo "$dev_postgres does not belong to $dev_name; left unchanged." >&2
  elif dev_container_running "$dev_postgres"; then
    echo "Stopping $dev_postgres"
    docker stop --time 30 "$dev_postgres" >/dev/null
  fi
fi

if [[ -n $dev_ollama_container && $keep_ollama == false ]] && dev_container_running "$dev_ollama_container"; then
  echo "Stopping $dev_ollama_container"
  docker stop "$dev_ollama_container" >/dev/null
fi

if [[ $purge == true ]]; then
  if docker container inspect "$dev_postgres" >/dev/null 2>&1 && dev_owned_container "$dev_postgres"; then
    echo "Removing $dev_postgres and $dev_postgres_volume"
    docker container rm "$(docker container inspect --format '{{.Id}}' "$dev_postgres")" >/dev/null
    docker volume rm "$dev_postgres_volume" >/dev/null
  fi
  # Only this instance's own tags; an image ID shared with another tag stays in place.
  for tag in $(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E "^$dev_name/(ferrule-host|build-worker):" || true); do
    docker image rm "$tag" >/dev/null
  done
  echo "Removing $dev_home and $dev_config"
  # The private store keeps read-only directories; make them removable by their owner first.
  [[ ! -d $dev_home ]] || chmod -R u+rwX -- "$dev_home"
  rm -rf -- "$dev_home" "$dev_config"
fi
