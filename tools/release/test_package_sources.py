import io
import hashlib
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import package_sources as sources


def tar_bytes(files):
    out = io.BytesIO()
    with tarfile.open(fileobj=out, mode="w:gz") as tf:
        for name, value in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(value)
            tf.addfile(info, io.BytesIO(value))
    return out.getvalue()


class PackageSourcesTest(unittest.TestCase):
    def test_bun_follows_nested_resolution_and_peers_excluding_dev_tools(self):
        web = {"name": "@site/web", "dependencies": {"app": "1"}, "devDependencies": {"dev": "1"}}
        def row(name, deps=None):
            return [name + "@1.0", "", deps or {}, "sha512-test"]
        lock = {"workspaces": {"apps/web": web}, "packages": {
            "app": row("app", {"dependencies": {"lib": "*"}, "peerDependencies": {"peer": "*", "absent": "*"},
                               "optionalPeers": ["absent"]}),
            "dev": row("dev"), "peer": row("peer"), "app/lib": row("lib"), "lib": row("lib")}}
        self.assertEqual([x[0] for x in sources.browser_closure(lock, web)], ["app", "app/lib", "peer"])
        del lock["packages"]["peer"]
        with self.assertRaisesRegex(ValueError, "required dependency"):
            sources.browser_closure(lock, web)

    def test_incomplete_angular_map_fails_even_for_valid_package_bytes(self):
        for text in [None, "export const source = 1"]:
            data = tar_bytes({"package/fesm2022/app.mjs": b"1",
                              "package/fesm2022/app.mjs.map": json.dumps(
                                  {"sources": ["app.ts"], "sourcesContent": [text]}).encode()})
            with tarfile.open(fileobj=io.BytesIO(data)) as tf:
                if text is None:
                    with self.assertRaisesRegex(ValueError, "incomplete Angular"):
                        sources.angular_maps(tf)
                else:
                    self.assertEqual(sources.angular_maps(tf)["sourceEntries"], 1)

    def test_nuget_hash_ignores_signature_but_not_package_payload(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as z:
            z.writestr("project.cs", b"class Source {}")
        original = stream.getvalue()
        with zipfile.ZipFile(stream, "a") as z:
            z.writestr(".signature.p7s", b"fixture")
        signed = stream.getvalue()
        for data, expected in [(signed, True), (signed.replace(b"class Source", b"class Other_"), False)]:
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                self.assertEqual(sources.nuget_hash(data, z) == sources.sha512(original), expected)

    def test_nuspec_requires_a_pinned_source_not_just_a_binary(self):
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w") as z:
            z.writestr("Demo.nuspec", "<package><metadata><id>Demo</id><version>1.0</version></metadata></package>")
            z.writestr("lib/net8.0/demo.dll", b"binary")
        data = out.getvalue()
        with tempfile.TemporaryDirectory() as tmp, patch.object(sources, "download", return_value=data):
            for digest, error in [("bad", "integrity mismatch"), (sources.sha512(data), "manual source review")]:
                with self.assertRaisesRegex(ValueError, error):
                    sources.nuget_package(("Demo", "1.0", digest), Path(tmp))

    def test_nuget_lock_superset_excludes_dev_and_requires_every_app_lock(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for folder, pkg in [("apps/Product", "prod"), ("tools/OperatorTool", "operator"),
                                ("tools/InspectionProbe", "probe"), ("tests/SomeTests", "dev")]:
                p = root / "dotnet" / folder
                p.mkdir(parents=True)
                (p / "App.csproj").touch()
                (p / "packages.lock.json").write_text(json.dumps({"version": 1, "dependencies": {
                    "net10.0": {pkg: {"type": "Direct", "resolved": "1.0", "contentHash": "hash"},
                                "internal": {"type": "Project"}}}}))
            self.assertEqual({r[0] for r in sources.nuget_closure(root)}, {"prod", "operator"})
            (root / "dotnet/apps/Product/packages.lock.json").unlink()
            with self.assertRaises(FileNotFoundError):
                sources.nuget_closure(root)

    def test_archive_deduplicates_and_rejects_mutable_sources(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(sources, "download", return_value=
                tar_bytes({"project/file.cs": b"int i;"})) as get:
            root = Path(tmp)
            for _ in range(2):
                path = sources.archive(root, "org/repo", "a" * 40)
                self.assertTrue((root / path).is_file())
            with self.assertRaisesRegex(ValueError, "invalid Git source"):
                sources.archive(root, "org/repo", "main")
            self.assertEqual(get.call_count, 1)

    def test_monorepo_selects_real_project_tree_and_flags_ambiguity(self):
        ref, tree = "1" * 40, "2" * 40
        files = [b"src/Demo/src/Demo.csproj", b"src/Demo/ref/Demo.csproj", b"src/shims/stubs/Demo.csproj"]
        def git(cmd, **unused):
            if "ls-tree" in cmd:
                return b"\0".join(files) + b"\0"
            if "rev-parse" in cmd:
                return (ref if cmd[-1] == "FETCH_HEAD" else tree).encode()
            return b""
        with tempfile.TemporaryDirectory() as tmp, patch.object(sources.subprocess, "check_output", side_effect=git), \
                patch.object(sources, "archive", return_value="git/subtree.tar.gz") as save:
            root, row = Path(tmp), {"name": "Demo"}
            sources.monorepo_sources("dotnet/dotnet", ref, [row], root, root)
            save.assert_called_once_with(root, "dotnet/dotnet", tree)
            self.assertEqual(row["source"]["root"], "src/Demo")
            self.assertEqual(row["source"]["completeRepository"], sources.github("dotnet/dotnet", ref))
            files.append(b"alternative/src/Demo.csproj")
            with self.assertRaisesRegex(ValueError, "project matches"):
                sources.monorepo_sources("dotnet/dotnet", ref, [row], root, root)

    def test_collect_refuses_nonempty_destination_before_network(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp)
            (p / "unrelated").touch()
            with self.assertRaisesRegex(ValueError, "must be empty"):
                sources.collect(p, p)

    def test_reconstructed_overlay_requires_exact_pdb_document(self):
        data = b"public class Patched {}\n"
        original = data.replace(b"\n", b"\r\n")
        path = "src/Patched.cs"
        with tempfile.TemporaryDirectory() as tmp, patch.object(sources, "archive", return_value="base.tar.gz"), \
                patch.object(sources, "download", return_value=data):
            out = Path(tmp)
            def collect(digest):
                return sources.reconstructed_source(out, "test/repo", "0" * 40, "1" * 40,
                                                    "nuget/overlay", {path: digest}, "net8.0")
            with self.assertRaisesRegex(ValueError, "PDB document checksum"):
                collect("bad")
            index = collect(hashlib.sha256(original).hexdigest())
            self.assertEqual((out / index["applyOverlayAtRoot"] / path).read_bytes(), original)
