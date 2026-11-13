"""Fail closed if the edit module acquires any browser or host capability."""

import argparse
import pathlib
import re


def unsigned(data, offset):
    value = 0
    for shift in range(0, 35, 7):
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ValueError("Invalid wasm integer")


def name(data, offset):
    length, offset = unsigned(data, offset)
    end = offset + length
    if end > len(data):
        raise ValueError("Truncated wasm name")
    return data[offset:end].decode("utf-8"), end


def check(path):
    data = pathlib.Path(path).read_bytes()
    glue = pathlib.Path(str(path).replace("_bg.wasm", ".js")).read_text()
    if data[:8] != b"\0asm\x01\0\0\0":
        raise ValueError("Not a version-1 wasm module")
    offset = 8
    observed = []
    while offset < len(data):
        section = data[offset]
        length, start = unsigned(data, offset + 1)
        end = start + length
        if end > len(data):
            raise ValueError("Truncated wasm section")
        if section == 2:
            count, cursor = unsigned(data, start)
            for _ in range(count):
                module, cursor = name(data, cursor)
                field, cursor = name(data, cursor)
                kind = data[cursor]
                cursor += 1
                if kind != 0:
                    raise ValueError(f"Forbidden non-function import: {module}.{field}")
                _, cursor = unsigned(data, cursor)
                # wasm-bindgen hashes intrinsic names as its implementation changes.
                # Match the full intrinsic stem, not general JS imports.
                if module != "./weft_ferrule_edit_wasm_bg.js":
                    raise ValueError(f"Forbidden import: {module}.{field}")
                function = re.search(re.escape(field) + r": function\(arg0, arg1\) \{([^}]+)\}", glue)
                body = " ".join(function.group(1).split()) if function else None
                permitted = (
                    field.startswith("__wbg___wbindgen_throw_")
                    and body == "throw new Error(getStringFromWasm0(arg0, arg1));"
                ) or (
                    field == "__wbindgen_generic_0000000000000001"
                    and body == '// Cast intrinsic for `Ref(String) -> Externref`. const ret = getStringFromWasm0(arg0, arg1); return addHeapObject(ret);'
                )
                if not permitted:
                    raise ValueError(f"Forbidden import implementation: {module}.{field}")
                observed.append(f"{module}.{field}")
            if cursor != end:
                raise ValueError("Unexpected import-section tail")
        offset = end
    return observed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("module")
    for item in check(parser.parse_args().module):
        print(item)
