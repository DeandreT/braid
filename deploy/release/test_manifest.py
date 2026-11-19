import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import manifest


HERE = Path(__file__).resolve().parent
EXAMPLE = HERE / "release-manifest.example.json"


class ReleaseManifestTests(unittest.TestCase):
    def setUp(self):
        self.example = manifest.read_json(EXAMPLE)
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.temp = Path(temp.name)
        self.output = self.temp / "release.json"

    def cli(self, *args, ok=True):
        result = subprocess.run(
            [sys.executable, str(HERE / "manifest.py"), *map(str, args)],
            cwd=self.temp, capture_output=True, text=True,
        )
        self.assertEqual(ok, result.returncode == 0, result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def generate(self, *args, ok=True):
        ref = self.example["images"]
        self.cli(
            "generate", "--version", self.example["version"], "--source-commit", self.example["sourceCommit"],
            "--server", ref["server"], "--edge", ref["edge"], "--worker", ref["worker"],
            "--mapping-endpoint", ref["mappingEndpoint"], "--ferrule-service", ref["ferruleService"],
            "--output", self.output, *args, ok=ok,
        )

    def reports(self):
        pins = self.example["ferrule"]
        compiler = {"schema": "braid.compiler-image-identity.v1", **{k: pins[k] for k in ("native", "toolchain", "build")}}
        runtime = {"schema": "braid.compiled-runtime-image-identity.v1", "native": pins["native"], "runtime": pins["runtime"]}
        self.compiler_path = self.temp / "compiler.json"
        self.runtime_path = self.temp / "runtime.json"
        self.compiler_path.write_text(json.dumps(compiler), encoding="utf-8")
        self.runtime_path.write_text(json.dumps(runtime), encoding="utf-8")
        return ["--compiler-identity", self.compiler_path, "--runtime-identity", self.runtime_path]

    def test_publisher_handoff_matches_example_with_measured_pins(self):
        self.generate(*self.reports())
        self.cli("validate", self.output, "--expect-version", "v1.2.3", "--expect-source-commit", self.example["sourceCommit"])
        self.assertEqual(self.example, manifest.read_json(self.output))
        self.cli("validate", self.output, "--expect-version", "v1.2.4", ok=False)
        self.cli("validate", self.output, "--expect-source-commit", "b" * 40, ok=False)

    def test_release_without_measurements_does_not_fabricate_pins(self):
        self.generate()
        del self.example["ferrule"]
        self.assertEqual(self.example, manifest.read_json(self.output))
        self.cli("validate", self.output)

    def test_refuse_incomplete_or_mismatched_measurements(self):
        args = self.reports()
        self.generate(*args[:2], ok=False)
        runtime = manifest.read_json(self.runtime_path)
        for overrides in ({"native": "sha256:" + "a"*64}, {"runtime": "bad"}, {"schema": "other"}, {"extra": True}):
            self.runtime_path.write_text(json.dumps(runtime | overrides), encoding="utf-8")
            self.generate(*args, ok=False)
        self.assertFalse(self.output.exists())

    def test_reject_oci_digests_as_compiler_build_identity(self):
        self.example["ferrule"]["build"] = self.example["images"]["ferruleService"].split("@")[1]
        with self.assertRaisesRegex(ValueError, "does not bind"):
            manifest.validate(self.example)

    def test_registry_neutral_reference_and_invalid_references(self):
        repository = "registry.example.org:5000/team/tools/server"
        digest = "sha256:" + "b" * 64
        ref = self.example["images"]
        ref["server"] = f"{repository}@{digest}"
        manifest.validate(self.example)
        for value in (
            "braid", digest, f"team/server@{digest}", f"{repository}:latest",
            f"{repository}:v1.2.3@{digest}", f"https://{repository}@{digest}",
            f"{repository}@{digest}\n", f"{repository}@{digest.upper()}",
            f"{repository}@{digest[:-1]}", f"{repository}/../escape@{digest}",
        ):
            with self.subTest(value=value), self.assertRaises(ValueError):
                ref["server"] = value
                manifest.validate(self.example)

    def test_closed_manifest_and_version_contract(self):
        for override in (
            {"schemaVersion": True}, {"schemaVersion": 2}, {"version": "v1.02.3"},
            {"version": "v1.2.3-rc1"}, {"version": "main"}, {"platform": "linux/arm64"},
            {"sourceCommit": "abcdef0"}, {"sourceCommit": "z"*40}, {"secret": "x"},
            {"ferrule": {"native": self.example["ferrule"]["native"]}},
            {"images": self.example["images"] | {"unknown": "extra"}},
        ):
            with self.subTest(override=override), self.assertRaises(ValueError):
                manifest.validate(self.example | override)
        for key in self.example["images"]:
            with self.subTest(missing=key), self.assertRaises(ValueError):
                manifest.validate(self.example | {"images": {k: v for k, v in self.example["images"].items() if k != key}})

    def test_reject_shadowed_duplicate_image_key(self):
        text = EXAMPLE.read_text(encoding="utf-8").replace('"server":', '"server": "untrusted", "server":')
        self.output.write_text(text, encoding="utf-8")
        self.cli("validate", self.output, ok=False)


if __name__ == "__main__":
    unittest.main()
