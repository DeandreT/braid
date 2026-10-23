#!/usr/bin/env bash
# A clean EXIT alone is not proof that every acceptance stage completed. Signal
# traps also make the failure status available before the EXIT cleanup runs.
acceptance_complete=false
trap 'exit 130' INT
trap 'exit 143' TERM

acceptance_result() {
  local status=$1
  ((status == 0)) || return "$status"
  [[ $acceptance_complete == true ]]
}
