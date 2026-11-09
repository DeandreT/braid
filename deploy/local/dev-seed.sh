#!/usr/bin/env bash
# Fills the local instance started by dev-up.sh with demo projects, mappings and activity through
# the product API (deploy/local/demo-data). Rerunnable: fixed keys replay instead of duplicating.
# See docs/development.md ("Demo data").
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/dev-common.sh"
instance=$dev_config/instance.env
[[ -f $instance ]] || { echo "dev-seed: $instance is missing; run dev-up.sh first." >&2; exit 1; }
source "$instance"
[[ -n ${WEFT_DEV_PROJECT_ID:-} ]] || { echo 'dev-seed: the default project does not exist yet; run dev-up.sh first.' >&2; exit 1; }
curl -fsS --max-time 10 "$dev_origin/api/v1/health" >/dev/null || { echo "dev-seed: nothing answers at $dev_origin." >&2; exit 1; }

WEFT_SEED_ORIGIN=$dev_origin WEFT_SEED_ORGANIZATION_ID=$WEFT_DEV_ORGANIZATION_ID WEFT_SEED_PROJECT_ID=$WEFT_DEV_PROJECT_ID \
  WEFT_SEED_FERRULE_EXAMPLES=${WEFT_DEV_FERRULE_EXAMPLES:-} WEFT_SEED_NAMESPACE=${WEFT_DEV_SEED_NAMESPACE:-weft-demo-v1} \
  WEFT_SEED_CHAT=${WEFT_DEV_SEED_CHAT:-1} exec node "$script_dir/demo-data/seed.mjs"
