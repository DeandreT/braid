import importlib.util
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("check_imports", pathlib.Path(__file__).with_name("check-imports.py"))
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


def leb(value):
    data = bytearray()
    while value >= 128:
        data.append((value & 127) | 128)
        value >>= 7
    data.append(value)
    return bytes(data)


def text(value):
    data = value.encode()
    return leb(len(data)) + data


class ImportAllowlistTests(unittest.TestCase):
    def check(self, field, body, kind=0):
        with tempfile.TemporaryDirectory(prefix="weft-edit-imports-") as directory:
            path = pathlib.Path(directory) / "module_bg.wasm"
            imports = leb(1) + text("./weft_ferrule_edit_wasm_bg.js") + text(field) + bytes([kind, 0])
            path.write_bytes(b"\0asm\x01\0\0\0" + bytes([2]) + leb(len(imports)) + imports)
            path.with_name("module.js").write_text(f"{field}: function(arg0, arg1) {{ {body} }}")
            return checker.check(path)

    def test_error_intrinsic_is_admitted(self):
        self.assertEqual(len(self.check("__wbg___wbindgen_throw_123", "throw new Error(getStringFromWasm0(arg0, arg1));")), 1)

    def test_network_storage_and_generic_replacement_are_rejected(self):
        for field, body in [
            ("__wbg_fetch_123", "return fetch(arg0);"),
            ("__wbg_localStorage_123", "return localStorage;"),
            ("__wbindgen_generic_0000000000000001", "return fetch(arg0);"),
            ("__wbg___wbindgen_throw_123", "fetch(arg0); throw new Error(arg1);"),
        ]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.check(field, body)

    def test_non_function_import_is_rejected(self):
        with self.assertRaises(ValueError):
            self.check("memory", "", kind=2)


if __name__ == "__main__":
    unittest.main()
