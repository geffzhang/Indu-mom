#!/usr/bin/env python3
"""Compare the locked Python API and .NET platform API on cloned SQLite fixtures."""

import argparse
import csv
import datetime as dt
import io
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import sqlite3
from contextlib import closing
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "tests" / "porting" / "platform_api_cases" / "platform-api.json"
CREATE_FIXTURE = ROOT / "tests" / "porting" / "create_reference_db.py"
DOTNET_API = ROOT / "dotnet" / "src" / "HoneyMOM.Api" / "bin" / "Release" / "net10.0" / "HoneyMOM.Api.dll"
IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
TEMPLATE = re.compile(r"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}")
JSON_PATH_PART = re.compile(r"[^.[\]]+|\[\*\]|\[\d+\]")
SHA256_HEX = re.compile(r"^[a-f0-9]{64}$")
BUILTIN_AI_SKILL_KEYS = (
    "shift_summary",
    "bottleneck_action",
    "oee_loss_top5",
    "quality_pareto",
    "overdue_maint",
    "energy_anomaly",
)


class DifferentialFailure(RuntimeError):
    pass


def load_cases():
    cases = json.loads(CASES.read_text(encoding="utf-8"))
    if cases.get("version") != 1:
        raise DifferentialFailure(f"unsupported corpus version in {CASES}")
    return cases


def available_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def database_url(path):
    return "sqlite:///" + path.resolve().as_posix()


def load_locked_reference(reference):
    if not reference.is_absolute() and not reference.exists():
        in_workspace = ROOT / reference
        in_sibling = ROOT.parent / reference
        reference = in_workspace if in_workspace.exists() else in_sibling
    reference = reference.resolve()
    lock = json.loads((ROOT / "porting" / "reference-lock.json").read_text(encoding="utf-8"))
    actual = subprocess.check_output(
        ["git", "-C", str(reference), "rev-parse", "HEAD"],
        text=True,
    ).strip()
    if actual != lock["commit"]:
        raise DifferentialFailure(
            f"reference checkout is {actual}, expected locked commit {lock['commit']}"
        )
    return reference


def create_fixture(reference, directory):
    subprocess.run(
        [
            sys.executable,
            str(CREATE_FIXTURE),
            "--reference",
            str(reference),
            "--data-dir",
            str(directory),
            "--mode",
            "production",
        ],
        cwd=ROOT,
        check=True,
    )
    return directory / "mes.db"


def seed_builtin_ai_skills(database):
    with closing(sqlite3.connect(database)) as connection:
        existing = {
            row[0]
            for row in connection.execute("SELECT key FROM ai_skill WHERE key IS NOT NULL")
        }
        connection.executemany(
            "INSERT INTO ai_skill (key) VALUES (?)",
            [(key,) for key in BUILTIN_AI_SKILL_KEYS if key not in existing],
        )
        connection.commit()


def server_environment(data_directory, reference=None):
    environment = os.environ.copy()
    database = data_directory / "mes.db"
    environment.update(
        {
            "MOM_DATA": str(data_directory),
            "MOM_DB": database_url(database),
            "MOM_DEMO": "0",
            "MOM_BLANK": "1",
            "MOM_LANG": "en",
            "PYTHONIOENCODING": "utf-8",
        }
    )
    if reference is not None:
        existing = environment.get("PYTHONPATH")
        environment["PYTHONPATH"] = str(reference) + (os.pathsep + existing if existing else "")
    return environment


def start_server(name, command, cwd, environment, log_path, ready_path, timeout=60):
    log = log_path.open("wb")
    try:
        process = subprocess.Popen(
            command,
            cwd=cwd,
            env=environment,
            stdout=log,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
        )
    finally:
        log.close()

    port = command_port(command)
    base_url = f"http://127.0.0.1:{port}"
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        if process.poll() is not None:
            terminate_process(process)
            raise DifferentialFailure(
                f"{name} exited with code {process.returncode} during startup:\n"
                f"{tail(log_path)}"
            )
        try:
            http_request(base_url, "GET", ready_path, {}, None, timeout=2)
            return process, base_url
        except (OSError, urllib.error.URLError, TimeoutError) as error:
            last_error = error
            time.sleep(0.25)
    terminate_process(process)
    raise DifferentialFailure(
        f"{name} did not become ready at {base_url}{ready_path}: {last_error}\n{tail(log_path)}"
    )


