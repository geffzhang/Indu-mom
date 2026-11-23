# HoneyMOM .NET 10 Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a reproducible behavior baseline and a bootable .NET 10 foundation that can safely connect to the existing SQLite/PostgreSQL schema, use EF Core 10 for writes and Dapper for reads, and preserve the first-admin and login contracts.

**Architecture:** Add a modular .NET solution under `dotnet/` with ASP.NET Core Minimal API hosting, application/domain abstractions, and infrastructure persistence. This first plan covers only the reference tooling, host, configuration, core schema baseline, and first-admin/login vertical slice; follow-on plans cover the remaining API platform and business domains.

**Tech Stack:** .NET 10, ASP.NET Core, Entity Framework Core 10, Dapper, SQLite, PostgreSQL, xUnit, Python standard library for reference/ledger tooling.

**Spec:** [2026-11-17-dotnet10-port-design.md](../specs/2026-11-17-dotnet10-port-design.md)

## Global Constraints

- “Keep existing API routes, request and response shapes, status codes, validation and error behavior, authentication semantics, SSE streams, and MCP protocol behavior.”
- “Keep the existing SQLite and PostgreSQL databases usable in place. Preserve their current tables, data, and Python migration history; do not recreate or silently reshape an existing database.”
- “Entity Framework Core 10 is the only business-data write path.”
- “Dapper is the query/read path.”
- “Do not modify files under `web/`. Continue building and serving its existing output.”
- “Keep `replayer/` in Python. Use a separate Python image for it rather than adding Python to the .NET server image.”
- “Do not run Python and .NET server instances as concurrent writers to the same production database.”
- Target framework is `net10.0`; builds must have zero warnings and zero errors.

## Review Focus

- Environment precedence: an empty `MOM_*` value falls back to its legacy name, but a non-empty `MOM_*` value wins — test `MomOptionsResolverTests.EmptyCurrentNameFallsBackToLegacy` and `CurrentNameWinsOverLegacy`.
- Existing database safety: schema baseline/check does not rewrite user data or Python migration rows — test `SchemaBaselineTests.BaselinePreservesExistingRowsAndPythonHistory`.
- Provider-specific schema mapping: SQLite and PostgreSQL both preserve the existing table/column names and JSON values — test `PersistenceTests.MappedCoreRowsRoundTripOnBothProviders`.
- Password/token interoperability: Python PBKDF2 hashes and HMAC tokens remain verifiable, including malformed and expired tokens — test `AuthenticationCompatibilityTests.AcceptsPythonHashAndTokenFixtures`.
- First-admin race and validation: only one active administrator can be created, with the original 8-character minimum and username trimming — test `SetupAdminTests.ConcurrentFirstAdminRequestsCreateAtMostOneAdministrator`.

---

## File Structure

- `tools/porting/`: repository-local reference-lock, corpus, and difference-ledger commands, based on the supplied cross-language porting guide.
- `porting/`: exact reference record, baseline checklist, machine-readable differences, and initial differential cases.
- `dotnet/HoneyMOM.sln` and `dotnet/Directory.Build.props`: solution and shared .NET 10 compiler/test policy.
- `dotnet/src/HoneyMOM.Domain/`: user and role domain types, independent of persistence and HTTP.
- `dotnet/src/HoneyMOM.Application/`: clock, read-store, and use-case abstractions.
- `dotnet/src/HoneyMOM.Infrastructure/`: options, connection resolution, EF Core context/entities, Dapper read store, schema baseline, password/token services.
- `dotnet/src/HoneyMOM.Api/`: ASP.NET Core host, health route, setup-admin/login endpoints, localization and error mapping for this slice.
- `dotnet/tests/HoneyMOM.Infrastructure.Tests/`: configuration, persistence, migration-baseline, and cryptographic compatibility tests.
- `dotnet/tests/HoneyMOM.Api.Tests/`: in-process HTTP contract tests for the first-admin/login routes.
- `tests/porting/`: Python reference-database fixture, API corpus runner, and tests for the repository-local porting tools.

## Task 1: Lock the Reference and Add Porting Ledgers

**Files:**
- Create: `tools/porting/reference_lock.py`
- Create: `tools/porting/diff_corpus.py`
- Create: `tools/porting/diff_register.py`
- Create: `tools/porting/init_porting_ledger.py`
- Create: `tests/porting/test_porting_tools.py`
- Create: `tests/porting/api_corpus_runner.py`
- Create: `porting/00-reference.md`
- Create: `porting/01-baseline.md`
- Create: `porting/differences.jsonl`
- Create: `porting/reference-lock.json`
- Create: `porting/corpus/setup-admin.input.json`
- Create: `porting/corpus/login-invalid.input.json`

