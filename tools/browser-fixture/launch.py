"""Launch the existing web browser tests on an isolated Xvfb display.

This only owns launch setup. Playwright configs and fixture owners retain their
scenario policies. exec leaves signals, exit status and Xvfb cleanup with
xvfb-run, rather than adding a Python supervisor or touching any display itself.
"""

import argparse
import os
import re
import resource
import sys


DESKTOP_VARIABLES = (
    "WAYLAND_DISPLAY",
    "XDG_RUNTIME_DIR",
    "XDG_SESSION_TYPE",
    "DBUS_SESSION_BUS_ADDRESS",
)


def launch_arguments(arguments, environment, display_from_env=False):
    display = environment.get("WEFT_XVFB_SERVER_NUM", "") if display_from_env else ""
    if display and not re.fullmatch(r"[0-9]+", display):
        raise ValueError("WEFT_XVFB_SERVER_NUM must be a nonnegative decimal display number")
    return [
        "xvfb-run",
        "--server-num=" + display if display else "-a",
        "-w",
        "10",
        "npm",
        "--workspace",
        "@weft/web",
        "run",
        "test:e2e",
        *(["--", *arguments] if arguments else []),
    ]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--display-from-env", action="store_true",
                        help="honor WEFT_XVFB_SERVER_NUM (HTTPS auth route only)")
    parser.add_argument("--no-core-dumps", action="store_true",
                        help="disable core dumps before launching the HTTPS auth route")
    parser.add_argument("arguments", nargs=argparse.REMAINDER,
                        help="Playwright arguments after --")
    options = parser.parse_args(argv)
    arguments = options.arguments
    if arguments[:1] == ["--"]:
        arguments = arguments[1:]
    environment = dict(os.environ)
    for name in DESKTOP_VARIABLES:
        environment.pop(name, None)
    try:
        command = launch_arguments(arguments, environment, options.display_from_env)
    except ValueError as error:
        parser.error(str(error))
    if options.no_core_dumps:
        try:
            # Like `ulimit -c 0`, lower both limits for this process and its heirs.
            resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        except (OSError, ValueError):
            print("browser launcher: could not disable core dumps", file=sys.stderr)
            return 1
    try:
        # No shell interpolation, child supervisor, display adoption or cleanup.
        os.execvpe(command[0], command, environment)
    except FileNotFoundError:
        print("browser launcher: xvfb-run was not found", file=sys.stderr)
        return 127
    except OSError:
        print("browser launcher: could not execute xvfb-run", file=sys.stderr)
        return 126


if __name__ == "__main__":
    sys.exit(main())
