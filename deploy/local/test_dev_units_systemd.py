"""Opt-in, bounded user-systemd migration fixture. No Docker or Weft host is run.

Linux only; requires explicit opt-in, an existing current-user manager and an
owned work root on the explicitly identified, writable Btrfs data filesystem.
"""

import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import shlex
import shutil
import stat
import subprocess
import tempfile
import time
import uuid


@contextmanager
def fixture_directory(work_root):
    root = Path(tempfile.mkdtemp(prefix="weft-unit-migration-", dir=work_root))
    try:
        yield root
    except BaseException:
        # Preserve receipts and the private fixture files if settlement cannot be proved.
        import sys
        print("Owned fixture evidence retained at " + str(root), file=sys.stderr)
        raise
    else:
        shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-owned-user-units", action="store_true", required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--expected-filesystem-uuid", required=True)
    args = parser.parse_args()
    assert args.run_owned_user_units and os.name == "posix"
    work_root = args.work_root.resolve(strict=True)
    assert work_root.is_dir() and work_root.stat().st_uid == os.getuid()
    mount = subprocess.check_output(["findmnt", "-n", "-T", str(work_root), "-o", "TARGET,FSTYPE,UUID,FSROOT,OPTIONS"], text=True).split()
    assert mount[1] == "btrfs" and mount[2] == args.expected_filesystem_uuid
    assert "rw" in mount[4].split(",") and "compress=zstd:3" in mount[4].split(",")
    runtime = Path("/run/user") / str(os.getuid())
    assert runtime.stat().st_uid == os.getuid() and stat.S_ISSOCK((runtime / "bus").stat().st_mode)
    env = dict(os.environ, XDG_RUNTIME_DIR=str(runtime), DBUS_SESSION_BUS_ADDRESS="unix:path=" + str(runtime / "bus"))
    helper = Path(__file__).resolve().with_name("dev-units.sh")
    token = uuid.uuid4().hex
    prefix = "weft-grace-fixture-" + token
    units = []
    secret = "fixture-private-" + uuid.uuid4().hex
    report = {"systemdVersion": "", "runtimePropertyChangeSupported": None, "checks": {}}

    def run(arguments, check=True, timeout=10):
        completed = subprocess.run(arguments, env=env, capture_output=True, text=True, timeout=timeout)
        assert secret not in completed.stdout + completed.stderr, "Private fixture content was logged"
        if check and completed.returncode:
            raise RuntimeError("Owned fixture command failed: " + completed.stderr.replace(secret, "<redacted>"))
        return completed

    def control(*arguments, check=True):
        return run(["systemctl", "--user", *arguments], check=check)

    def wait_for(predicate, seconds=8):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.05)
        raise TimeoutError("Owned user-unit fixture did not settle within its short bound")

    report["systemdVersion"] = run(["systemctl", "--version"]).stdout.splitlines()[0]
    with fixture_directory(work_root) as root:
        assert subprocess.check_output(["findmnt", "-n", "-T", str(root), "-o", "UUID"], text=True).strip() == mount[2]
        for child in ["app/Fixture.Host", "private", "home/run", "logs"]:
            (root / child).mkdir(parents=True, exist_ok=True)
        assembly = root / "app/Fixture.Host/Fixture.Host.dll"
        assembly.write_text("""import json, os, signal, time
from pathlib import Path
root=Path(__file__).resolve().parents[2]
pid=os.getpid()
stopping=False
def event(name):
    with (root/'events.jsonl').open('a') as output:
        output.write(json.dumps({'event':name,'pid':pid,'time':time.monotonic()})+'\\n')
def stop(signum, frame):
    global stopping
    if stopping: return
    stopping=True
    event('signal')
    time.sleep(float(os.environ.get('WEFT_FIXTURE_DRAIN_SECONDS','0.15')))
    event('drained')
    raise SystemExit(0)
signal.signal(signal.SIGTERM,stop)
event('ready')
while True: signal.pause()
""", encoding="utf-8")
        environment = root / "private/fixture.env"
        environment.write_text("WEFT_UNIT_FIXTURE_SECRET=" + secret + "\nWEFT_FIXTURE_DRAIN_SECONDS=1.5\n", encoding="utf-8")
        environment.chmod(0o600)
        launcher = root / "invoke-helper.sh"
        launcher.write_text("\n".join([
            "set -euo pipefail", "umask 077", "source " + shlex.quote(str(helper)),
            "dev_home=" + shlex.quote(str(root / "home")), "dev_logs=" + shlex.quote(str(root / "logs")),
            "app=" + shlex.quote(str(root / "app")), "dev_name=fixture-" + token,
            "dotnet_root=/usr", "dotnet=/usr/bin/python3", "build_key=fixture", "declare -A log_offset",
            "step() { printf '%s\\n' \"$*\"; }", "fail() { printf '%s\\n' \"$*\" >&2; exit 1; }",
            "start_unit \"$1\" Fixture.Host " + shlex.quote(str(environment)), "",
        ]), encoding="utf-8")

        def events():
            path = root / "events.jsonl"
            return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

        def event_seen(pid, name):
            return any(item["pid"] == pid and item["event"] == name for item in events())

        def start(suffix, grace):
            unit = prefix + "-" + suffix + ".service"
            units.append(unit)
            run(["systemd-run", "--user", "--quiet", "--unit", unit,
                 "--description", "Weft owned stop-grace fixture " + token,
                 "--property", "Type=exec", "--property", "KillMode=mixed",
                 "--property", "TimeoutStopSec=" + str(grace),
                 "--property", "EnvironmentFile=" + str(environment),
                 "/usr/bin/python3", str(assembly)])
            pid = int(control("show", unit, "--property=MainPID", "--value").stdout.strip())
            assert pid > 0
            wait_for(lambda: event_seen(pid, "ready"))
            return unit, pid

        def finish_cooperatively(unit, pid):
            # This fixture owns these units and their synthetic cleanup receipt.
            # kill-main sends no stop job, so the old short stop timer is not armed.
            if not event_seen(pid, "drained"):
                observed = control("show", unit, "--property=MainPID", "--value", check=False)
                assert observed.returncode == 0 and int(observed.stdout.strip() or "0") == pid, "Owned fixture exited without its settlement receipt"
                control("kill", "--kill-whom=main", "--signal=SIGTERM", unit)
                wait_for(lambda: event_seen(pid, "drained"))

        try:
            probe, probe_pid = start("property-probe", 1)
            attempted = control("set-property", "--runtime", probe, "TimeoutStopSec=300", check=False)
            report["runtimePropertyChangeSupported"] = attempted.returncode == 0
            report["runtimePropertyChangeFailure"] = attempted.stderr.strip() if attempted.returncode else None
            # The implementation does not rely on a setter, even on versions that support one.
            finish_cooperatively(probe, probe_pid)

            legacy, legacy_pid = start("legacy", 1)
            refused = run(["bash", str(launcher), legacy], check=False)
            assert refused.returncode != 0 and "refusing automatic stop" in refused.stderr
            assert control("is-active", "--quiet", legacy, check=False).returncode == 0
            assert int(control("show", legacy, "--property=MainPID", "--value").stdout.strip()) == legacy_pid
            assert not event_seen(legacy_pid, "signal")
            report["checks"]["insufficient_active_grace_refused_before_signal_or_stop"] = True
            finish_cooperatively(legacy, legacy_pid)
            old_events = [item for item in events() if item["pid"] == legacy_pid]
            signal_time = next(item["time"] for item in old_events if item["event"] == "signal")
            drained_time = next(item["time"] for item in old_events if item["event"] == "drained")
            assert drained_time - signal_time > 1.0
            report["checks"]["owned_manual_cooperative_settlement_outlived_old_grace"] = True

            adequate, old_pid = start("adequate", 300)
            run(["bash", str(launcher), adequate])
            assert event_seen(old_pid, "drained")
            new_pid = int(control("show", adequate, "--property=MainPID", "--value").stdout.strip())
            assert new_pid > 0 and new_pid != old_pid
            wait_for(lambda: event_seen(new_pid, "ready"))
            timeline = events()
            assert next(item["time"] for item in timeline if item["pid"] == old_pid and item["event"] == "drained") < next(item["time"] for item in timeline if item["pid"] == new_pid and item["event"] == "ready")
            report["checks"]["adequate_active_grace_allows_real_settled_replacement"] = True
            run(["bash", str(launcher), adequate])
            assert int(control("show", adequate, "--property=MainPID", "--value").stdout.strip()) == new_pid
            report["checks"]["identical_rerun_keeps_real_main_pid"] = True
            finish_cooperatively(adequate, new_pid)
        finally:
            # Only exact unique units created by this invocation are eligible for cleanup.
            failures = []
            for unit in units:
                load = control("show", unit, "--property=LoadState", "--value", check=False)
                if load.stdout.strip() == "not-found":
                    continue
                if load.returncode != 0:
                    failures.append("Cannot confirm owned unit state: " + unit)
                    continue
                description = control("show", unit, "--property=Description", "--value", check=False)
                if description.returncode != 0:
                    failures.append("Cannot confirm owned unit identity: " + unit)
                    continue
                assert token in description.stdout, "Refusing to clean a unit without this fixture's identity"
                pid_result = control("show", unit, "--property=MainPID", "--value", check=False)
                pid = int(pid_result.stdout.strip() or "0")
                if pid > 0:
                    try:
                        finish_cooperatively(unit, pid)
                    except Exception as error:
                        failures.append(str(error))
                        continue  # Never force-stop a fixture whose settlement was not proved.
                stopped = control("stop", unit, check=False)
                if stopped.returncode != 0:
                    absent = control("show", unit, "--property=LoadState", "--value", check=False)
                    if absent.stdout.strip() != "not-found":
                        failures.append("Cannot confirm owned unit stop: " + unit)
                        continue
                control("reset-failed", unit, check=False)
            if failures:
                raise RuntimeError("Owned fixture cleanup remains unconfirmed: " + "; ".join(failures))
        report["checks"]["only_owned_fixture_units_cleaned"] = True
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