**Interfaces:**
- `reference_lock.py lock --repo <path> --dest <path> --note <text> --label <stable identity>` records the exact Git commit; `check` exits nonzero when it differs.
- The reference path is a detached checkout of the Python source commit, not the active port branch. Store a stable logical repository label in the lock, never an absolute machine path.
- `diff_corpus.py record|verify --cmd <command> --corpus <path>` records or verifies JSON request/response cases.
- `diff_register.py new|lint|render` validates difference entries with ID, behavior, cause, decision, and test.
- `init_porting_ledger.py --dest <path> --name <name> --from <language> --to <language>` creates the standard ledger files.
- `api_corpus_runner.py --base-url <url>` reads one `{ method, path, body? }` request from stdin and writes stable JSON containing status, `Content-Language`, and response body.
- `create_reference_db.py --reference <detached-worktree> --data-dir <path> --mode production` creates and seeds an isolated SQLite database using the locked Python source.

- [ ] **Step 1: Write standard-library tests for the porting commands**

Add `unittest` cases for reference-lock round trip and mismatch, stable labels when the same reference is checked out at different absolute paths, corpus record/verify and mismatch, API runner JSON output for success and HTTP error responses, and difference-ledger rejection when any required field is missing.

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `python -m unittest discover -s tests/porting -v`
Expected: FAIL because the command modules do not exist yet.

- [ ] **Step 3: Implement the four porting commands and API runner, then initialize the ledger**

Use the CLI contracts above. Create a detached worktree outside the port branch with `git worktree add --detach ..\honeymom-python-reference cf7abbbe215677022d22d3edef3554a86d18a924`; initialize and lock/check that worktree, never `.`:

```powershell
python tools/porting/init_porting_ledger.py --dest porting --name HoneyMOM --from Python --to .NET
python tools/porting/reference_lock.py lock --repo ..\honeymom-python-reference --dest porting/reference-lock.json --note "Initial port baseline" --label HoneyMeta/HoneyMOM
```

The initial reference must be `cf7abbbe215677022d22d3edef3554a86d18a924`; `00-reference.md` must name `HoneyMeta/HoneyMOM` as the ongoing synchronization target. Record that initial parity cases are setup-admin state and invalid login; leave `differences.jsonl` empty until a measured difference exists.

- [ ] **Step 4: Run the tests and verify the checked-in reference**

Run:

```powershell
python -m unittest discover -s tests/porting -v
python tools/porting/reference_lock.py check --repo ..\honeymom-python-reference --dest porting/reference-lock.json
python tools/porting/diff_register.py lint --dir porting
```

Expected: all unit tests pass; the reference check reports the exact locked commit; difference lint accepts the empty ledger.

- [ ] **Step 5: Commit the reference tooling**

```powershell
git add tools/porting tests/porting porting
git commit -m "test: establish cross-language port baseline"
```

## Task 2: Create the .NET 10 Solution and Bootable Host

**Files:**
- Create: `dotnet/Directory.Build.props`
- Create: `dotnet/HoneyMOM.sln`
- Create: `dotnet/src/HoneyMOM.Domain/HoneyMOM.Domain.csproj`
- Create: `dotnet/src/HoneyMOM.Application/HoneyMOM.Application.csproj`
- Create: `dotnet/src/HoneyMOM.Infrastructure/HoneyMOM.Infrastructure.csproj`
- Create: `dotnet/src/HoneyMOM.Api/HoneyMOM.Api.csproj`
- Create: `dotnet/src/HoneyMOM.Api/Program.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/HoneyMOM.Infrastructure.Tests.csproj`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/HoneyMOM.Api.Tests.csproj`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/HealthEndpointTests.cs`

**Interfaces:**
- `Program` composes `Api`, `Application`, and `Infrastructure`; dependency direction remains inward as specified.
- `GET /health` is an operational liveness route returning HTTP 200 and `{"status":"ok"}`; it does not replace any existing `/api` route.

- [ ] **Step 1: Add a failing host test**

Add `HealthEndpointTests.GetHealth_ReturnsOk` using `WebApplicationFactory<Program>` and assert status 200 and exact JSON body `{"status":"ok"}`.

