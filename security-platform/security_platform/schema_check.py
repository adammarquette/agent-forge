"""A JSON Schema subset validator for the platform's own schemas, stdlib only.

The image ships no third-party packages, so this covers exactly the keywords schema/*.json use and
refuses any other keyword rather than ignoring it: a schema cannot silently lean on one it lacks.
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from security_platform.allowlist import platform_root

_ANNOTATIONS = frozenset({"$schema", "$id", "$defs", "title", "description"})
_KEYWORDS = frozenset(
    {
        "type", "enum", "const", "required", "properties", "additionalProperties", "items",
        "minItems", "maxItems", "minLength", "maxLength", "pattern", "minimum", "maximum",
        "$ref", "allOf", "if", "then",
    }
)


class SchemaError(Exception):
    """The schema itself uses something this validator does not implement."""


def schema_dir() -> Path:
    return platform_root() / "schema"


def load_schema(name: str) -> dict[str, Any]:
    return json.loads((schema_dir() / name).read_text(encoding="utf-8"))


def _type_ok(value: Any, name: str) -> bool:
    if name == "object":
        return isinstance(value, dict)
    if name == "array":
        return isinstance(value, list)
    if name == "string":
        return isinstance(value, str)
    if name == "boolean":
        return isinstance(value, bool)
    if name == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if name == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if name == "null":
        return value is None
    raise SchemaError(f"unknown type {name}")


def _same(a: Any, b: Any) -> bool:
    # Python has 1 == True; JSON does not, so const and enum compare type-strictly.
    return json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)


class _Validator:
    def __init__(self, root_name: str) -> None:
        self._docs: dict[str, dict[str, Any]] = {}
        self._root_name = root_name

    def _doc(self, name: str) -> dict[str, Any]:
        if name not in self._docs:
            self._docs[name] = load_schema(name)
        return self._docs[name]

    def _resolve(self, ref: str, doc_name: str) -> tuple[dict[str, Any], str]:
        file_part, _, fragment = ref.partition("#")
        target_name = file_part or doc_name
        node: Any = self._doc(target_name)
        for piece in [p for p in fragment.split("/") if p]:
            node = node[piece]
        return node, target_name

    def errors(self, value: Any, schema: dict[str, Any], doc_name: str, path: str) -> list[str]:
        unknown = set(schema) - _KEYWORDS - _ANNOTATIONS
        if unknown:
            raise SchemaError(f"unsupported keyword(s) {sorted(unknown)} at {path}")
        out: list[str] = []
        if "$ref" in schema:
            target, target_doc = self._resolve(schema["$ref"], doc_name)
            out += self.errors(value, target, target_doc, path)
        if "type" in schema:
            names = schema["type"] if isinstance(schema["type"], list) else [schema["type"]]
            if not any(_type_ok(value, n) for n in names):
                return out + [f"{path}: expected {names}"]
        if "const" in schema and not _same(value, schema["const"]):
            out.append(f"{path}: must equal {schema['const']!r}")
        if "enum" in schema and not any(_same(value, e) for e in schema["enum"]):
            out.append(f"{path}: not one of {schema['enum']}")
        if isinstance(value, str):
            if "minLength" in schema and len(value) < schema["minLength"]:
                out.append(f"{path}: shorter than {schema['minLength']}")
            if "maxLength" in schema and len(value) > schema["maxLength"]:
                out.append(f"{path}: longer than {schema['maxLength']}")
            if "pattern" in schema and not re.search(schema["pattern"], value):
                out.append(f"{path}: does not match {schema['pattern']}")
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if "minimum" in schema and value < schema["minimum"]:
                out.append(f"{path}: below {schema['minimum']}")
            if "maximum" in schema and value > schema["maximum"]:
                out.append(f"{path}: above {schema['maximum']}")
        if isinstance(value, list):
            if "minItems" in schema and len(value) < schema["minItems"]:
                out.append(f"{path}: fewer than {schema['minItems']} items")
            if "maxItems" in schema and len(value) > schema["maxItems"]:
                out.append(f"{path}: more than {schema['maxItems']} items")
            if "items" in schema:
                for i, item in enumerate(value):
                    out += self.errors(item, schema["items"], doc_name, f"{path}[{i}]")
        if isinstance(value, dict):
            for key in schema.get("required", []):
                if key not in value:
                    out.append(f"{path}: missing {key}")
            props = schema.get("properties", {})
            extra = schema.get("additionalProperties", True)
            for key, item in value.items():
                if key in props:
                    out += self.errors(item, props[key], doc_name, f"{path}.{key}")
                elif extra is False:
                    out.append(f"{path}: unexpected property {key}")
                elif isinstance(extra, dict):
                    out += self.errors(item, extra, doc_name, f"{path}.{key}")
        for sub in schema.get("allOf", []):
            out += self.errors(value, sub, doc_name, path)
        if "if" in schema and not self.errors(value, schema["if"], doc_name, path):
            out += self.errors(value, schema.get("then", {}), doc_name, path)
        return out


def validate(value: Any, schema_name: str) -> list[str]:
    """Every violation of schema/<schema_name> by value; empty when it conforms."""
    v = _Validator(schema_name)
    return v.errors(value, v._doc(schema_name), schema_name, "$")
