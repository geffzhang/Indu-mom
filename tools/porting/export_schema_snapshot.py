import argparse
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
LOCK_PATH = ROOT / "porting" / "reference-lock.json"


def reference_commit(reference):
    return subprocess.check_output(
        ["git", "-C", str(reference), "rev-parse", "HEAD"],
        text=True,
        stderr=subprocess.PIPE,
    ).strip()


def sqlalchemy_type(column_type):
    result = {"name": type(column_type).__name__}
    for attribute in ("length", "precision", "scale"):
        value = getattr(column_type, attribute, None)
        if value is not None:
            result[attribute] = value
    return result


def table_snapshot(table):
    columns = []
    for column in table.columns:
        columns.append(
            {
                "name": column.name,
                "type": sqlalchemy_type(column.type),
                "nullable": column.nullable,
                "primary_key": column.primary_key,
                "autoincrement": column.autoincrement,
            }
        )

    indexes = [
        {
            "name": index.name,
            "columns": [column.name for column in index.columns],
            "unique": index.unique,
        }
        for index in table.indexes
    ]
    for constraint in table.constraints:
        if constraint.__class__.__name__ == "UniqueConstraint":
            indexes.append(
                {
                    "name": constraint.name,
                    "columns": [column.name for column in constraint.columns],
                    "unique": True,
                }
            )
    indexes.sort(
        key=lambda index: (
            tuple(index["columns"]),
            index["name"] or "",
            index["unique"],
        )
    )
    return {
        "columns": columns,
        "primary_key": [column.name for column in table.primary_key.columns],
        "indexes": indexes,
    }


def build_snapshot(reference):
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    actual_commit = reference_commit(reference)
    if actual_commit != lock["commit"]:
        raise ValueError(
            f"reference checkout is {actual_commit}, expected locked commit {lock['commit']}"
        )

    sys.path.insert(0, str(reference))
    from honeymom import schema

    entity_specs = schema.spec_json()
    entity_definitions = {entity.name: entity for entity in schema.ENTITIES}
    for spec in entity_specs:
        spec["order"] = entity_definitions[spec["name"]].order

    return {
        "version": 1,
        "reference_commit": actual_commit,
        "entities": entity_specs,
        "tables": {
            name: table_snapshot(table)
            for name, table in sorted(schema.metadata.tables.items())
        },
    }


def serialized_snapshot(snapshot):
    return (json.dumps(snapshot, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode(
        "utf-8"
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    try:
        content = serialized_snapshot(build_snapshot(args.reference.resolve()))
        if args.check:
            if not args.output.is_file() or args.output.read_bytes() != content:
                print(f"schema snapshot is stale: {args.output}", file=sys.stderr)
                return 1
            print(f"schema snapshot is current: {args.output}")
            return 0

        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(content)
        print(f"wrote schema snapshot: {args.output}")
        return 0
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"schema snapshot error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
