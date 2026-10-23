# Settlement of compiled-runtime containers for deploy/compose/endpoint-acceptance-test.sh; sourced, not run.
# A run's runtime containers carry the label they inherit from its run-unique runtime image, and only those
# are ever selected. Manual removal settles leftovers but never lets that run pass: each removal is printed
# with its evidence, and settlement_status fails once anything was removed, so a clean result needs a new run.

settled=0

# runtime_containers RUN: the run's runtime containers, in any state.
runtime_containers() {
  docker ps --all --quiet --filter "label=io.weft.acceptance.run=$1"
}

# settle RUN REASON: prints each remaining runtime container's evidence, removes it and counts it.
settle() {
  local run=$1 reason=$2 container evidence
  for container in $(runtime_containers "$run"); do
    evidence=$(docker inspect --format \
      '{{.Name}} {{.State.Status}} created={{.Created}} started={{.State.StartedAt}} finished={{.State.FinishedAt}}' \
      "$container") || evidence="$container (not inspectable)"
    printf 'Settled runtime container (%s): %s\n' "$reason" "$evidence" >&2
    docker rm --force "$container" >/dev/null || return 1
    settled=$((settled + 1))
  done
}

# settlement_status: fails once any runtime container was removed manually.
settlement_status() {
  ((settled == 0)) && return 0
  printf 'Manual settlement removed %d runtime container(s); this run cannot pass, and a clean result needs a new run.\n' \
    "$settled" >&2
  return 1
}
