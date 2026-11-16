# Local host unit composition; sourced by dev-up.sh, never executed on its own.
# 120 seconds of create settlement + 120 seconds of independent cleanup, with
# 60 seconds for host cancellation/draining and bounded CLI-exit overhead.
# This is only the service manager's stop grace, not a worker execution budget.
readonly dev_unit_stop_seconds=300

dev_docker_budgets() {
  local name
  for name in BRAID_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS BRAID_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS; do
    if [[ -n ${!name:-} ]]; then printf '%s=%s\n' "$name" "${!name}"; fi
  done
}

# Read the active service's numeric microsecond property, not systemctl's human
# time formatting. Loaded transient services cannot change this timeout on all
# supported systemd versions, so an insufficient/unknown allowance is not upgraded
# by assumption: its owner must first arrange a safe, settled manual stop.
dev_unit_has_stop_grace() {
  local unit_name=$1 response object_path actual_usec
  [[ $unit_name == *.service ]] || unit_name+=.service
  response=$(busctl --user --timeout=5s call org.freedesktop.systemd1 /org/freedesktop/systemd1 \
    org.freedesktop.systemd1.Manager GetUnit s "$unit_name" 2>/dev/null) || return 1
  [[ $response =~ ^o\ \"(/org/freedesktop/systemd1/unit/[A-Za-z0-9_]+)\"$ ]] || return 1
  object_path=${BASH_REMATCH[1]}
  response=$(busctl --user --timeout=5s get-property org.freedesktop.systemd1 "$object_path" \
    org.freedesktop.systemd1.Service TimeoutStopUSec 2>/dev/null) || return 1
  [[ $response =~ ^t\ ([0-9]+)$ ]] || return 1
  actual_usec=${BASH_REMATCH[1]}
  # Also reject infinity and values outside safe shell integer arithmetic.
  [[ ${#actual_usec} -le 15 ]] || return 1
  ((10#$actual_usec >= dev_unit_stop_seconds * 1000000))
}

# Callers provide the instance paths, SDK identity, step/fail functions and log_offset array.
start_unit() { # unit host-name environment-file
  local unit=$1 host=$2 environment=$3 stamp assembly=$app/$2/$2.dll
  local -a unit_arguments=(
    --user --quiet --unit "$unit" --description "Braid local $host ($dev_name)"
    --property "EnvironmentFile=$environment" --property "WorkingDirectory=$app/$host"
    --property "StandardOutput=append:$dev_logs/$unit.log" --property "StandardError=append:$dev_logs/$unit.log"
    --property KillMode=mixed --property "TimeoutStopSec=$dev_unit_stop_seconds"
    --setenv "DOTNET_ROOT=$dotnet_root" --setenv "PATH=$dotnet_root:/usr/local/bin:/usr/bin:/bin"
    --setenv "TMPDIR=$dev_home/tmp/$unit" --setenv DOTNET_CLI_TELEMETRY_OPTOUT=1 --setenv DOTNET_NOLOGO=1
    --setenv DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
    "$dotnet" "$assembly"
  )
  # Fingerprint exactly the generated invocation, plus private settings and binary contents.
  # Only their hashes enter the stamp stream; no private environment values are printed.
  stamp=$({ printf '%s\0' "${unit_arguments[@]}"; sha256sum -- "$environment" "$assembly"; } | sha256sum | cut -d' ' -f1)
  if systemctl --user is-active --quiet "$unit"; then
    [[ $(cat "$dev_home/run/$unit.stamp" 2>/dev/null) != "$stamp" ]] || return 0
    if ! dev_unit_has_stop_grace "$unit"; then
      fail "Unit $unit has insufficient or unconfirmed active stop grace; refusing automatic stop. Let its original work and cleanup settle, then have its owner perform a safe manual stop before rerunning."
      return 1
    fi
    step "Restarting $unit"
    systemctl --user stop "$unit"
  fi
  systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
  [[ $host != Braid.Server || -z $(ss -ltnH "sport = :$dev_port") ]] || fail "Port $dev_port is in use; set BRAID_DEV_PORT."
  install -d -m 0700 "$dev_home/tmp/$unit"
  step "Starting $unit (log: $dev_logs/$unit.log)"
  printf '\n==== %s %s %s\n' "$(date -u +%FT%TZ)" "$unit" "$build_key" >>"$dev_logs/$unit.log"
  log_offset[$unit]=$(stat -c %s "$dev_logs/$unit.log")
  systemd-run "${unit_arguments[@]}"
  printf '%s\n' "$stamp" >"$dev_home/run/$unit.stamp"
}
