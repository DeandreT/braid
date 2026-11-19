"""Package a synthetic Angular output tree using the same script as the container build."""

from html.parser import HTMLParser
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.urls = []

    def handle_starttag(self, tag, attrs):
        if tag == "a":
            self.urls.append(dict(attrs)["href"])


class WebNoticesTest(unittest.TestCase):
    @unittest.skipUnless(shutil.which("node") or shutil.which("bun"), "requires JS runtime")
    def test_packaged_page_links_to_the_exact_archive_and_existing_static_notices(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            dist = root / "apps/web/dist/braid"
            web = dist / "browser"
            third = web / "edit/third-party"
            shutil.copytree(ROOT / "engines/ferrule-host/third-party", third)
            (third / "rust-std-COPYRIGHT.html").write_text("Rust standard library notices")
            (dist / "3rdpartylicenses.txt").write_text("Bundle notices")
            shutil.copyfile(ROOT / "LICENSE", root / "LICENSE")
            url = "https://sources.example.org/braid/v1.2.3/src.tgz?type=tar&build=final"
            for args in ([], ["v1.2.3", url], ["v1.2.3", "javascript:alert(1)"]):
                shutil.copyfile(ROOT / "apps/web/public/licenses.html", web / "licenses.html")
                p = subprocess.run([shutil.which("node") or shutil.which("bun"),
                                    ROOT / "apps/web/tools/package-notices.mjs", *args], cwd=root, capture_output=True)
                if args and args[1] != url:
                    self.assertNotEqual(0, p.returncode)
                    continue
                self.assertEqual(0, p.returncode, p.stderr)
                page = Links()
                page.feed((web / "licenses.html").read_text(encoding="utf-8"))
                self.assertEqual(bool(args), url in page.urls)
                self.assertIn("3rdpartylicenses.txt", page.urls)
                for path in page.urls:
                    if not path.startswith("https:"):
                        self.assertTrue((web / path).is_file(), path)
