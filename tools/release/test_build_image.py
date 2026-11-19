"""Offline build evidence tests; no Docker daemon, signing service, or registry is contacted."""

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import build_image as release


def fake_digest(char):
    return "sha256:" + char * 64


class ReleaseEvidenceTests(unittest.TestCase):
    def test_ferrule_uses_the_exported_native_manifest_and_measures_the_pushed_service_index(self):
        binary = b"synthetic native bytes"
        native_sha = hashlib.sha256(binary).hexdigest()
        native, config, service = (fake_digest(c) for c in "abc")
        prefix = "registry.example/product"
        ref = prefix + "-ferrule-service@" + service
        commit = "d" * 40
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def build(command, **kwargs):
                self.assertTrue(kwargs["check"])
                if command[-1] == "engines/ferrule-host":
                    self.assertNotIn("--push", command)
                    meta, manifest = root / "native.json", native
                elif "engines/ferrule-service/Dockerfile" in command:
                    self.assertIn("BRAID_NATIVE_BINARY_SHA256=" + native_sha, command)
                    self.assertIn(f"braid-release-native=oci-layout://{root / 'native'}@{native}", command)
                    self.assertIn("--push", command)
                    meta, manifest = root / "result.json", service
                else:
                    self.assertIn(f"braid-release-native=oci-layout://{root / 'native'}@{native}", command)
                    self.assertIn("COPY --from=braid-release-native /ferrule-host", kwargs["input"])
                    (root / "extracted").mkdir()
                    (root / "extracted/ferrule-host").write_bytes(binary)
                    return
                meta.write_text(json.dumps({"containerimage.digest": manifest, "containerimage.config.digest": config}))

            def measure(command):
                self.assertEqual(ref, command[-2])
                return json.dumps({"native": "sha256:" + native_sha}).encode()

            with patch.object(release.subprocess, "run", side_effect=build), \
                    patch.object(release.subprocess, "check_output", side_effect=measure) as reports, \
                    patch.object(release, "check_attestations") as verify:
                release.publish("ferruleService", prefix, "v1.2.3", commit, "https://example.org/product",
                                root / "evidence", root)
            verify.assert_called_once_with(ref)
            self.assertEqual(["identity", "runtime-identity"], [c.args[0][-1] for c in reports.call_args_list])
            self.assertEqual(ref, (root / "evidence/ferruleService.ref").read_text().strip())

    def test_rejects_missing_or_misdirected_attestation(self):
        leaf, attestation = fake_digest("1"), fake_digest("2")
        repo = "registry.example/product@"
        ref = repo + fake_digest("3")
        for subject, arch, predicates, valid in [
            (leaf, "amd64", ["https://slsa.dev/provenance/v1", "https://spdx.dev/Document"], True),
            (leaf, "arm64", ["https://slsa.dev/provenance/v1", "https://spdx.dev/Document"], False),
            (leaf, "amd64", ["https://spdx.dev/Document"], False),
            (leaf, "amd64", ["https://slsa.dev/provenance/v0.2"], False),
            (attestation, "amd64", ["https://slsa.dev/provenance/v1", "https://spdx.dev/Document"], False),
        ]:
            docs = {
                ref: {"manifests": [
                    {"platform": {"os": "linux", "architecture": arch}, "digest": leaf},
                    {"platform": {"os": "unknown", "architecture": "unknown"}, "digest": attestation,
                     "annotations": {"vnd.docker.reference.type": "attestation-manifest",
                                     "vnd.docker.reference.digest": subject}},
                ]},
                repo + attestation: {"layers": [
                    {"mediaType": "application/vnd.in-toto+json", "annotations": {"in-toto.io/predicate-type": p}}
                    for p in predicates
                ]},
            }
            with self.subTest(subject=subject, arch=arch, predicates=predicates), \
                    patch.object(release.subprocess, "check_output", side_effect=lambda cmd: json.dumps(docs[cmd[-1]])):
                if valid:
                    release.check_attestations(ref)
                else:
                    with self.assertRaises(ValueError):
                        release.check_attestations(ref)


if __name__ == "__main__":
    unittest.main()
