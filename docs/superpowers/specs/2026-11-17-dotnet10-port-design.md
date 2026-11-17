# HoneyMOM Python to .NET 10 Port Design

**Status:** Proposed for written review
**Reference commit:** `cf7abbbe215677022d22d3edef3554a86d18a924`
**Reference repository:** `agentthink/Indu-mom`; upstream synchronization target: `HoneyMeta/HoneyMOM`

## Goal

Port the complete `honeymom/` server from Python to .NET 10 with observable behavior equivalent to the reference implementation. Keep the Vue frontend in `web/` unchanged. The Python `replayer/` remains a separate demo and test utility.

The migration is complete only when the .NET server can replace the Python server without frontend changes, loss of existing database data, or removal of existing backend capabilities. The port will also be maintained against future upstream changes, with behavior differences made explicit and testable.

## Scope and invariants

The port includes the server API, generic entity endpoints, authentication and permissions, audit, business domains, AI and agents, MCP, device collection, background work, imports and exports, backup and restore, CLI operations, configuration, and deployment.

The following are compatibility requirements:

- Keep existing API routes, request and response shapes, status codes, validation and error behavior, authentication semantics, SSE streams, and MCP protocol behavior.
- Keep the existing SQLite and PostgreSQL databases usable in place. Preserve their current tables, data, and Python migration history; do not recreate or silently reshape an existing database.
- Keep current `MOM_*` configuration, supported legacy aliases, data directories, and the 8900 service port.
- Do not modify files under `web/`. Continue building and serving its existing output.
- Keep `replayer/` in Python. Use a separate Python image for it rather than adding Python to the .NET server image.
- Do not run Python and .NET server instances as concurrent writers to the same production database. Python remains the production service until full .NET parity is accepted.

Unrelated product redesign, frontend changes, and feature removal are out of scope.

## Architecture

Implement a .NET 10 modular monolith hosted by ASP.NET Core. Keep one server process per database. Organize the solution into a small set of projects:

- `HoneyMOM.Api`: HTTP endpoints, middleware, static frontend hosting, and process composition.
- `HoneyMOM.Application`: use cases and domain services grouped by business area.
- `HoneyMOM.Domain`: business rules and domain types independent of HTTP and database providers.
- `HoneyMOM.Infrastructure`: persistence, protocol clients, files, backups, and external services.
- Focused test projects for unit, API-contract, integration, and differential tests.

Dependencies point inward: the API and infrastructure implement or invoke application abstractions; domain rules do not depend on ASP.NET Core, database providers, or external clients.

### Data access

- Entity Framework Core 10 is the only business-data write path.
- Dapper is the query/read path.
- Separate commands from queries so writes cannot accidentally bypass EF Core and read models can be optimized independently.
- Map EF Core explicitly to the current table and column names. Do not use conventions that rename, recreate, or otherwise reshape the existing schema.
- Preserve the metadata-driven generic entity API. Its write implementation must use EF Core while retaining current validation, field coercion, custom-field, audit, and transaction behavior.
- Keep the existing `schema_migration` data. Establish and verify a one-time EF Core baseline against the current schema without replaying or rewriting existing data. Track subsequent .NET schema changes through versioned EF Core migrations, with a preflight schema check and explicit migration command. Do not use `EnsureCreated` or silently migrate on an unreviewed startup path.

### Runtime and integration seams

Run collectors, agent execution, scheduled backups, ERP retries, and demo automation as hosted background services. Preserve their existing persistence and restart behavior. Provide injectable boundaries for the clock, device protocol clients, LLM client, file storage, and other environment-dependent services so tests can run without real devices or external credentials.

Preserve the existing authentication, permission, and audit behavior. Map expected API failures to the same response contract; log unexpected failures with enough context to diagnose them. Cancellation, timeout, and business rejection must remain distinguishable.

## Migration sequence

The work is staged for development and verification, but the deployed production writer is switched only after full acceptance.

1. **Reference and inventory:** lock the exact source commit; inventory routes, schemas, migrations, background loops, configuration, and existing tests.
2. **Behavior baseline and foundation:** capture API and database behavior; implement configuration, persistence, migration checks, clock, authentication, permissions, audit, metadata, and generic entity operations.
3. **Core business domains:** port production, quality, inventory, equipment, and their related operations.
4. **Specialized domains:** port factory modeling and collectors, implementation tools, ERP, AI, agents, and MCP.
5. **Operational capabilities:** port import/export, files, backups and restore, schedulers, CLI, and deployment behavior.
6. **Full acceptance and cutover:** test SQLite and PostgreSQL, exercise the unchanged frontend and Python replayer, and switch the server only after the complete suite passes.

Python is the production server during stages 1–5. It is also retained as the behavior oracle during migration; it is not part of the .NET production runtime image.

## Behavior baseline and verification

Treat the locked Python implementation, existing test scripts, and documented behavior as the specification. Build a differential harness that sends equivalent scenarios to Python and .NET against isolated database copies and compares:

- HTTP status, headers, JSON field presence, types, defaults, ordering where observable, and error bodies.
- Resulting persisted state for write operations.
- SSE framing and event order, MCP request/response behavior, and file/export results.
- Collector-driven outcomes using the unchanged Python replayer.

Only normalize a field when it is demonstrably nondeterministic, such as a wall-clock timestamp or generated identifier. Document each normalization and each accepted behavior difference with an identifier, reason, decision, and test. Do not normalize business-visible differences away.

Port existing backend checks into .NET tests where appropriate and retain Python tests as reference evidence during the port. Run coverage for both database providers and both supported UI languages. Run the existing frontend build and browser walkthroughs against .NET without changing frontend code.

Acceptance requires:

- .NET 10 build with zero warnings and zero errors.
- A nonzero test count, all required tests passing, and failures/skips reported explicitly.
- Differential checks passing for supported SQLite and PostgreSQL scenarios.
- Existing UI/API flows and demo replay working against the .NET server.
- Existing frontend build and browser checks passing, with no changes under `web/`.
- All known deviations recorded and classified; no unrecorded compatibility gaps.

## Deployment and upstream synchronization

The `mom` Docker service moves to a .NET 10 runtime image and continues serving the built frontend. The Python replayer gets a separate image while preserving its existing network relationship to `mom`. Keep the existing external port, data volume, environment variable names, and operational capabilities. Provide .NET CLI equivalents for administrator creation, migration status/check/upgrade, backup/restore, and audit verification.

For ongoing upstream synchronization, lock each reference revision before porting it, compare upstream changes against the behavior baseline, and add or update .NET tests for every adopted behavior change. Track accepted differences in a machine-readable ledger and make its validation part of CI. The current reference is the exact commit above; future upstream revisions must be explicitly selected rather than inferred from a moving branch.

## Risks to resolve in implementation planning

- The generic metadata-driven entity API and custom fields must remain writable through EF Core without weakening validation or audit behavior.
- Existing SQLAlchemy migrations cover database history and dialect-specific behavior. The .NET baseline and future EF Core migrations need a tested path for both SQLite and PostgreSQL.
- Device protocol, PDF/document generation, and database backup tools have runtime-specific dependencies that need .NET-compatible implementations or explicitly justified adapters.
- The repository's current `origin` is `agentthink/Indu-mom`; the upstream synchronization source is identified in project documentation as `HoneyMeta/HoneyMOM`. The implementation plan should make reference acquisition and drift checking reproducible.
