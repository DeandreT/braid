"""Create or reuse the source release for this protected tag. Writes remotely; workflow use only."""

import os
from pathlib import Path
import subprocess
import tempfile

from source_archive import MAX_BYTES, build, identities, rust_inputs, verify


def public_tag_exists(repo_url, tag, commit):
    ref = "refs/tags/" + tag
    query = subprocess.run(["git", "-c", "credential.helper=", "ls-remote", "--exit-code", "--tags",
                            repo_url + ".git", ref, ref + "^{}"],
                           env={**os.environ, "GIT_TERMINAL_PROMPT": "0"}, capture_output=True, text=True)
    if query.returncode == 2:  # Git's documented "no matching refs"; not an auth or network failure.
        return False
    query.check_returncode()
    refs = {refname: oid for oid, refname in (line.split("\t") for line in query.stdout.splitlines())}
    if refs.get(ref + "^{}", refs.get(ref)) != commit:
        raise ValueError("Existing public source tag does not resolve to the release commit")
    return True


def publish(version, commit, repo_url, identity):
    name = identities(version, commit)
    tag = "source-" + version
    base = f"{repo_url}/releases/download/{tag}"
    with tempfile.TemporaryDirectory() as temporary:
        temp = Path(temporary)
        if not public_tag_exists(repo_url, tag, commit):
            assets = temp / "build"
            build(version, commit, assets)
            subprocess.run(["cosign", "sign-blob", "--yes", "--bundle", "source.json.bundle", "source.json"],
                           cwd=assets, check=True)
            subprocess.run(["gh", "release", "create", tag, "--repo", os.environ["GITHUB_REPOSITORY"],
                            "--target", commit, "--latest=false", "--title", "Source for " + version,
                            "--notes", f"braid {version} ({commit}). See README.md inside for build instructions and scope.",
                            name, "source.json", "source.json.bundle"], cwd=assets, check=True)
            if not public_tag_exists(repo_url, tag, commit):
                raise ValueError("New source tag is not publicly readable")

        # On retry never upload, replace, or bless new bytes for an existing tag.
        for file in (name, "source.json", "source.json.bundle"):
            subprocess.run(["curl", "-qfsSL", "--proto", "=https", "--proto-redir", "=https", "--retry", "3",
                            "--max-time", "900", "--max-filesize", str(MAX_BYTES if file == name else 1024**2),
                            "--output", temp / file, base + "/" + file], check=True)
        subprocess.run(["cosign", "verify-blob", "--bundle", "source.json.bundle",
                        "--certificate-identity=" + identity,
                        "--certificate-oidc-issuer=https://token.actions.githubusercontent.com", "source.json"],
                       cwd=temp, check=True)
        record = verify(version, commit, temp)
        if record["ferruleCommit"] != rust_inputs(Path.cwd())[1]:
            raise ValueError("Source release records a different Ferrule revision")
        return base + "/" + name, record["sha256"]


if __name__ == "__main__":
    e = os.environ
    url, sha = publish(e["GITHUB_REF_NAME"], e["GITHUB_SHA"], e["GITHUB_SERVER_URL"] + "/" + e["GITHUB_REPOSITORY"],
                       e["GITHUB_SERVER_URL"] + "/" + e["GITHUB_WORKFLOW_REF"])
    with open(e["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        print("archive=" + url, "sha256=" + sha, sep="\n", file=output)
