import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[2]


def create_database(reference, data_directory, mode):
    if mode != "production":
        raise ValueError(f"unsupported fixture mode: {mode}")

    reference = reference.resolve()
    data_directory = data_directory.resolve()
    lock = json.loads(
        (ROOT / "porting" / "reference-lock.json").read_text(encoding="utf-8")
    )
    actual_commit = subprocess.check_output(
        ["git", "-C", str(reference), "rev-parse", "HEAD"], text=True
    ).strip()
    if actual_commit != lock["commit"]:
        raise ValueError(
            f"reference checkout is {actual_commit}, expected locked commit {lock['commit']}"
        )
    if data_directory.exists() and any(data_directory.iterdir()):
        raise ValueError(f"refusing to initialize a non-empty data directory: {data_directory}")

    data_directory.mkdir(parents=True, exist_ok=True)
    database_path = data_directory / "mes.db"
    database_url = "sqlite:///" + str(database_path).replace("\\", "/")
    os.environ["MOM_DATA"] = str(data_directory)
    os.environ["MOM_DB"] = database_url
    os.environ["MOM_DEMO"] = "0"
    os.environ["MOM_BLANK"] = "1"
    sys.path.insert(0, str(reference))

    from honeymom import db, seed

    try:
        db.init(database_url, migrate_db=True)
        seed.run(blank=True)
    finally:
        if db.engine is not None:
            db.engine.dispose()

    return database_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--data-dir", required=True, type=Path)
    parser.add_argument("--mode", choices=("production",), required=True)
    args = parser.parse_args()
    database_path = create_database(args.reference, args.data_dir, args.mode)
    print(database_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