- [ ] **Step 2: Run the focused test and confirm it fails**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~HealthEndpointTests`
Expected: FAIL because the solution and endpoint are not implemented.

- [ ] **Step 3: Create the solution and minimum host**

Set `TargetFramework` to `net10.0`, nullable reference types on, and warnings as errors in `Directory.Build.props`. Add the four production projects and both test projects with references only in the permitted direction, add `Microsoft.AspNetCore.Mvc.Testing` to the API test project, and map the liveness route.

- [ ] **Step 4: Run the focused test and full solution test**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release`
Expected: build has zero warnings/errors; at least one test executes and passes.

- [ ] **Step 5: Commit the solution skeleton**

```powershell
git add dotnet
git commit -m "build: scaffold .NET 10 HoneyMOM solution"
```

## Task 3: Implement Configuration and Database URL Resolution

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Configuration/MomOptions.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Configuration/MomOptionsResolver.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/DatabaseUrlParser.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Configuration/MomOptionsResolverTests.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/DatabaseUrlParserTests.cs`

**Interfaces:**
- `MomOptionsResolver.Resolve(IReadOnlyDictionary<string,string?> values, string repositoryRoot) -> MomOptions`
- `DatabaseUrlParser.Parse(string? url, string dataDirectory) -> DatabaseConnectionOptions`
- `DatabaseConnectionOptions` contains provider, normalized connection string, SQLite timeout, PostgreSQL pool size, and data directory. This slice maps `MOM_DATA`, `MOM_DB`, `MOM_SQLITE_TIMEOUT`, and `MOM_DB_POOL` plus their documented legacy aliases; feature-specific variables are added with their owning follow-on plans.

- [ ] **Step 1: Write option and URL parser tests**

Add `MomOptionsResolverTests.CurrentNameWinsOverLegacy`, `MomOptionsResolverTests.EmptyCurrentNameFallsBackToLegacy`, `MomOptionsResolverTests.DefaultsToRepositoryDataDirectory`, `DatabaseUrlParserTests.ParsesWindowsSqliteUrl`, and `DatabaseUrlParserTests.ParsesPostgresSqlalchemyUrlAndEscapesCredentials`. Assert `MOM_DATA` over `HM_DATA`, `MOM_DB` over `MES_DB`, `MOM_SQLITE_TIMEOUT` over `MES_SQLITE_TIMEOUT`, `MOM_DB_POOL` over `MES_DB_POOL`, empty current values falling back to legacy values, default `<repository>/data/mes.db`, SQLite `sqlite:///` URLs including Windows absolute paths, and URL-decoded PostgreSQL credentials in `postgresql+psycopg://`.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~MomOptionsResolverTests`
Expected: FAIL because the resolver is not implemented.

- [ ] **Step 3: Implement the options resolver and URL parser**

Preserve the Python `env.get` rule: non-empty new name first, then its legacy name, then the documented default. Do not silently accept unsupported URL schemes.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~Configuration`
Expected: all resolver and parser tests pass.

- [ ] **Step 5: Commit configuration support**

```powershell
git add dotnet
git commit -m "feat: preserve HoneyMOM environment configuration"
```

## Task 4: Add EF Core Writes and Dapper Reads for the Core Schema

**Files:**
- Create: `dotnet/src/HoneyMOM.Domain/Users/SystemUser.cs`
- Create: `dotnet/src/HoneyMOM.Domain/Users/SystemRole.cs`
- Create: `dotnet/src/HoneyMOM.Application/Abstractions/IReadStore.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/HoneyMomDbContext.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/SystemUserRow.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/SystemRoleRow.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/SettingRow.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/AuditLogRow.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/DapperReadStore.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/HoneyMOM.Infrastructure.csproj`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/PersistenceTests.cs`

**Interfaces:**
- `IReadStore.QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken) -> Task<IReadOnlyList<T>>`
- `IReadStore.QuerySingleOrDefaultAsync<T>(string sql, object? parameters, CancellationToken cancellationToken) -> Task<T?>`
- `HoneyMomDbContext` owns all business-data writes. Dapper is registered only behind `IReadStore` for query operations.
- EF mappings use exact existing table names (`sys_user`, `sys_role`, `setting`, `audit_log`) and snake_case columns; JSON mappings must preserve current SQLite and PostgreSQL values.

- [ ] **Step 1: Add persistence contract tests**

