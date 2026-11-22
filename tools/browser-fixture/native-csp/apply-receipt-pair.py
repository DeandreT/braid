"""Apply the isolated fixture patch stack to pristine pinned inputs, once."""
import argparse
import hashlib
from pathlib import Path
import subprocess


def run(cwd, *args):
    return subprocess.check_output(args, cwd=cwd, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("chromium", type=Path)
    parser.add_argument("playwright", type=Path)
    args = parser.parse_args()
    chrome, driver = args.chromium.resolve(), args.playwright.resolve()
    here = Path(__file__).resolve().parent
    revision = "2ea1b71af70d7fc5d41325dda8df37c1f98ac531"
    if (chrome / "build/util/LASTCHANGE").read_text().splitlines()[0] != (
        f"LASTCHANGE={revision}-refs/branch-heads/8090@{{#1}}"
    ):
        parser.error("wrong Chromium source revision")
    if run(driver, "git", "rev-parse", "HEAD") != "606ecd8b34bac100db32561b6278ffd76e5eb3cd":
        parser.error("wrong Playwright source revision")
    if run(driver, "git", "status", "--porcelain"):
        parser.error("expected pristine Playwright checkout")
    # Grant no provenance based on LASTCHANGE alone. Caller verifies the
    # release archive before extraction, as specified in the adjacent README.
    stages = [(chrome, here / "native-phase.patch"), (chrome, here / "inspector-receipt.patch"),
              (driver, here / "driver-launch.patch")]
    touched = []
    try:
        for cwd, patch in stages:
            run(cwd, "git", "apply", "--check", str(patch))
            run(cwd, "git", "apply", str(patch))
            touched.append((cwd, patch))
        for cwd, patch in stages:
            print(patch.name, hashlib.sha256(patch.read_bytes()).hexdigest())
    except BaseException:
        for cwd, patch in reversed(touched):
            run(cwd, "git", "apply", "--reverse", str(patch))
        raise


if __name__ == "__main__":
    main()
