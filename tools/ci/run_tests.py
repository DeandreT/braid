"""Run the existing CI test selection without leaking CI control descriptors to children."""
from __future__ import annotations

import os
import subprocess
from collections.abc import Sequence


def run(command: Sequence[str]) -> int:
    environment = os.environ.copy()
    environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false"
    # .NET forwards unrelated inheritable descriptors to Process.Start children.
    # Start its test host with only standard streams, not the CI runner's handles.
    return subprocess.run(command, env=environment, close_fds=True, check=False).returncode


def main() -> int:
    return run([
        "dotnet", "test", "dotnet/Weft.slnx", "--configuration", "Release", "--no-build", "--no-restore",
        "--logger", "console;verbosity=normal",
    ])


if __name__ == "__main__":
    raise SystemExit(main())
