"""Build and push one product image on a trusted release runner (run from the repository root)."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile


# Role -> (image name suffix, deploy/container target). Ferrule has a separate Dockerfile.
PRODUCTS = {
    "server": ("", "braid"),
    "edge": ("-edge", "edge"),
    "worker": ("-worker", "braid-worker"),
    "mappingEndpoint": ("-mapping-endpoint", "braid-mapping-endpoint"),
    "ferruleService": ("-ferrule-service", None),
}
SCANNER = "docker.io/docker/buildkit-syft-scanner@sha256:873a7458c3c8ac8e32c7b796c10e1600b07b963d2aa000a72a2d97aaaa2d6315"  # 1.12.1
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
BUILD = ["docker", "buildx", "build", "--platform", "linux/amd64"]


def digest(value):
    if not isinstance(value, str) or not DIGEST.fullmatch(value):
        raise ValueError("Missing or invalid OCI manifest digest")
    return value


def native_source(temp, commit, source):
    # The buildx container cannot see docker's local image store. Export an OCI layout
    # and bind both extraction and the service's FROM to the exact exported digest.
    layout = temp / "native"
    meta = temp / "native.json"
    subprocess.run(BUILD + ["--provenance=false", "--build-arg", f"BRAID_REVISION={commit}",
                            "--label", f"org.opencontainers.image.source={source}",
                            "--output", f"type=oci,tar=false,dest={layout}", "--metadata-file", str(meta),
                            "engines/ferrule-host"], check=True)
    native = digest(json.loads(meta.read_text())["containerimage.digest"])
    context = ["--build-context", f"braid-release-native=oci-layout://{layout}@{native}"]
    extract = temp / "extracted"
    subprocess.run(BUILD + context + ["--output", f"type=local,dest={extract}", "-f", "-", "."],
                   input="FROM scratch\nCOPY --from=braid-release-native /ferrule-host /ferrule-host\n",
                   text=True, check=True)
    with (extract / "ferrule-host").open("rb") as binary:
        sha = hashlib.file_digest(binary, "sha256").hexdigest()
    return sha, context + ["--build-arg", "BRAID_NATIVE_IMAGE=braid-release-native",
                           "--build-arg", f"BRAID_NATIVE_BINARY_SHA256={sha}",
                           "--build-arg", f"BRAID_REVISION={commit}"]


def check_attestations(ref):
    def inspect(image):
        return json.loads(subprocess.check_output(["docker", "buildx", "imagetools", "inspect", "--raw", image]))

    descriptors = inspect(ref)["manifests"]
    platforms = [d for d in descriptors if d["platform"]["os"] != "unknown"]
    if len(platforms) != 1 or platforms[0]["platform"] != {"os": "linux", "architecture": "amd64"}:
        raise ValueError("Expected one linux/amd64 runtime manifest")
    leaf = digest(platforms[0]["digest"])
    predicates = set()
    for descriptor in descriptors:
        annotations = descriptor.get("annotations", {})
        if (annotations.get("vnd.docker.reference.type") == "attestation-manifest"
                and annotations.get("vnd.docker.reference.digest") == leaf):
            for layer in inspect(ref.split("@")[0] + "@" + digest(descriptor["digest"]))["layers"]:
                if layer["mediaType"] == "application/vnd.in-toto+json":
                    predicates.add(layer["annotations"]["in-toto.io/predicate-type"])
    if ("https://spdx.dev/Document" not in predicates
            or not predicates.intersection({"https://slsa.dev/provenance/v0.2", "https://slsa.dev/provenance/v1"})):
        raise ValueError("Missing SPDX SBOM or SLSA provenance for the released platform")


def publish(role, prefix, version, commit, source, evidence, temp):
    suffix, target = PRODUCTS[role]
    repository = prefix + suffix
    meta = temp / "result.json"
    if target:
        options = ["--file", "deploy/container/Dockerfile", "--target", target]
    else:
        sha, inputs = native_source(temp, commit, source)
        options = ["--file", "engines/ferrule-service/Dockerfile", *inputs]
    subprocess.run(BUILD + options + [
        "--label", f"org.opencontainers.image.revision={commit}", "--label", f"org.opencontainers.image.source={source}",
        "--label", f"org.opencontainers.image.version={version}", "--sbom", f"generator={SCANNER}",
        "--provenance=mode=max", "--tag", f"{repository}:{version}", "--push", "--metadata-file", str(meta), ".",
    ], check=True)
    ref = repository + "@" + digest(json.loads(meta.read_text())["containerimage.digest"])
    check_attestations(ref)
    evidence.mkdir()
    if role == "ferruleService":
        for command, name in (("identity", "compiler-identity"), ("runtime-identity", "runtime-identity")):
            report = subprocess.check_output([
                "docker", "run", "--rm", "--platform", "linux/amd64", "--network", "none", "--read-only",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--tmpfs", "/tmp:size=256m,mode=1777",
                "--memory", "3g", "--pids-limit", "256", ref, command,
            ])
            if json.loads(report)["native"] != "sha256:" + sha:
                raise ValueError("Ferrule identity does not match the native build")
            (evidence / (name + ".json")).write_bytes(report)
    (evidence / (role + ".ref")).write_text(ref + "\n", encoding="ascii")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("role", choices=PRODUCTS)
    parser.add_argument("--prefix", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as scratch:
        publish(args.role, args.prefix, args.version, args.commit, args.source, args.evidence, Path(scratch))
