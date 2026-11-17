import argparse
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dest", required=True, type=Path)
    parser.add_argument("--name", required=True)
    parser.add_argument("--from", dest="source_language", required=True)
    parser.add_argument("--to", dest="target_language", required=True)
    args = parser.parse_args()
    destination = args.dest
    files = {
        "00-reference.md": (
            f"# {args.name} Reference\n\n"
            f"Porting from {args.source_language} to {args.target_language}.\n\n"
            "Upstream synchronization target: `HoneyMeta/HoneyMOM`.\n"
        ),
        "01-baseline.md": (
            f"# {args.name} Behavior Baseline\n\n"
            "Record exact commands, test totals, provider coverage, and corpus status.\n"
        ),
        "differences.jsonl": "",
    }
    try:
        existing = [name for name in files if (destination / name).exists()]
        if existing:
            print(
                f"refusing to overwrite existing ledger files: {', '.join(existing)}",
                file=sys.stderr,
            )
            return 1
        destination.mkdir(parents=True, exist_ok=True)
        for name, content in files.items():
            (destination / name).write_text(content, encoding="utf-8")
        print(f"Initialized {destination}")
        return 0
    except OSError as error:
        print(f"ledger initialization error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
