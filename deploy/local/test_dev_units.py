"""Exercise real unit composition with stub services, never Docker or systemd.

Run: py -3.12 -m unittest discover -s deploy/local -p test_dev_units.py -v
Windows uses the existing Git Bash; WEFT_TEST_BASH can select another Bash.
"""

import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
import uuid


LOCAL = Path(__file__).resolve().parent
HELPER = LOCAL / "dev-units.sh"
CREATE = "WEFT_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS"
CLEANUP = "WEFT_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS"
OPTIONS = LOCAL.parents[1] / "dotnet/src/Weft.MappingBackends/Workers/DockerInspectionWorkerOptions.cs"
MAXIMUM = int(re.search(r"TimeSpan Maximum = TimeSpan.FromSeconds\((\d+)\)", OPTIONS.read_text()).group(1))

HARNESS = r'''#!/usr/bin/env bash
set -euo pipefail
umask 077
# Only Git Bash/POSIX tools and these stubs are available, not installed host services.
export PATH=/usr/bin:/bin
root=$1
source "$2"
mode=$4
dev_home=$root/home
dev_config=$root/private
dev_logs=$root/logs
app=$root/app
dev_name=fixture
build_key=fixture-build
dev_port=59991
dev_origin=http://127.0.0.1:59991
dotnet_root=/fixture/sdk
dotnet=$dotnet_root/dotnet
storage_root=$root/storage
worker_image=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
chat_profiles=$root/private/chat.json
WEFT_DEV_ORGANIZATION_ID=11111111-1111-1111-1111-111111111111
WEFT_DEV_PRINCIPAL_ID=22222222-2222-2222-2222-222222222222
fixture_secret=$WEFT_UNIT_TEST_SECRET
declare -A log_offset
step() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }
connection() { printf 'Host=fixture;Password=%s' "$fixture_secret"; }
docker() { printf 'Unexpected Docker invocation.\n' >&2; exit 99; }
ss() { return 0; }
# Native Windows cannot apply Linux directory modes. Validate the production
# request without substituting Windows ACL behavior for this unit-settings test.
install() {
  [[ $# == 4 && $1 == -d && $2 == -m && $3 == 0700 && $4 == "$dev_home/tmp/"* ]] || fail
  mkdir -p -- "$4"
}
systemctl() {
  local unit=${!#}
  case $2 in
    is-active) [[ -f $root/state/$unit.active ]] ;;
    stop)
      [[ $(cat "$root/state/$unit.grace") -ge $((dev_unit_stop_seconds * 1000000)) ]] || fail 'Unsafe stop attempted.'
      printf 'stop %s\n' "$unit" >>"$root/events"; rm "$root/state/$unit.active" ;;
    reset-failed) return 0 ;;
    *) fail ;;
  esac
}
busctl() {
  local unit
  case $3 in
    call)
      unit=${!#}; unit=${unit%.service}; unit=${unit//-/_2d}
      printf 'o "/org/freedesktop/systemd1/unit/%s_2eservice"\n' "$unit" ;;
    get-property)
      unit=${5##*/}; unit=${unit%_2eservice}; unit=${unit//_2d/-}
      printf 'grace %s\n' "$unit" >>"$root/events"
      [[ $mode != grace-unavailable ]] || return 1
      if [[ $mode == grace-malformed ]]; then printf 't infinity\n'
      else printf 't %s\n' "$(cat "$root/state/$unit.grace")"; fi ;;
    *) fail 'Unexpected bus operation.' ;;
  esac
}
systemd-run() {
  local count=0 unit= previous= argument grace=
  [[ ! -f $root/calls/count ]] || count=$(cat "$root/calls/count")
  count=$((count + 1))
  printf '%s\n' "$count" >"$root/calls/count"
  printf '%s\0' "$@" >"$root/calls/$count.args"
  for argument in "$@"; do
    [[ $previous != --unit ]] || unit=$argument
    [[ $argument != TimeoutStopSec=* ]] || grace=${argument#TimeoutStopSec=}
    previous=$argument
  done
  [[ -n $unit ]] || fail
  touch "$root/state/$unit.active"
  printf '%s\n' "$((grace * 1000000))" >"$root/state/$unit.grace"
  printf 'run %s\n' "$unit" >>"$root/events"
}
# This is the maintained environment-file block from dev-up.sh, not a copy of it.
source "$3"
[[ $mode != prepare ]] || exit 0
if [[ $mode == server-env-change ]]; then printf 'WEFT_UNIT_FIXTURE_SETTING=changed\n' >>"$server_env"; fi
start_unit fixture-server Weft.Server "$server_env"
start_unit fixture-worker Weft.Worker "$worker_env"
'''


