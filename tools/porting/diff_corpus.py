import argparse
import json
from pathlib import Path
import subprocess
import sys


def run_case(command, input_path):
    result = subprocess.run(
        command,
        shell=True,
        input=input_path.read_text(encoding="utf-8"),
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode:
        raise RuntimeError(
            f"command failed for {input_path.name} ({result.returncode}): "
            f"{result.stderr.strip()}"
        )
    return json.loads(result.stdout)


def cases(directory):
    inputs = sorted(directory.glob("*.input.json"))
    if not inputs:
        raise ValueError(f"no *.input.json cases found in {directory}")
    return inputs


def record(args):
    for input_path in cases(args.corpus):
        response = run_case(args.cmd, input_path)
        expected = input_path.with_name(input_path.name.replace(".input.json", ".expected.json"))
        expected.write_text(
            json.dumps(response, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        print(f"Recorded {expected}")
    return 0


def verify(args):
    failures = 0
    for input_path in cases(args.corpus):
        expected_path = input_path.with_name(
            input_path.name.replace(".input.json", ".expected.json")
        )
        expected = json.loads(expected_path.read_text(encoding="utf-8"))
        actual = run_case(args.cmd, input_path)
        if actual != expected:
            failures += 1
            print(f"Mismatch: {input_path.name}", file=sys.stderr)
            print(
                json.dumps({"expected": expected, "actual": actual}, ensure_ascii=False, indent=2),
                file=sys.stderr,
            )
        else:
            print(f"Verified {input_path.name}")
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="action", required=True)
    for action in ("record", "verify"):
        command_parser = subparsers.add_parser(action)
        command_parser.add_argument("--cmd", required=True)
        command_parser.add_argument("--corpus", required=True, type=Path)
        command_parser.add_argument("--json-canonical", action="store_true")
    args = parser.parse_args()
    try:
        return record(args) if args.action == "record" else verify(args)
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"corpus {args.action} error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