Add `PersistenceTests.MappedCoreRowsRoundTripOnBothProviders`. Assert Dapper reads an existing `sys_user`, EF writes and updates a user row, JSON scope/perms round-trip, and the same logical operation works on SQLite and PostgreSQL. PostgreSQL tests use `FC_PG`; when unset they are explicitly skipped, not reported as passing.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~PersistenceTests`
Expected: FAIL because the context, mappings, and read store are absent.

- [ ] **Step 3: Implement exact core table mappings and provider registration**

Add `Microsoft.EntityFrameworkCore.Sqlite` 10.x, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, and `Dapper` package references. Register both EF providers from `DatabaseConnectionOptions`. Keep business writes in `HoneyMomDbContext`; construct Dapper commands with cancellation-aware `CommandDefinition`.

- [ ] **Step 4: Run SQLite and PostgreSQL persistence tests**

Run SQLite and PostgreSQL separately; configure `FC_PG` locally using the same connection form as `tests/foundation_check.py` before the PostgreSQL run:

```powershell
dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~PersistenceTests
if ($env:FC_PG) { dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~PersistenceTests } else { Write-Output "SKIP PostgreSQL: FC_PG is not configured" }
```

Expected: SQLite tests pass; with `FC_PG` configured the PostgreSQL tests pass with no skips.

- [ ] **Step 5: Commit the persistence boundary**

```powershell
git add dotnet
git commit -m "feat: add EF Core writes and Dapper reads"
```

## Task 5: Verify the Existing Schema and Establish the EF Migration Baseline

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Persistence/SchemaCheckResult.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/CoreSchemaChecker.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Migrations/20261117000000_BaselinePythonSchema.cs`
- Create: `dotnet/src/HoneyMOM.Api/Commands/DatabaseCommands.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/SchemaBaselineTests.cs`
- Create: `tests/porting/create_reference_db.py`

**Interfaces:**
- `CoreSchemaChecker.CheckAsync(DbConnection connection, CancellationToken cancellationToken) -> Task<SchemaCheckResult>`
- `DatabaseCommands` exposes explicit `db check-core` and `db baseline` commands; the legacy full-schema `db check` is provided by the later operations plan.
- `db baseline` verifies mapped current-schema tables and columns before applying the no-DDL EF baseline. It never edits `schema_migration` and is never run implicitly at application startup.
- Reference the EF Core design-time package from the infrastructure project; the no-DDL baseline does not create business tables.

- [ ] **Step 1: Write schema safety tests**

Use `create_reference_db.py` to create a temporary database through the locked Python baseline. Add `SchemaBaselineTests.BaselinePreservesExistingRowsAndPythonHistory` and assert that `db check-core` reports missing core columns, that a valid Python schema passes, and that `db baseline` leaves all business row counts and `schema_migration` rows unchanged while only adding the EF migration-history table and baseline record.

- [ ] **Step 2: Run schema tests and confirm they fail**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~SchemaBaselineTests`
Expected: FAIL because the checker and baseline command are absent.

- [ ] **Step 3: Implement the core schema checker and explicit baseline command**

Compare required table and column names without creating or altering application tables. Add a no-DDL EF Core baseline migration. Apply it only after successful preflight via `db baseline`; do not call `Database.Migrate` from startup.

- [ ] **Step 4: Run schema safety tests against SQLite and PostgreSQL**

Run SQLite and PostgreSQL separately; configure `FC_PG` locally using the same connection form as `tests/foundation_check.py` before the PostgreSQL run:

```powershell
dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~SchemaBaselineTests
if ($env:FC_PG) { dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~SchemaBaselineTests } else { Write-Output "SKIP PostgreSQL: FC_PG is not configured" }
```

Expected: schema checks pass on both providers; the Python migration ledger and business row counts are unchanged.

- [ ] **Step 5: Commit schema compatibility checks**

```powershell
git add dotnet tests/porting
git commit -m "feat: baseline existing HoneyMOM database schema"
```

## Task 6: Preserve Password Hash and Token Compatibility

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Abstractions/IPasswordHasher.cs`
- Create: `dotnet/src/HoneyMOM.Application/Abstractions/ITokenService.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Security/PythonCompatiblePasswordHasher.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Security/PythonCompatibleTokenService.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Security/AuthenticationCompatibilityTests.cs`

