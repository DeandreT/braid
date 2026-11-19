"""Exercise the tracked-tree boundary and the public-source gate without a remote."""

import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import source_archive as source
import source_release as publisher


TAG = "v1.2.3"
COMMIT = "a" * 40
PIN = "b" * 40
NAME = source.identities(TAG, COMMIT)


class ArchiveTests(unittest.TestCase):
    def test_archive_is_from_the_named_tree_not_the_worktree(self):
        with tempfile.TemporaryDirectory() as temporary:
            temp = Path(temporary)
            repo = temp / "repo"
            subprocess.run(["git", "init", "--quiet", repo], check=True)
            (repo / "build.sh").write_bytes(b"echo original\n")
            subprocess.run(["git", "-C", repo, "add", "build.sh"], check=True)
            oid = subprocess.check_output(["git", "-C", repo, "write-tree"], text=True).strip()
            (repo / "build.sh").write_bytes(b"changed\n")
            (repo / "untracked.env").write_bytes(b"local only")
            source.git_tree(repo, oid, temp / "tree")
            source.write_archive(temp / "tree", temp / NAME)
            with tarfile.open(temp / NAME) as tf:
                files = [m for m in tf if m.isfile()]
                self.assertEqual(1, len(files))
                self.assertEqual("braid-v1.2.3-source/build.sh", files[0].name)
                self.assertEqual(b"echo original\n", tf.extractfile(files[0]).read())
            (repo / ".gitattributes").write_text("build.sh export-ignore\n")
            subprocess.run(["git", "-C", repo, "add", ".gitattributes"], check=True)
            hidden = subprocess.check_output(["git", "-C", repo, "write-tree"], text=True).strip()
            with self.assertRaisesRegex(ValueError, "differs from tracked tree"):
                source.git_tree(repo, hidden, temp / "incomplete")

    def test_distinct_or_unpinned_ferrule_inputs_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for i, pin in enumerate([PIN, COMMIT]):
                path = root / f"engines/{i}/Cargo.lock"
                path.parent.mkdir(parents=True)
                path.write_text(f'[[package]]\nname = "mapping"\nsource = "git+{source.FERRULE}?rev={pin}#{pin}"\n')
            with self.assertRaisesRegex(ValueError, "identical pinned"):
                source.rust_inputs(root)
            path.write_text('[[package]]\nname="mapping"\nsource="git+https://example.com/branch"\n')
            with self.assertRaisesRegex(ValueError, "Unadmitted"):
                source.rust_inputs(root)


class SourceGateTests(unittest.TestCase):
    def test_public_tag_missing_is_distinct_from_private_or_wrong_commit(self):
        for rc, stdout, expected in [
            (2, "", False), (128, "", subprocess.CalledProcessError),
            (0, "c" * 40 + "\trefs/tags/source-v1.2.3\n", ValueError),
            (0, "c" * 40 + "\trefs/tags/source-v1.2.3\n" + COMMIT + "\trefs/tags/source-v1.2.3^{}\n", True),
        ]:
            result = subprocess.CompletedProcess(["git"], rc, stdout)
            with self.subTest(rc=rc, expected=expected), patch.object(publisher.subprocess, "run", return_value=result):
                if isinstance(expected, bool):
                    self.assertIs(expected, publisher.public_tag_exists("https://example.com/repo", "source-" + TAG, COMMIT))
                else:
                    with self.assertRaises(expected):
                        publisher.public_tag_exists("https://example.com/repo", "source-" + TAG, COMMIT)

    def test_retry_reuses_only_signed_public_assets_with_matching_bytes_and_pin(self):
        data = b"original release bytes"
        sha = hashlib.sha256(data).hexdigest()
        record = {"version": TAG, "braidCommit": COMMIT, "ferruleCommit": PIN, "bytes": len(data),
                  "archive": NAME, "sha256": sha}
        for fault in (None, "missing", "corrupt", "signature", "pin"):
            with self.subTest(fault=fault):
                def public_command(cmd, **kwargs):
                    if cmd[0] == "cosign" and cmd[1] == "verify-blob":
                        self.assertIn("--certificate-identity=publisher", cmd)
                        if fault == "signature":
                            raise subprocess.CalledProcessError(1, cmd)
                    else:
                        self.assertEqual("curl", cmd[0])  # No upload, overwrite or signing during recovery.
                        path = cmd[cmd.index("--output") + 1]
                        if fault == "missing":
                            raise subprocess.CalledProcessError(22, cmd)
                        path.write_bytes({NAME: data.upper() if fault == "corrupt" else data,
                                          "source.json": json.dumps(record).encode(),
                                          "source.json.bundle": b"test"}[path.name])

                with patch.object(publisher, "public_tag_exists", return_value=True), \
                        patch.object(publisher, "build", side_effect=AssertionError("must not rebuild")), \
                        patch.object(publisher, "rust_inputs", return_value=([], COMMIT if fault == "pin" else PIN, [])), \
                        patch.object(publisher.subprocess, "run", side_effect=public_command):
                    args = (TAG, COMMIT, "https://example.com/repo", "publisher")
                    if fault:
                        with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                            publisher.publish(*args)
                    else:
                        self.assertEqual((f"{args[2]}/releases/download/source-{TAG}/{NAME}", sha), publisher.publish(*args))