def bash_path(path):
    path = Path(path).resolve()
    return "/" + path.drive[0].lower() + path.as_posix()[2:] if os.name == "nt" else str(path)


def find_bash():
    if os.environ.get("WEFT_TEST_BASH"):
        return os.environ["WEFT_TEST_BASH"]
    if os.name == "nt":
        git = shutil.which("git")
        candidates = [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"]
        if git:
            candidates.append(Path(git).resolve().parents[1] / "bin/bash.exe")
        for candidate in candidates:
            if candidate.is_file():
                return str(candidate)
    elif shutil.which("bash"):
        return shutil.which("bash")
    raise RuntimeError("Bash is required. Set WEFT_TEST_BASH to an existing Bash executable.")


class DevUnitSettingsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="weft unit fixture ", dir=os.environ.get("WEFT_TEST_TEMP_ROOT"))
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.secret = "private-fixture-" + uuid.uuid4().hex
        self.output = ""
        for directory in ["home/run", "private", "logs", "state", "calls", "app/Weft.Server", "app/Weft.Worker"]:
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        for host in ["Weft.Server", "Weft.Worker"]:
            (self.root / "app" / host / (host + ".dll")).write_bytes(b"fixture binary, never executed")
        source = (LOCAL / "dev-up.sh").read_text(encoding="utf-8")
        self.assertIn('source "$script_dir/dev-units.sh"', source)
        block = source.split("# ---- Host settings:", 1)[1].split("\n", 1)[1].split("# ---- Units:", 1)[0]
        self.assertIn("docker_budgets=$(dev_docker_budgets)", block)
        self.harness = self.root / "harness.sh"
        self.block = self.root / "environment-block.sh"
        self.harness.write_text(HARNESS, encoding="utf-8", newline="\n")
        self.block.write_text(block, encoding="utf-8", newline="\n")

    def run_fixture(self, budgets=None, helper=HELPER, mode="run", expected_success=True):
        environment = dict(os.environ)
        for key in [CREATE, CLEANUP, "BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS"]:
            environment.pop(key, None)
        environment.update(budgets or {})
        environment["WEFT_UNIT_TEST_SECRET"] = self.secret
        result = subprocess.run(
            [self.bash, "--noprofile", "--norc", bash_path(self.harness), bash_path(self.root), bash_path(helper), bash_path(self.block), mode],
            env=environment, capture_output=True, text=True, timeout=30,
        )
        self.output += result.stdout + result.stderr
        self.assert_private_values_not_logged()
        if expected_success:
            self.assertEqual(result.returncode, 0, result.stderr.replace(self.secret, "<redacted>"))
        else:
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("refusing automatic stop", result.stderr)

    def calls(self):
        return [path.read_bytes().decode().rstrip("\0").split("\0") for path in sorted((self.root / "calls").glob("*.args"), key=lambda path: int(path.stem))]

    def stops(self):
        events = self.root / "events"
        return [line for line in events.read_text().splitlines() if line.startswith("stop ")] if events.exists() else []

    def stamps(self):
        return {path.name: path.read_text().strip() for path in (self.root / "home/run").glob("*.stamp")}

    def input_digests(self):
        paths = list((self.root / "private").glob("*.env")) + list((self.root / "app").glob("*/*.dll"))
        return {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}

    def owner_confirmed_manual_stop(self):
        # Model an owner-confirmed settled unit, not an automatic stop under its old policy.
        for active in (self.root / "state").glob("*.active"):
            active.unlink()

    def assert_private_values_not_logged(self):
        logged = self.output + "".join("\0".join(call) for call in self.calls())
        logged += "".join(self.stamps().values())
        self.assertFalse(self.secret in logged, "Private environment content was logged or included in invocation arguments.")

    def assert_grace(self, calls, seconds=300):
        for call in calls:
            unit = call[call.index("--unit") + 1]
            host, name = {"fixture-server": ("Weft.Server", "server"), "fixture-worker": ("Weft.Worker", "worker")}[unit]
            properties = [call[index + 1] for index, value in enumerate(call[:-1]) if value == "--property"]
            self.assertIn("TimeoutStopSec=" + str(seconds), properties)
            self.assertIn("KillMode=mixed", properties)
            self.assertIn("EnvironmentFile=" + bash_path(self.root / "private" / (name + ".env")), properties)
            self.assertIn("WorkingDirectory=" + bash_path(self.root / "app" / host), properties)
            self.assertIn("StandardOutput=append:" + bash_path(self.root / "logs" / (unit + ".log")), properties)
            self.assertIn("StandardError=append:" + bash_path(self.root / "logs" / (unit + ".log")), properties)
            self.assertIn("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false", call)
            self.assertIn("TMPDIR=" + bash_path(self.root / "home/tmp" / unit), call)
            self.assertEqual(call[-2:], ["/fixture/sdk/dotnet", bash_path(self.root / "app" / host / (host + ".dll"))])

    def test_defaults_use_independent_grace_and_identical_rerun_keeps_units(self):
        self.run_fixture()
        self.assertEqual(len(self.calls()), 2)
        self.assert_grace(self.calls())
        for path in (self.root / "private").glob("*.env"):
            lines = path.read_text().splitlines()
            self.assertFalse(any(line.startswith(CREATE + "=") or line.startswith(CLEANUP + "=") for line in lines))
        stamps = self.stamps()
        self.assertTrue(all(re.fullmatch(r"[0-9a-f]{64}", stamp) for stamp in stamps.values()))
        self.run_fixture()
        self.assertEqual(len(self.calls()), 2)
        self.assertEqual(self.stops(), [])
        self.assertEqual(self.stamps(), stamps)
        self.assertNotIn("grace ", (self.root / "events").read_text())

    def test_maximum_accepted_budgets_reach_both_units_and_fit_stop_grace(self):
        self.run_fixture({CREATE: str(MAXIMUM), CLEANUP: str(MAXIMUM)})
        self.assert_grace(self.calls())
        self.assertGreaterEqual(300, 2 * MAXIMUM + 60)
        for path in (self.root / "private").glob("*.env"):
            lines = path.read_text().splitlines()
            self.assertIn(CREATE + "=" + str(MAXIMUM), lines)
            self.assertIn(CLEANUP + "=" + str(MAXIMUM), lines)

    def test_legacy_unit_is_not_stopped_before_owner_confirmed_safe_upgrade(self):
        self.run_fixture(mode="prepare")
        for unit, host, name in [("fixture-server", "Weft.Server", "server"), ("fixture-worker", "Weft.Worker", "worker")]:
            data = (self.root / "private" / (name + ".env")).read_bytes() + (self.root / "app" / host / (host + ".dll")).read_bytes()
            (self.root / "home/run" / (unit + ".stamp")).write_text(hashlib.sha256(data).hexdigest() + "\n")
            (self.root / "state" / (unit + ".active")).touch()
            (self.root / "state" / (unit + ".grace")).write_text("120000000\n")
        previous_stamps = self.stamps()
        self.run_fixture(expected_success=False)
        self.assertEqual(self.calls(), [])
        self.assertEqual(self.stops(), [])
        self.assertEqual(self.stamps(), previous_stamps)
        self.owner_confirmed_manual_stop()
        self.run_fixture()
        self.assertEqual(len(self.calls()), 2)
        self.assertEqual(self.stops(), [])
        self.assert_grace(self.calls())
        self.run_fixture()
        self.assertEqual(len(self.calls()), 2)

    def test_grace_only_change_is_stamped_but_cannot_unsafely_stop_the_old_unit(self):
        source = HELPER.read_text(encoding="utf-8")
        self.assertEqual(source.count("readonly dev_unit_stop_seconds=300"), 1)
        old_policy = self.root / "old-policy.sh"
        old_policy.write_text(source.replace("readonly dev_unit_stop_seconds=300", "readonly dev_unit_stop_seconds=120"), encoding="utf-8", newline="\n")
        self.run_fixture(helper=old_policy)
        previous_stamps, previous_inputs = self.stamps(), self.input_digests()
        self.assert_grace(self.calls(), 120)
        self.run_fixture(expected_success=False)
        self.assertEqual(self.input_digests(), previous_inputs)
        self.assertEqual(self.stamps(), previous_stamps)
        self.assertEqual(self.stops(), [])
        self.assertEqual(len(self.calls()), 2)
        self.owner_confirmed_manual_stop()
        self.run_fixture()
        self.assertNotEqual(self.stamps(), previous_stamps)
        self.assertEqual(self.stops(), [])
        self.assertEqual(len(self.calls()), 4)
        self.assert_grace(self.calls()[2:])
        self.run_fixture()
        self.assertEqual(len(self.calls()), 4)

    def test_binary_and_private_environment_changes_still_restart_only_affected_unit(self):
        self.run_fixture()
        (self.root / "app/Weft.Server/Weft.Server.dll").write_bytes(b"changed fixture binary")
        self.run_fixture()
        self.assertEqual(len(self.calls()), 3)
        self.assertEqual(self.stops(), ["stop fixture-server"])
        self.run_fixture(mode="server-env-change")
        self.assertEqual(len(self.calls()), 4)
        self.assertEqual(self.stops(), ["stop fixture-server", "stop fixture-server"])
        events = (self.root / "events").read_text().splitlines()
        self.assertLess(events.index("grace fixture-server"), events.index("stop fixture-server"))

    def test_failed_live_grace_query_fails_closed_without_stop_or_replacement(self):
        self.run_fixture()
        previous = self.stamps()
        (self.root / "app/Weft.Server/Weft.Server.dll").write_bytes(b"changed fixture binary")
        self.run_fixture(mode="grace-unavailable", expected_success=False)
        self.assertEqual(self.stops(), [])
        self.assertEqual(len(self.calls()), 2)
        self.assertEqual(self.stamps(), previous)

    def test_malformed_live_grace_fails_closed_without_stop_or_replacement(self):
        self.run_fixture()
        previous = self.stamps()
        (self.root / "app/Weft.Server/Weft.Server.dll").write_bytes(b"changed fixture binary")
        self.run_fixture(mode="grace-malformed", expected_success=False)
        self.assertEqual(self.stops(), [])
        self.assertEqual(len(self.calls()), 2)
        self.assertEqual(self.stamps(), previous)

    def test_changed_forwarded_budgets_restart_once_then_remain_idempotent(self):
        self.run_fixture()
        previous_stamps = self.stamps()
        budgets = {CREATE: str(MAXIMUM), CLEANUP: str(MAXIMUM)}
        self.run_fixture(budgets)
        self.assertNotEqual(self.stamps(), previous_stamps)
        self.assertEqual(len(self.calls()), 4)
        self.assertEqual(len(self.stops()), 2)
        self.run_fixture(budgets)
        self.assertEqual(len(self.calls()), 4)


if __name__ == "__main__":
    unittest.main()