**Interfaces:**
- `IPasswordHasher.Hash(string password) -> string`
- `IPasswordHasher.Verify(string password, string? storedHash) -> bool`
- `ITokenService.Create(string username, DateTimeOffset now, TimeSpan? lifetime = null) -> string`
- `ITokenService.ReadUsername(string token, DateTimeOffset now) -> string?`

- [ ] **Step 1: Add deterministic Python interoperability fixtures**

Test PBKDF2-HMAC-SHA256 with 120,000 iterations, UTF-8 password and 16-character lowercase-hex salt, 32-byte digest, and `pbkdf2$<salt>$<lowercase hex>` storage; malformed stored hashes verify false. Test token format `<padded-base64url-payload>.<32-lowercase-hex-signature>`, payload `{ "u": username, "exp": unix_seconds }`, HMAC-SHA256 over the encoded payload, default seven-day expiration, invalid signature, malformed body, and expired token. Interpret the 64 hexadecimal characters in `secret.key` as UTF-8 bytes, matching Python; create 32 random bytes only when the file is absent. Include fixtures generated by Python and verify both directions in `AcceptsPythonHashAndTokenFixtures`.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~AuthenticationCompatibilityTests`
Expected: FAIL because the services are absent.

- [ ] **Step 3: Implement the Python-compatible cryptographic services**

Use constant-time comparisons. Store/read the signing secret at `<MOM_DATA>/secret.key` using the existing 64-character hexadecimal representation as UTF-8 bytes; do not rotate or regenerate a present secret.

- [ ] **Step 4: Run interoperability tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter FullyQualifiedName~AuthenticationCompatibilityTests`
Expected: all Python-generated fixtures validate and .NET-generated fixtures validate in the Python reference.

- [ ] **Step 5: Commit authentication compatibility**

```powershell
git add dotnet
git commit -m "feat: preserve Python password and token formats"
```

## Task 7: Implement First-Admin and Login Routes

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Users/SetupAdminService.cs`
- Create: `dotnet/src/HoneyMOM.Application/Users/LoginService.cs`
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/SetupEndpoints.cs`
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/AuthenticationEndpoints.cs`
- Create: `dotnet/src/HoneyMOM.Api/Middleware/RequestLanguageMiddleware.cs`
- Create: `dotnet/src/HoneyMOM.Api/Middleware/ApiErrorMiddleware.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/SetupAdminTests.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/LoginTests.cs`

**Interfaces:**
- `GET /api/setup/admin -> { needed, demo, lang }`
- `POST /api/setup/admin` accepts `{ username, password, name? }`; on success returns `{ token, user: { username: string, name: string, roles: string } }`, where `roles` is the existing comma-separated string.
- `POST /api/login` accepts `{ username, password }`; success returns the same token/user shape as the Python endpoint.
- Errors use `{"detail": <localized string>}` and preserve the current HTTP status codes.
- Request language precedence is `X-Lang`, then `?lang=`, then `Accept-Language`, then the configured server default.

- [ ] **Step 1: Write route contract tests**

Add `SetupAdminTests.ConcurrentFirstAdminRequestsCreateAtMostOneAdministrator` and `LoginTests.InvalidCredentialsReturnGeneric401`. Cover setup-needed response, successful first-admin creation, trimmed username, empty name defaulting to username, minimum password length 8, username length limit 40, existing active administrator rejection, inactive same-name user activation, successful login, inactive-user and invalid-credential attempts both returning the generic 401 credential error, `Content-Language` matching the resolved request language, and audit rows written in the same EF transaction as the user operation.

Before implementing the .NET routes, create the Python reference database and record the two corpus cases:

```powershell
python tests/porting/create_reference_db.py --reference ..\honeymom-python-reference --data-dir "$env:TEMP\hm-ref" --mode production
```

In a separate terminal, set `MOM_DATA` to `$env:TEMP\hm-ref`, set `MOM_DEMO=0`, and start the reference checkout with `python -m honeymom serve --host 127.0.0.1 --http 8901`. Then run:

```powershell
python tools/porting/diff_corpus.py record --cmd "python tests/porting/api_corpus_runner.py --base-url http://127.0.0.1:8901" --corpus porting/corpus
```

Expected: two `.expected.json` files are recorded from the locked Python server.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~SetupAdminTests|FullyQualifiedName~LoginTests"`
Expected: FAIL because the routes and application services are absent.

- [ ] **Step 3: Implement setup and login use cases**

