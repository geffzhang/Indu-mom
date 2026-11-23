import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
from contextlib import closing

import platform_api_differential


ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / "tools" / "porting"


def run_tool(name, *args, input_data=None):
    return subprocess.run(
        [sys.executable, str(TOOLS / name), *map(str, args)],
        cwd=ROOT,
        input=input_data,
        text=True,
        capture_output=True,
    )


def create_reference_repo(path):
    path.mkdir()
    subprocess.run(["git", "init", "-q", str(path)], check=True)
    (path / "reference.txt").write_text("baseline\n", encoding="utf-8")
    subprocess.run(["git", "-C", str(path), "add", "."], check=True)
    subprocess.run(
        [
            "git",
            "-C",
            str(path),
            "-c",
            "user.name=Porting Test",
            "-c",
            "user.email=porting-test@example.invalid",
            "commit",
            "-qm",
            "baseline",
        ],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(path), "checkout", "--detach", "-q", "HEAD"],
        check=True,
    )


class PortingToolsTests(unittest.TestCase):
    def test_platform_fixture_marks_builtin_ai_skills_initialized(self):
        with tempfile.TemporaryDirectory() as temp:
            database = Path(temp) / "fixture.db"
            with closing(sqlite3.connect(database)) as connection:
                connection.execute("CREATE TABLE ai_skill (key TEXT)")
                connection.execute("CREATE TABLE audit_log (id INTEGER)")
                connection.commit()

            platform_api_differential.seed_builtin_ai_skills(database)

            with closing(sqlite3.connect(database)) as connection:
                keys = connection.execute(
                    "SELECT key FROM ai_skill ORDER BY key"
                ).fetchall()
                audit_count = connection.execute(
                    "SELECT COUNT(*) FROM audit_log"
                ).fetchone()[0]
            self.assertEqual(
                keys,
                [
                    ("bottleneck_action",),
                    ("energy_anomaly",),
                    ("oee_loss_top5",),
                    ("overdue_maint",),
                    ("quality_pareto",),
                    ("shift_summary",),
                ],
            )
            self.assertEqual(audit_count, 0)

    def test_time_derived_hash_normalization_validates_sha256_values(self):
        digest = "a" * 64
        normalized = platform_api_differential.apply_normalizations(
            {"items": [{"hash": digest}]},
            [{"path": "$.items[*].hash", "type": "time_derived_hash"}],
        )
        self.assertEqual(normalized, {"items": [{"hash": "<time_derived_hash>"}]})
        with self.assertRaises(platform_api_differential.DifferentialFailure):
            platform_api_differential.apply_normalizations(
                {"items": [{"hash": "not-a-digest"}]},
                [{"path": "$.items[*].hash", "type": "time_derived_hash"}],
            )

    def test_csv_normalization_supports_time_derived_hashes(self):
        digest = "b" * 64
        normalized = platform_api_differential.csv_body(
            {"body": f"Sequence,Time,Hash\n1,2024-01-02T03:04:05,{digest}\n"},
            [
                {"column": "Time", "type": "timestamp"},
                {"column": "Hash", "type": "time_derived_hash"},
            ],
        )
        self.assertEqual(
            normalized["rows"],
            [["Sequence", "Time", "Hash"], ["1", "<timestamp>", "<time_derived_hash>"]],
        )

    def test_csv_normalization_compares_json_cells_by_value(self):
        response_a = {
            "body": "Changes\n\"{\"\"name\"\": [null, \"\"A\"\"], \"\"code\"\": [null, \"\"P-1\"\"]}\"\n"
        }
        response_b = {
            "body": "Changes\n\"{\"\"code\"\": [null, \"\"P-1\"\"], \"\"name\"\": [null, \"\"A\"\"]}\"\n"
        }
        normalization = [{"column": "Changes", "type": "json_value"}]

        self.assertEqual(
            platform_api_differential.csv_body(response_a, normalization),
            platform_api_differential.csv_body(response_b, normalization),
        )

    def test_persisted_json_null_matches_sql_null(self):
        self.assertIsNone(platform_api_differential.canonical_database_value("null", True))

    def test_reference_lock_round_trip_and_commit_mismatch(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp) / "reference"
            lock = Path(temp) / "reference-lock.json"
            create_reference_repo(repo)

            recorded = run_tool(
                "reference_lock.py",
                "lock",
                "--repo",
                repo,
                "--dest",
                lock,
                "--note",
                "Initial port baseline",
                "--label",
                "HoneyMeta/HoneyMOM",
            )
            self.assertEqual(recorded.returncode, 0, recorded.stderr)
            expected_commit = subprocess.check_output(
                ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
            ).strip()
            self.assertEqual(
                json.loads(lock.read_text(encoding="utf-8"))["label"],
                "HoneyMeta/HoneyMOM",
            )
            self.assertEqual(
                json.loads(lock.read_text(encoding="utf-8"))["commit"], expected_commit
            )

            checked = run_tool(
                "reference_lock.py", "check", "--repo", repo, "--dest", lock
            )
            self.assertEqual(checked.returncode, 0, checked.stderr)
            self.assertIn(expected_commit, checked.stdout + checked.stderr)

            (repo / "reference.txt").write_text("changed\n", encoding="utf-8")
            subprocess.run(["git", "-C", str(repo), "add", "."], check=True)
            subprocess.run(
                [
                    "git",
                    "-C",
                    str(repo),
                    "-c",
                    "user.name=Porting Test",
                    "-c",
                    "user.email=porting-test@example.invalid",
                    "commit",
                    "-qm",
                    "changed",
                ],
                check=True,
            )
            mismatch = run_tool(
                "reference_lock.py", "check", "--repo", repo, "--dest", lock
            )
            self.assertNotEqual(mismatch.returncode, 0)

    def test_reference_lock_uses_stable_label_across_checkout_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            first = Path(temp) / "first" / "reference"
            second = Path(temp) / "second" / "reference"
            lock = Path(temp) / "reference-lock.json"
            first.parent.mkdir()
            second.parent.mkdir()
            create_reference_repo(first)
            subprocess.run(
                ["git", "clone", "-q", "--no-hardlinks", str(first), str(second)],
                check=True,
            )
            subprocess.run(
                ["git", "-C", str(second), "checkout", "--detach", "-q", "HEAD"],
                check=True,
            )

            recorded = run_tool(
                "reference_lock.py",
                "lock",
                "--repo",
                first,
                "--dest",
                lock,
                "--note",
                "Initial port baseline",
                "--label",
                "HoneyMeta/HoneyMOM",
            )
            checked = run_tool(
                "reference_lock.py", "check", "--repo", second, "--dest", lock
            )

            self.assertEqual(recorded.returncode, 0, recorded.stderr)
            self.assertEqual(checked.returncode, 0, checked.stderr)
            contents = lock.read_text(encoding="utf-8")
            self.assertNotIn(str(first), contents)
            self.assertNotIn(str(second), contents)

    def test_corpus_record_verify_and_mismatch(self):
        with tempfile.TemporaryDirectory() as temp:
            corpus = Path(temp) / "corpus"
            corpus.mkdir()
            (corpus / "setup.input.json").write_text(
                '{"method":"GET","path":"/health"}\n', encoding="utf-8"
            )
            responder = Path(temp) / "responder.py"
            responder.write_text(
                "import json,sys\n"
                "request=json.load(sys.stdin)\n"
                "json.dump({'status':200,'body':{'path':request['path']}},sys.stdout)\n",
                encoding="utf-8",
            )
            command = f'"{sys.executable}" "{responder}"'

            recorded = run_tool(
                "diff_corpus.py", "record", "--cmd", command, "--corpus", corpus
            )
            self.assertEqual(recorded.returncode, 0, recorded.stderr)
            expected = json.loads(
                (corpus / "setup.expected.json").read_text(encoding="utf-8")
            )
            self.assertEqual(expected["body"]["path"], "/health")

            verified = run_tool(
                "diff_corpus.py", "verify", "--cmd", command, "--corpus", corpus
            )
            self.assertEqual(verified.returncode, 0, verified.stderr)

            responder.write_text(
                "import json,sys\n"
                "json.load(sys.stdin)\n"
                "json.dump({'status':500,'body':{}},sys.stdout)\n",
                encoding="utf-8",
            )
            mismatch = run_tool(
                "diff_corpus.py", "verify", "--cmd", command, "--corpus", corpus
            )
            self.assertNotEqual(mismatch.returncode, 0)

    def test_api_corpus_runner_preserves_success_and_http_error_contract(self):
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == "/failure":
                    status, payload = 401, {"detail": "Invalid credentials"}
                else:
                    status, payload = 200, {"ok": True}
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Language", "zh-CN")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base_url = f"http://127.0.0.1:{server.server_port}"
            for path, status, expected_body in (
                ("/success", 200, {"ok": True}),
                ("/failure", 401, {"detail": "Invalid credentials"}),
            ):
                result = subprocess.run(
                    [
                        sys.executable,
                        str(TOOLS / "api_corpus_runner.py"),
                        "--base-url",
                        base_url,
                    ],
                    cwd=ROOT,
                    input=json.dumps({"method": "GET", "path": path}),
                    text=True,
                    capture_output=True,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                response = json.loads(result.stdout)
                self.assertEqual(response["status"], status)
                self.assertEqual(response["content_language"], "zh-CN")
                self.assertEqual(response["body"], expected_body)
        finally:
            server.shutdown()
            thread.join()
            server.server_close()

    def test_difference_linter_rejects_missing_required_fields(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / "differences.jsonl").write_text(
                '{"id":"D-001","behavior":"different"}\n', encoding="utf-8"
            )
            result = run_tool("diff_register.py", "lint", "--dir", directory)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("cause", result.stderr.lower())
            self.assertIn("decision", result.stderr.lower())

    def test_ledger_initializer_creates_standard_files(self):
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / "porting"
            result = run_tool(
                "init_porting_ledger.py",
                "--dest",
                destination,
                "--name",
                "HoneyMOM",
                "--from",
                "Python",
                "--to",
                ".NET",
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue((destination / "00-reference.md").is_file())
            self.assertTrue((destination / "01-baseline.md").is_file())
            self.assertEqual(
                (destination / "differences.jsonl").read_text(encoding="utf-8"), ""
            )


if __name__ == "__main__":
    unittest.main()
