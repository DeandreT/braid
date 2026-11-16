"""Keep source checkouts portable without changing exact-byte fixtures."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class CheckoutLineEndingTests(unittest.TestCase):
    def test_checkout_preserves_lf_source_and_exact_fixture_bytes(self):
        git = shutil.which("git")
        self.assertIsNotNone(git, "Git is required to verify checkout attributes.")
        attributes = (Path(__file__).resolve().parents[2] / ".gitattributes").read_bytes()
        files = {
            "source.cs": b"// source\nclass Example {}\n",
            "script.sh": b"#!/bin/sh\nexit 0\n",
            "migration.sql": b"select 1;\n",
            "package.json": b'{"name":"checkout-fixture"}\n',
            "guide.md": "# Checkout\nUnicode: caf\u00e9\n".encode("utf-8"),
            "image.bin": b"\x00\x01binary\r\nbytes\n\xff",
            "tools/qualification-capture/fixtures/mixed.txt": b"one\r\ntwo\nthree\r\n",
            "tools/qualification-capture/fixtures/lf.txt": b"exact\nfixture\n",
        }
        with tempfile.TemporaryDirectory(prefix="braid-checkout-test-") as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            empty_attributes = root / "empty-attributes"
            empty_attributes.touch()
            hooks = root / "hooks"
            hooks.mkdir()

            def run(*arguments, autocrlf="false"):
                subprocess.run([
                    git, "-c", "core.autocrlf=" + autocrlf,
                    "-c", "core.safecrlf=false",
                    "-c", "core.attributesFile=" + str(empty_attributes),
                    "-c", "core.hooksPath=" + str(hooks),
                    "-c", "commit.gpgSign=false",
                    "-c", "user.name=Braid checkout test",
                    "-c", "user.email=checkout@braid.invalid",
                    *arguments,
                ], cwd=source, check=True, capture_output=True, timeout=30)

            run("init")
            (source / ".gitattributes").write_bytes(attributes)
            for name, content in files.items():
                path = source / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
            run("add", ".")
            run("commit", "-m", "Self-authored checkout fixture")
            for autocrlf in ("true", "false", "input"):
                with self.subTest(autocrlf=autocrlf):
                    checkout = root / ("checkout-" + autocrlf)
                    run("worktree", "add", "--detach", str(checkout), "HEAD", autocrlf=autocrlf)
                    for name, expected in files.items():
                        self.assertEqual(expected, (checkout / name).read_bytes(), name)
