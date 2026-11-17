import argparse
import json
from pathlib import Path
import sys


REQUIRED_FIELDS = ("id", "behavior", "cause", "decision", "test")


def ledger_path(directory):
    return directory / "differences.jsonl"


def read_entries(directory):
    path = ledger_path(directory)
    if not path.exists():
        raise ValueError(f"missing difference ledger: {path}")
    entries = []
    errors = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError as error:
            errors.append(f"line {line_number}: invalid JSON ({error})")
            continue
        if not isinstance(entry, dict):
            errors.append(f"line {line_number}: entry must be an object")
            continue
        missing = [field for field in REQUIRED_FIELDS if not isinstance(entry.get(field), str) or not entry[field].strip()]
        if missing:
            errors.append(f"line {line_number}: missing required fields: {', '.join(missing)}")
        else:
            entries.append(entry)
    return entries, errors


def main():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="action", required=True)
    for action in ("lint", "render"):
        action_parser = subparsers.add_parser(action)
        action_parser.add_argument("--dir", required=True, type=Path)
    new_parser = subparsers.add_parser("new")
    new_parser.add_argument("--dir", required=True, type=Path)
    for field in REQUIRED_FIELDS:
        new_parser.add_argument(f"--{field}", required=True)
    args = parser.parse_args()
    try:
        if args.action == "new":
            path = ledger_path(args.dir)
            entry = {field: getattr(args, field) for field in REQUIRED_FIELDS}
            line = json.dumps(entry, ensure_ascii=False, sort_keys=True)
            with path.open("a", encoding="utf-8", newline="\n") as ledger:
                ledger.write(line + "\n")
            print(f"Registered {args.id}")
            return 0

        entries, errors = read_entries(args.dir)
        if errors:
            print("\n".join(errors), file=sys.stderr)
            return 1
        if args.action == "lint":
            print(f"Valid difference ledger ({len(entries)} entries)")
            return 0
        for entry in entries:
            print(f"## {entry['id']}\n\n- Behavior: {entry['behavior']}\n- Cause: {entry['cause']}\n- Decision: {entry['decision']}\n- Test: {entry['test']}\n")
        return 0
    except OSError as error:
        print(f"difference ledger error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
