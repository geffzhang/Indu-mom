# HoneyMOM Behavior Baseline

The initial differential corpus covers setup-admin state and invalid login.
No behavior differences are accepted until measured and registered in
`differences.jsonl`.

## Foundation verification

Reference lock:

- `python tools/porting/reference_lock.py check --repo ..\honeymom-python-reference --dest porting/reference-lock.json`
  — passed at `cf7abbbe215677022d22d3edef3554a86d18a924`.

Porting tools and .NET:

- `python -m unittest discover -s tests/porting -v` — 6 passed.
- `dotnet build dotnet/HoneyMOM.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test dotnet/HoneyMOM.sln -c Release` — 15 passed, 2 skipped, 17 total.
  All SQLite tests passed; the two PostgreSQL tests were skipped locally because
  `FC_PG` was not configured. The `dotnet-foundation` CI job configures PostgreSQL
  16 and `FC_PG` to run those provider tests.
- The final review added coverage for custom SQLite database paths outside
  `MOM_DATA` and for Python-compatible setup error precedence.
- `python tests/foundation_check.py` — 109/109 passed on SQLite with the CI
  environment (`MOM_DEMO=1`, `MOM_DEMO_LANG=zh`, `E2E_LANG=zh-CN`).

Differential API corpus:

- Recorded from the locked Python server with
  `python tools/porting/diff_corpus.py record --cmd "python tools/porting/api_corpus_runner.py --base-url http://127.0.0.1:8901" --corpus porting/corpus`.
- Verified against .NET with
  `python tools/porting/diff_corpus.py verify --cmd "python tools/porting/api_corpus_runner.py --base-url http://127.0.0.1:8900" --corpus porting/corpus --json-canonical`.
- Both `login-invalid` and `setup-admin` matched in status, language header, and
  response body.

Unchanged frontend:

- `Push-Location web; npm ci --no-audit --no-fund; npm run build; Pop-Location`
  — passed.
- `git diff --exit-code -- web` — passed; no frontend files changed.

The local Python fixture dependencies were supplied through the plan-local
environment. PostgreSQL integration execution remains pending on a host with
`FC_PG`; it is not counted as a local pass.
