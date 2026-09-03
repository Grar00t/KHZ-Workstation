from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

_SCHEMA_TYPES = {"text": "TEXT", "int": "INTEGER", "float": "REAL", "date": "TEXT", "bool": "TEXT"}
_SCI = re.compile(r"^[+-]?(?:\d+\.?\d*|\.\d+)[eE][+-]?\d+$")


class SchemaError(ValueError):
    pass


@dataclass(frozen=True)
class SchemaColumn:
    name: str
    declared_type: str
    sql_type: str
    nullable: bool
    pk: bool


@dataclass(frozen=True)
class TableSchema:
    columns: tuple[SchemaColumn, ...]
    source: Path


def sidecar_path(source: Path) -> Path:
    return source.with_name(source.stem + ".schema.json")


def load_sidecar(source: Path) -> TableSchema:
    path = sidecar_path(source)
    if not path.is_file():
        raise SchemaError(f"SCHEMA_MISSING: expected sidecar {path.name}")
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raise SchemaError("ENC_SCHEMA_BOM: schema JSON must not include a UTF-8 BOM")
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SchemaError(f"SCHEMA_INVALID: {exc}") from exc
    columns = payload.get("columns") if isinstance(payload, dict) else None
    if not isinstance(columns, list) or not columns:
        raise SchemaError("SCHEMA_INVALID: columns list is required")
    seen: set[str] = set()
    parsed: list[SchemaColumn] = []
    for entry in columns:
        if not isinstance(entry, dict):
            raise SchemaError("SCHEMA_INVALID: column entries must be objects")
        name = str(entry.get("name") or "").strip()
        declared = str(entry.get("type") or "").strip().casefold()
        if not name:
            raise SchemaError("SCHEMA_INVALID: column name is required")
        if name.casefold() in seen:
            raise SchemaError(f"TYPE_MULTI: duplicate column {name}")
        if declared not in _SCHEMA_TYPES:
            raise SchemaError(f"SCHEMA_INVALID: unsupported type {declared} for {name}")
        seen.add(name.casefold())
        parsed.append(
            SchemaColumn(
                name=name,
                declared_type=declared,
                sql_type=_SCHEMA_TYPES[declared],
                nullable=bool(entry.get("nullable", True)),
                pk=bool(entry.get("pk", False)),
            )
        )
    return TableSchema(columns=tuple(parsed), source=path)


def sqlite_types_for_headers(schema: TableSchema, headers: list[str]) -> list[str]:
    index = {c.name.casefold(): c for c in schema.columns}
    types: list[str] = []
    unused = set(index)
    for header in headers:
        key = header.casefold()
        col = index.get(key)
        if col is None:
            raise SchemaError(f"TYPE_UNDECLARED: column {header} is absent from sidecar")
        unused.discard(key)
        types.append(col.sql_type)
    if unused:
        raise SchemaError(f"SCHEMA_COLUMN_MISSING: sidecar columns not in file: {sorted(unused)}")
    return types


def value_violates_schema(text: str, declared: str) -> str | None:
    if declared == "text":
        return None
    if declared == "int":
        body = text[1:] if text[:1] in "+-" else text
        if not body.isdigit() or (len(body) > 1 and body.startswith("0")):
            return "TYPE_INT"
        if len(body) > 15:
            return "TYPE_DIGIT_PRECISION"
        return None
    if declared == "float":
        if _SCI.match(text):
            return "TYPE_SCIENTIFIC"
        try:
            float(text)
        except ValueError:
            return "TYPE_FLOAT"
        return None
    if declared == "date":
        if len(text) != 10 or text[4] != "-" or text[7] != "-":
            return "TYPE_DATE_NOT_ISO"
        if not (text[0:4].isdigit() and text[5:7].isdigit() and text[8:10].isdigit()):
            return "TYPE_DATE_NOT_ISO"
        return None
    if declared == "bool":
        if text.casefold() not in {"true", "false", "0", "1"}:
            return "TYPE_BOOL"
        return None
    return "SCHEMA_INVALID"
