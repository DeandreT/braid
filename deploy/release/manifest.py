"""Generate or validate a Braid product release manifest, without registry access."""

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def read_json(path):
    with Path(path).open(encoding="utf-8") as stream:
        return json.load(stream, object_pairs_hook=unique_object)


SCHEMA = read_json(Path(__file__).with_name("release-manifest.schema.json"))
IMAGE_FLAGS = {
    "server": "server",
    "edge": "edge",
    "worker": "worker",
    "mappingEndpoint": "mapping-endpoint",
    "ferruleService": "ferrule-service",
}


def check(value, rule, path):
    """Apply the closed objects, scalar types, constants and patterns in our v1 schema."""
    if "$ref" in rule:
        rule = SCHEMA["$defs"][rule["$ref"].removeprefix("#/$defs/")]
    kinds = {"string": str, "integer": int, "object": dict}
    if type(value) is not kinds[rule["type"]]:
        raise ValueError(f"{path}: expected {rule['type']}")
    if "const" in rule and value != rule["const"]:
        raise ValueError(f"{path}: expected {rule['const']}")
    if "pattern" in rule and not re.fullmatch(rule["pattern"], value):
        raise ValueError(f"{path}: incorrect format")
    if rule["type"] == "object":
        fields = rule["properties"]
        for name in rule["required"]:
            if name not in value:
                raise ValueError(f"{path}: missing {name}")
        if rule["additionalProperties"] is False and value.keys() - fields.keys():
            raise ValueError(f"{path}: unexpected field")
        for name, field in value.items():
            check(field, fields[name], f"{path}.{name}")


def validate(manifest):
    check(manifest, SCHEMA, "manifest")
    if "ferrule" in manifest:
        pins = manifest["ferrule"]
        material = f"braid.compiler-identity.v1\nnative={pins['native']}\ntoolchain={pins['toolchain']}\n"
        if pins["build"] != "sha256:" + hashlib.sha256(material.encode("ascii")).hexdigest():
            raise ValueError("ferrule.build does not bind ferrule.native and ferrule.toolchain")


def collect_pins(compiler_file, runtime_file):
    compiler = read_json(compiler_file)
    runtime = read_json(runtime_file)
    for doc, schema, keys in [
        (compiler, "braid.compiler-image-identity.v1", {"native", "toolchain", "build"}),
        (runtime, "braid.compiled-runtime-image-identity.v1", {"native", "runtime"}),
    ]:
        if type(doc) is not dict or doc.keys() != keys | {"schema"} or doc["schema"] != schema:
            raise ValueError(f"expected the {schema} report")
    if compiler["native"] != runtime["native"]:
        raise ValueError("Ferrule reports disagree on the native identity")
    return {key: compiler[key] for key in ("native", "toolchain", "build")} | {"runtime": runtime["runtime"]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    generate = commands.add_parser("generate")
    generate.add_argument("--version", required=True)
    generate.add_argument("--source-commit", required=True)
    for role, flag in IMAGE_FLAGS.items():
        generate.add_argument("--" + flag, dest=role, required=True)
    generate.add_argument("--compiler-identity", type=Path)
    generate.add_argument("--runtime-identity", type=Path)
    generate.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("validate")
    verify.add_argument("manifest", type=Path)
    verify.add_argument("--expect-version")
    verify.add_argument("--expect-source-commit")
    args = parser.parse_args(argv)
    try:
        if args.action == "validate":
            manifest = read_json(args.manifest)
            validate(manifest)
            for expected, key in ((args.expect_version, "version"), (args.expect_source_commit, "sourceCommit")):
                if expected is not None and manifest[key] != expected:
                    raise ValueError(f"manifest.{key} does not match expected value")
        else:
            manifest = {
                "schemaVersion": 1,
                "version": args.version,
                "sourceCommit": args.source_commit,
                "platform": "linux/amd64",
                "images": {role: getattr(args, role) for role in IMAGE_FLAGS},
            }
            if bool(args.compiler_identity) != bool(args.runtime_identity):
                raise ValueError("provide both Ferrule identity reports or neither")
            if args.compiler_identity:
                manifest["ferrule"] = collect_pins(args.compiler_identity, args.runtime_identity)
            validate(manifest)
            args.output.write_bytes((json.dumps(manifest, indent=2) + "\n").encode("utf-8"))
    except (OSError, ValueError, RecursionError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    sys.exit(main())
