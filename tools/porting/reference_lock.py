import argparse
import json
from pathlib import Path
import subprocess
import sys


def git(repo, *args):
    return subprocess.run(
        ["git", "-C", str(repo), *args],
        text=True,
        capture_output=True,
        check=False,
    )


def current_commit(repo):
    result = git(repo, "rev-parse", "--verify", "HEAD^{commit}")
    if result.returncode:
        raise ValueError(f"{repo} is not a valid Git checkout: {result.stderr.strip()}")
    detached = git(repo, "symbolic-ref", "--quiet", "HEAD")
    if detached.returncode == 0:
        raise ValueError(f"{repo} must be a detached checkout")
    return result.stdout.strip()


def lock(args):
    if not args.label.strip() or not args.note.strip():
        raise ValueError("label and note must be non-empty")
    commit = current_commit(args.repo)
    record = {"commit": commit, "label": args.label, "note": args.note}
    destination = Path(args.dest)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    print(f"Locked {args.label} at {commit}")


def check(args):
    record = json.loads(Path(args.dest).read_text(encoding="utf-8"))
    commit = current_commit(args.repo)
    if commit != record["commit"]:
        print(
            f"Reference mismatch: locked {record['commit']}, checkout is {commit}",
            file=sys.stderr,
        )
        return 1
    print(f"Reference lock OK: {record['label']} @ {commit}")
    return 0


def main():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="action", required=True)
    lock_parser = subparsers.add_parser("lock")
    lock_parser.add_argument("--repo", required=True, type=Path)
    lock_parser.add_argument("--dest", required=True, type=Path)
    lock_parser.add_argument("--note", required=True)
    lock_parser.add_argument("--label", required=True)
    check_parser = subparsers.add_parser("check")
    check_parser.add_argument("--repo", required=True, type=Path)
    check_parser.add_argument("--dest", required=True, type=Path)
    args = parser.parse_args()
    try:
        return lock(args) or 0 if args.action == "lock" else check(args)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"reference lock error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