def command_port(command):
    for index, value in enumerate(command):
        if value == "--http" and index + 1 < len(command):
            return int(command[index + 1])
        if value == "--urls" and index + 1 < len(command):
            return int(command[index + 1].rsplit(":", 1)[1])
    raise DifferentialFailure(f"server command does not specify a listening port: {command}")


def http_request(base_url, method, path, headers, body, timeout=20):
    data = None if body is None else json.dumps(
        body,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    request_headers = {
        "Accept": "application/json, text/csv",
        "Accept-Language": "en",
        "X-Lang": "en",
        **headers,
    }
    if data is not None:
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(
        base_url + path,
        data=data,
        headers=request_headers,
        method=method,
    )
    try:
        response = urllib.request.urlopen(request, timeout=timeout)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        content_type = response.headers.get("Content-Type", "")
        result = {
            "status": response.status,
            "content_language": response.headers.get("Content-Language"),
            "content_type": content_type.split(";", 1)[0].strip().lower(),
            "body_bytes": raw,
        }
        if "json" in content_type.lower() and raw:
            result["body"] = json.loads(raw.decode("utf-8"))
        else:
            result["body"] = raw.decode("utf-8")
        return result


def json_path_parts(path):
    if not path.startswith("$"):
        raise DifferentialFailure(f"invalid JSON path {path!r}")
    return [part[1:-1] if part.startswith("[") else part
            for part in JSON_PATH_PART.findall(path)[1:]]


def path_values(value, path):
    current = [value]
    for part in json_path_parts(path):
        next_values = []
        for item in current:
            if part == "*":
                if isinstance(item, list):
                    next_values.extend(item)
            elif isinstance(item, list) and part.isdigit():
                index = int(part)
                if index < len(item):
                    next_values.append(item[index])
            elif isinstance(item, dict) and part in item:
                next_values.append(item[part])
        current = next_values
    return current


def set_path(value, path, replacement):
    parts = json_path_parts(path)

    def update(node, index):
        if index == len(parts):
            return replacement
        part = parts[index]
        if part == "*" and isinstance(node, list):
            return [update(item, index + 1) for item in node]
        if isinstance(node, list) and part.isdigit():
            copied = list(node)
            position = int(part)
            if position < len(copied):
                copied[position] = update(copied[position], index + 1)
            return copied
        if isinstance(node, dict) and part in node:
            copied = dict(node)
            copied[part] = update(copied[part], index + 1)
            return copied
        return node

    return update(value, 0)


def remove_path(value, path):
    parts = json_path_parts(path)
    if not parts:
        return None

    def remove(node, index):
        part = parts[index]
        if index == len(parts) - 1:
            if part == "*" and isinstance(node, list):
                return []
            if isinstance(node, dict):
                copied = dict(node)
                copied.pop(part, None)
                return copied
            if isinstance(node, list) and part.isdigit():
                copied = list(node)
                position = int(part)
                if position < len(copied):
                    copied.pop(position)
                return copied
            return node
        if part == "*" and isinstance(node, list):
            return [remove(item, index + 1) for item in node]
        if isinstance(node, dict) and part in node:
            copied = dict(node)
            copied[part] = remove(copied[part], index + 1)
            return copied
        if isinstance(node, list) and part.isdigit():
            copied = list(node)
            position = int(part)
            if position < len(copied):
                copied[position] = remove(copied[position], index + 1)
            return copied
        return node

    return remove(value, 0)


def resolve_path(value, path):
    values = path_values(value, path)
    if len(values) != 1:
        raise DifferentialFailure(f"{path} resolved to {len(values)} values")
    return values[0]


def parse_timestamp(value):
    if not isinstance(value, str) or not value:
        raise DifferentialFailure(f"expected a non-empty timestamp, got {value!r}")
    try:
        dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise DifferentialFailure(f"invalid timestamp {value!r}: {error}") from error


def apply_normalizations(body, normalizations):
    result = body
    for normalization in normalizations:
        kind = normalization.get("type")
        if kind not in {"timestamp", "generated_id", "time_derived_hash"}:
            raise DifferentialFailure(f"unsupported normalization type: {kind!r}")
        path = normalization["path"]
        values = path_values(result, path)
        if not values:
            raise DifferentialFailure(f"normalization path {path} did not match a value")
        if kind == "timestamp":
            for value in values:
                parse_timestamp(value)
        elif kind == "time_derived_hash":
            if any(not isinstance(value, str) or not SHA256_HEX.fullmatch(value) for value in values):
                raise DifferentialFailure(f"{path} contains a non-SHA-256 hash")
        elif any(not isinstance(value, (str, int)) or isinstance(value, bool) for value in values):
            raise DifferentialFailure(f"{path} contains a non-ID value")
        result = set_path(result, path, f"<{kind}>")
    return result


def expand(value, variables):
    if isinstance(value, dict):
        return {key: expand(item, variables) for key, item in value.items()}
    if isinstance(value, list):
        return [expand(item, variables) for item in value]
    if not isinstance(value, str):
        return value

    exact = re.fullmatch(r"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", value)
    if exact:
        name = exact.group(1)
        if name not in variables:
            raise DifferentialFailure(f"missing template variable {name!r}")
        return variables[name]

    def replace(match):
        name = match.group(1)
        if name not in variables:
            raise DifferentialFailure(f"missing template variable {name!r}")
        return str(variables[name])

    return TEMPLATE.sub(replace, value)


def request_headers(auth_name, variables):
    if not auth_name:
        return {}
    if auth_name == "api_key":
        if "api_key" not in variables:
            raise DifferentialFailure("API key request ran before an API key was captured")
        return {"X-API-Key": str(variables["api_key"])}
    if auth_name not in variables:
        raise DifferentialFailure(f"missing authentication variable {auth_name!r}")
    return {"Authorization": f"Bearer {variables[auth_name]}"}


def capture_values(response, capture, variables, label):
    if not capture:
        return
    if not isinstance(response["body"], (dict, list)):
        raise DifferentialFailure(f"{label}: cannot capture values from a non-JSON response")
    for name, path in capture.items():
        variables[name] = resolve_path(response["body"], path)


def check_assertions(response, assertions, label):
    for assertion in assertions or []:
        path = assertion["path"]
        if path.startswith("$csv."):
            csv_body = response.get("csv_body")
            if csv_body is None:
                raise DifferentialFailure(f"{label}: {path} requires a CSV response")
            value = csv_body[path[5:]]
        elif isinstance(response["body"], (dict, list)):
            value = resolve_path(response["body"], path)
        else:
            raise DifferentialFailure(f"{label}: {path} requires a JSON response")

        op = assertion["op"]
        expected = assertion.get("value")
        passed = {
            "nonempty": lambda: value is not None and value != "",
            "starts_with": lambda: isinstance(value, str) and value.startswith(expected),
            "equals": lambda: value == expected,
            "truthy": lambda: bool(value),
            "contains": lambda: expected in value,
            "has_key": lambda: isinstance(value, dict) and expected in value,
        }.get(op)
        if passed is None:
            raise DifferentialFailure(f"{label}: unsupported assertion operator {op!r}")
        if not passed():
            raise DifferentialFailure(
                f"{label}: assertion {path} {op} {expected!r} failed; actual={value!r}"
            )


def csv_body(response, normalizations):
    text = response["body"]
    had_bom = text.startswith("\ufeff")
    rows = list(csv.reader(io.StringIO(text.lstrip("\ufeff"), newline="")))
    if not rows:
        raise DifferentialFailure("CSV response has no header")
    header = rows[0]
    for normalization in normalizations or []:
        kind = normalization.get("type")
        if kind not in {"timestamp", "time_derived_hash", "json_value"}:
            raise DifferentialFailure(
                f"unsupported CSV normalization type: {kind!r}"
            )
        column = normalization["column"]
        if column not in header:
            raise DifferentialFailure(f"CSV normalization column {column!r} not found in {header!r}")
        index = header.index(column)
        for row in rows[1:]:
            if index < len(row) and row[index]:
                if kind == "timestamp":
                    parse_timestamp(row[index])
                elif kind == "time_derived_hash":
                    if not SHA256_HEX.fullmatch(row[index]):
                        raise DifferentialFailure(f"CSV column {column!r} contains a non-SHA-256 hash")
                    row[index] = f"<{kind}>"
                elif kind == "json_value":
                    try:
                        value = json.loads(row[index])
                    except json.JSONDecodeError as error:
                        raise DifferentialFailure(
                            f"CSV column {column!r} contains invalid JSON: {error}"
                        ) from error
                    row[index] = json.dumps(
                        value,
                        ensure_ascii=False,
                        sort_keys=True,
                        separators=(",", ":"),
                    )
    return {"bom": had_bom, "header": header, "rows": rows}


def comparable_response(response, request_case):
    if response["content_type"] == "text/csv":
        body = csv_body(response, request_case.get("csv_normalize"))
    else:
        body = response["body"]
        if isinstance(body, (dict, list)):
            compare_paths = request_case.get("compare_paths")
            if compare_paths:
                body = {
                    path: path_values(body, path)
                    for path in compare_paths
                }
                if any(not values for values in body.values()):
                    empty = [path for path, values in body.items() if not values]
                    raise DifferentialFailure(f"comparison paths did not match: {empty}")
            else:
                body = apply_normalizations(body, request_case.get("normalize", []))
                for credential in request_case.get("server_specific_credentials", []):
                    if not path_values(body, credential["path"]):
                        raise DifferentialFailure(
                            f"credential path {credential['path']} missing: {credential['reason']}"
                        )
                    body = remove_path(body, credential["path"])
    return {
        "status": response["status"],
        "content_language": response["content_language"],
        "content_type": response["content_type"],
        "body": body,
    }


def compare_responses(python_response, dotnet_response, request_case, label):
    python_value = comparable_response(python_response, request_case)
    dotnet_value = comparable_response(dotnet_response, request_case)
    if python_value != dotnet_value:
        raise DifferentialFailure(
            f"{label}: Python and .NET responses differ\n"
            f"Python: {json.dumps(python_value, ensure_ascii=False, indent=2, default=str)}\n"
            f".NET: {json.dumps(dotnet_value, ensure_ascii=False, indent=2, default=str)}"
        )


def send_case(base_url, request_case, variables, label):
    method = request_case["method"].upper()
    path = expand(request_case["path"], variables)
    body = expand(request_case.get("body"), variables)
    response = http_request(
        base_url,
        method,
        path,
        request_headers(request_case.get("auth"), variables),
        body,
    )
    expected_status = request_case.get("expect_status", 200)
    if response["status"] != expected_status:
        raise DifferentialFailure(
            f"{label}: expected HTTP {expected_status}, got {response['status']}: "
            f"{response['body']!r}"
        )
    if response["content_type"] == "text/csv":
        response["csv_body"] = csv_body(response, request_case.get("csv_normalize"))
    check_assertions(response, request_case.get("assertions"), label)
    capture_values(response, request_case.get("capture"), variables, label)
    return response


def run_scenario(base_urls, variables, scenario_name, request_case):
    label = f"{scenario_name}/{request_case['name']}"
    python_response = send_case(base_urls["python"], request_case, variables["python"], label)
    dotnet_response = send_case(base_urls["dotnet"], request_case, variables["dotnet"], label)
    compare_responses(python_response, dotnet_response, request_case, label)
    print(f"PASS {label}")


def quoted_identifier(name):
    if not IDENTIFIER.fullmatch(name):
        raise DifferentialFailure(f"unsafe identifier in persisted_state: {name!r}")
    return f'"{name}"'


def canonical_database_value(value):
    if isinstance(value, bytes):
        return {"bytes": value.hex()}
    if isinstance(value, str) and value[:1] in {"{", "["}:
        try:
            parsed = json.loads(value)
        except json.JSONDecodeError:
            return value
        return json.dumps(parsed, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return value


def read_persisted_state(database, specifications):
    state = {}
    with closing(sqlite3.connect(database)) as connection:
        for specification in specifications:
            table = quoted_identifier(specification["table"])
            columns = specification["columns"]
            projection = ", ".join(quoted_identifier(column) for column in columns)
            clauses = []
            parameters = []
            for column, value in specification.get("where", {}).items():
                identifier = quoted_identifier(column)
                if value is None:
                    clauses.append(f"{identifier} IS NULL")
                elif isinstance(value, list):
                    if not value:
                        clauses.append("1 = 0")
                    else:
                        clauses.append(f"{identifier} IN ({','.join('?' for _ in value)})")
                        parameters.extend(value)
                else:
                    clauses.append(f"{identifier} = ?")
                    parameters.append(value)
            where = " WHERE " + " AND ".join(clauses) if clauses else ""
            order = ", ".join(quoted_identifier(column) for column in columns[:1])
            rows = connection.execute(
                f"SELECT {projection} FROM {table}{where} ORDER BY {order}",
                parameters,
            ).fetchall()
            state[specification["name"]] = [
                [canonical_database_value(value) for value in row]
                for row in rows
            ]
    return state


def terminate_process(process):
    if process is None or process.poll() is not None:
        return
    if os.name == "nt":
        try:
            process.send_signal(signal.CTRL_BREAK_EVENT)
        except OSError:
            process.terminate()
    else:
        process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def tail(path, count=35):
    if not path.exists():
        return ""
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    return "\n".join(lines[-count:])


def run(reference):
    reference = load_locked_reference(reference)
    cases = load_cases()
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise DifferentialFailure("dotnet executable was not found on PATH")
    if not DOTNET_API.is_file():
        raise DifferentialFailure(
            f"Release API assembly not found at {DOTNET_API}; build dotnet/HoneyMOM.sln -c Release first"
        )

    failures = []
    with tempfile.TemporaryDirectory(prefix="honeymom-platform-diff-") as temp:
        root = Path(temp)
        fixture = create_fixture(reference, root / "reference-fixture")
        seed_builtin_ai_skills(fixture)
        python_data = root / "python-data"
        dotnet_data = root / "dotnet-data"
        shutil.copytree(fixture.parent, python_data)
        shutil.copytree(fixture.parent, dotnet_data)

        python_port = available_port()
        dotnet_port = available_port()
        python_log = root / "python-server.log"
        dotnet_log = root / "dotnet-server.log"
        python_command = [
            sys.executable,
            "-m",
            "honeymom",
            "serve",
            "--host",
            "127.0.0.1",
            "--http",
            str(python_port),
        ]
        dotnet_command = [
            dotnet,
            str(DOTNET_API),
            "--urls",
            f"http://127.0.0.1:{dotnet_port}",
        ]
        processes = []
        try:
            python_process, python_url = start_server(
                "Python reference",
                python_command,
                reference,
                server_environment(python_data, reference),
                python_log,
                "/api/setup/admin",
            )
            processes.append(python_process)
            dotnet_process, dotnet_url = start_server(
                ".NET API",
                dotnet_command,
                ROOT,
                server_environment(dotnet_data),
                dotnet_log,
                "/health",
            )
            processes.append(dotnet_process)
            base_urls = {"python": python_url, "dotnet": dotnet_url}
            variables = {
                server: {
                    "admin_username": cases["fixture"]["admin"]["username"],
                    "admin_name": cases["fixture"]["admin"]["name"],
                    "admin_password": cases["fixture"]["admin"]["password"],
                }
                for server in base_urls
            }

            for action in cases.get("setup_actions", []):
                label = f"setup/{action['name']}"
                responses = {}
                for server in ("python", "dotnet"):
                    responses[server] = send_case(
                        base_urls[server],
                        action,
                        variables[server],
                        label,
                    )
                compare_responses(responses["python"], responses["dotnet"], action, label)
                print(f"PASS {label}")

            for scenario in cases["scenarios"]:
                for request_case in scenario["requests"]:
                    try:
                        run_scenario(base_urls, variables, scenario["name"], request_case)
                    except DifferentialFailure as error:
                        failures.append(str(error))
                        print(f"FAIL {scenario['name']}/{request_case['name']}: {error}", file=sys.stderr)
                        break
                if failures:
                    break
        finally:
            for process in reversed(processes):
                terminate_process(process)

        if not failures:
            state = {
                "python": read_persisted_state(
                    python_data / "mes.db",
                    cases.get("persisted_state", []),
                ),
                "dotnet": read_persisted_state(
                    dotnet_data / "mes.db",
                    cases.get("persisted_state", []),
                ),
            }
            if state["python"] != state["dotnet"]:
                failures.append(
                    "persisted state differs\n"
                    f"Python: {json.dumps(state['python'], ensure_ascii=False, indent=2, default=str)}\n"
                    f".NET: {json.dumps(state['dotnet'], ensure_ascii=False, indent=2, default=str)}"
                )
            else:
                print("PASS persisted-state")

        if failures:
            print("Differential corpus failed:", file=sys.stderr)
            for failure in failures:
                print(f"- {failure}", file=sys.stderr)
            print(f"Python server log:\n{tail(python_log)}", file=sys.stderr)
            print(f".NET server log:\n{tail(dotnet_log)}", file=sys.stderr)
            return 1
    print("All platform API differential scenarios passed.")
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path)
    arguments = parser.parse_args()
    return run(arguments.reference)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (DifferentialFailure, subprocess.CalledProcessError, sqlite3.Error) as error:
        print(f"platform API differential: {error}", file=sys.stderr)
        raise SystemExit(1) from error
