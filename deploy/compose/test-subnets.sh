#!/usr/bin/env bash
# Explicit small subnets for the networks of a test Compose project. A network without one takes a
# whole /16 or /20 from the daemon's default address pools, which concurrent test runs exhaust; see
# "Docker test networks" in docs/development.md. Sourced by the Compose test scripts.

test_subnet_range=${WEFT_TEST_SUBNET_RANGE:-198.19.0.0/16}
test_subnet_prefix=27
test_subnet_attempts=32

ipv4_number() {
  local IFS=.
  local -a octets
  read -r -a octets <<<"$1"
  echo $(((octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]))
}

ipv4_text() {
  echo "$(($1 >> 24 & 255)).$(($1 >> 16 & 255)).$(($1 >> 8 & 255)).$(($1 & 255))"
}

# Prints the first and last address of an IPv4 network in canonical form, or fails.
ipv4_bounds() {
  local address=${1%/*} size=${1#*/} first
  local octet='(0|[1-9][0-9]{0,2})'
  [[ $1 =~ ^$octet(\.$octet){3}/(0|[1-9][0-9]?)$ ]] && ((size <= 32)) || return 1
  first=$(ipv4_number "$address")
  [[ $(ipv4_text "$first") == "$address" ]] && ((first % (1 << (32 - size)) == 0)) || return 1
  echo "$first $((first + (1 << (32 - size)) - 1))"
}

# write_test_subnets <override> <network>...: writes a Compose override that gives each network a
# distinct /27 of WEFT_TEST_SUBNET_RANGE overlapping no existing Docker network. Candidates are
# random; each network tries at most 32 before the run fails with an exhaustion error.
write_test_subnets() {
  local override=$1 network subnet bounds first last count attempts index start end overlaps taken
  shift
  local -a taken_first taken_last tried
  # The override stays a valid Compose file at every step, so cleanup can still render the stack.
  echo 'networks:' >"$override"
  read -r first last < <(ipv4_bounds "$test_subnet_range") && ((${test_subnet_range#*/} <= test_subnet_prefix)) || {
    echo "WEFT_TEST_SUBNET_RANGE must be an IPv4 network of /$test_subnet_prefix or larger in canonical form, such as 198.19.0.0/16." >&2
    return 1
  }
  count=$(((last - first + 1) >> (32 - test_subnet_prefix)))
  attempts=$((count < test_subnet_attempts ? count : test_subnet_attempts))
  while read -r subnet; do
    bounds=$(ipv4_bounds "$subnet") || continue
    taken_first+=("${bounds% *}")
    taken_last+=("${bounds#* }")
  done < <(docker network ls --quiet | xargs -r docker network inspect \
    --format '{{range .IPAM.Config}}{{println .Subnet}}{{end}}' 2>/dev/null | grep -E '^[0-9.]+/[0-9]+$')
  for network; do
    tried=()
    subnet=
    while ((${#tried[@]} < attempts)); do
      index=$((((RANDOM << 15) | RANDOM) % count))
      [[ " ${tried[*]} " == *" $index "* ]] && continue
      tried+=("$index")
      start=$((first + (index << (32 - test_subnet_prefix))))
      end=$((start + (1 << (32 - test_subnet_prefix)) - 1))
      overlaps=false
      for taken in "${!taken_first[@]}"; do
        ((start <= taken_last[taken] && taken_first[taken] <= end)) && { overlaps=true; break; }
      done
      [[ $overlaps == false ]] && { subnet=$(ipv4_text "$start")/$test_subnet_prefix; break; }
    done
    [[ -n $subnet ]] || {
      echo "No free /$test_subnet_prefix subnet for the $network test network in $test_subnet_range after $attempts candidates;" \
        'remove leaked test networks or set WEFT_TEST_SUBNET_RANGE to a free IPv4 range.' >&2
      return 1
    }
    taken_first+=("$start")
    taken_last+=("$end")
    printf '  %s:\n    ipam:\n      config:\n        - subnet: %s\n' "$network" "$subnet" >>"$override"
  done
}

# Whether a failed `compose up` lost a chosen subnet to a network created in the meantime. The
# caller then removes the project's networks, writes new subnets and starts again.
test_subnets_overlapped() {
  grep -q -i -E 'Pool overlaps|invalid pool request' "$1"
}

# start_on_test_subnets <override> <log> <networks> <compose up argument>...: writes new subnets for
# the space-separated <networks> to <override>, which the caller's compose function includes, and
# runs `compose up` with its stderr in <log>. A start that lost a subnet to a network created in the
# meantime removes the project's networks and starts again on new subnets, up to three starts.
start_on_test_subnets() {
  local override=$1 log=$2 networks=$3 start
  shift 3
  for start in 1 2 3; do
    # shellcheck disable=SC2086 # network names are single words
    write_test_subnets "$override" $networks 2>"$log" || return 1
    compose up "$@" >/dev/null 2>"$log" && return 0
    ((start < 3)) && test_subnets_overlapped "$log" || return 1
    compose down --remove-orphans >/dev/null 2>&1 || return 1
  done
}
