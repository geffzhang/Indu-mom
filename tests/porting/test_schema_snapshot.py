import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
EXPORTER = ROOT / "tools" / "porting" / "export_schema_snapshot.py"
REFERENCE = ROOT.parent / "honeymom-python-reference"


class SchemaSnapshotTests(unittest.TestCase):
    def run_exporter(self, output, *options):
        return subprocess.run(
            [
                sys.executable,
                str(EXPORTER),
                "--reference",
                str(REFERENCE),
                "--output",
                str(output),
                *options,
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_snapshot_is_stable_for_locked_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            first = Path(directory) / "first.json"
            second = Path(directory) / "second.json"

            first_result = self.run_exporter(first)
            second_result = self.run_exporter(second)

            self.assertEqual(first_result.returncode, 0, first_result.stderr)
            self.assertEqual(second_result.returncode, 0, second_result.stderr)
            self.assertEqual(first.read_bytes(), second.read_bytes())

    def test_snapshot_covers_all_generic_entities_and_columns(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "schema.json"
            result = self.run_exporter(output)
            self.assertEqual(result.returncode, 0, result.stderr)

            snapshot = json.loads(output.read_text(encoding="utf-8"))
            entity_names = {entity["name"] for entity in snapshot["entities"]}
            self.assertTrue({"plant", "work_order", "ncr"}.issubset(entity_names))
            self.assertEqual(snapshot["version"], 1)
            self.assertEqual(
                next(entity for entity in snapshot["entities"] if entity["name"] == "bom")["order"],
                "product",
            )

            plant = snapshot["tables"]["plant"]
            columns = {column["name"] for column in plant["columns"]}
            self.assertTrue({"id", "code", "name", "created_at", "updated_at"}.issubset(columns))
            self.assertEqual(plant["primary_key"], ["id"])
            self.assertTrue(any("code" in index["columns"] and index["unique"] for index in plant["indexes"]))

    def test_check_mode_detects_stale_output_without_rewriting(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "schema.json"
            output.write_text("stale\n", encoding="utf-8")

            result = self.run_exporter(output, "--check")

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("schema.json", result.stderr)
            self.assertEqual(output.read_text(encoding="utf-8"), "stale\n")


if __name__ == "__main__":
    unittest.main()