Use EF Core transactions for user and audit writes. Guard first-admin creation with a process-wide asynchronous lock before checking and inserting; the deployment invariant is one server process per database. Route tests use isolated temporary SQLite databases, separate from the external corpus server. Match the Python success payload (including string-valued `roles`), localized `detail` errors, audit action names, and request language precedence.

- [ ] **Step 4: Run route tests and verify the Python reference corpus**

Stop the Python reference server, copy its data directory to a separate .NET data directory, run the explicit EF baseline against that copy, and start the .NET host on port 8900:

```powershell
Copy-Item -Recurse "$env:TEMP\hm-ref" "$env:TEMP\hm-dotnet"
$env:MOM_DATA = "$env:TEMP\hm-dotnet"
dotnet run --project dotnet/src/HoneyMOM.Api -- db baseline
dotnet run --project dotnet/src/HoneyMOM.Api -- --urls http://127.0.0.1:8900
```

In another terminal, run:

```powershell
dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~SetupAdminTests|FullyQualifiedName~LoginTests"
python tools/porting/diff_corpus.py verify --cmd "python tests/porting/api_corpus_runner.py --base-url http://127.0.0.1:8900" --corpus porting/corpus --json-canonical
```

Expected: all route tests pass; the corpus matches Python status codes, language headers, and JSON response bodies. Any accepted difference is registered with a test before proceeding.

- [ ] **Step 5: Commit the first API slice**

```powershell
git add dotnet tests/porting porting
git commit -m "feat: add compatible setup and login endpoints"
```

## Task 8: Foundation Acceptance and Handoff

**Files:**
- Modify: `porting/01-baseline.md`
- Modify: `.github/workflows/ci.yml`
- Verify only: `web/`
- Verify only: `honeymom/`

**Interfaces:**
- CI runs Python porting-tool tests, .NET Release build/tests, and existing frontend build without changing the frontend.
- Foundation test evidence records executed and skipped counts for SQLite and PostgreSQL.

- [ ] **Step 1: Add the foundation checks to CI**

Add `actions/setup-dotnet` with `dotnet-version: 10.0.x`, a PostgreSQL service, and `FC_PG` for the .NET integration tests. Run the new Python tooling tests and `dotnet test dotnet/HoneyMOM.sln -c Release` in a dedicated CI job. Check out the locked Python reference revision into a separate path and run `reference_lock.py check` against that checkout, not the port branch. Preserve current Python checks and the existing web-build job.

- [ ] **Step 2: Run the complete foundation validation locally**

Run:

```powershell
python -m unittest discover -s tests/porting -v
dotnet build dotnet/HoneyMOM.sln -c Release
dotnet test dotnet/HoneyMOM.sln -c Release
python tests/foundation_check.py
Push-Location web; npm ci --no-audit --no-fund; npm run build; Pop-Location
git diff --exit-code -- web
```

Expected: all foundation tests pass with nonzero executed test count; build has zero warnings/errors; existing Python and frontend checks pass; no `web/` files changed. SQLite and configured PostgreSQL tests both pass; any provider unavailable locally is reported as skipped, never counted as passing.

- [ ] **Step 3: Record measured baseline and commit**

Update `porting/01-baseline.md` with the exact commands, test totals, provider coverage, and corpus status, then commit:

```powershell
git add .github/workflows/ci.yml porting/01-baseline.md
git commit -m "ci: validate .NET port foundation"
```

## Follow-on Plans Required for Full Parity

This plan does not declare the migration complete. Create separate, independently testable plans in this order:

1. Platform API parity: current user and metadata, permissions/data scope, generic entity CRUD/actions, API keys, and audit verification.
2. Core MOM domains: production, quality, inventory, equipment and maintenance, scheduling, shop-floor execution, traceability, energy, dashboards, and reports.
3. Factory integration: MQTT/Sparkplug B, OPC UA, Modbus TCP, HTTP ingestion, time-series storage, reconnect/replay, and factory/onboarding APIs.
4. Implementation and ERP: imports, custom fields, forms, workflows, reports, finance, procurement, MRP, connectors, and webhook retries.
5. AI and agents: LLM calls, assistants, AI switches, skills, approvals, automations, agent runtime, and MCP.
6. Operations and release: complete backup/restore, CLI including full-schema `db check`, static frontend hosting, deployment, full API/SSE/MCP parity, unchanged-frontend walkthroughs, upstream drift CI, and cutover.

