# .NET 10 Platform API Parity Design

**Status:** Awaiting written review
**Reference commit:** `cf7abbbe215677022d22d3edef3554a86d18a924`
**Parent design:** [2026-11-17-dotnet10-port-design.md](./2026-11-17-dotnet10-port-design.md)

## Goal

Implement the platform and system API layer in the .NET 10 backend while preserving the behavior of the locked Python reference and leaving `web/` unchanged. This is the next migration phase after the .NET foundation; it is not a production cutover and does not complete the backend port.

## Scope

### Included

- Current-user information, password change, `whoami`, and API metadata endpoints.
- Permission catalog; role listing, creation, update, reset, and deletion; user role and data-scope management; organization tree.
- API key listing, creation, update, and revocation, including one-time secret disclosure, hashed storage, role/permission grants, scope, expiration, and IP allowlists.
- Audit listing, facets, CSV export, and hash-chain verification.
- Generic entity list, CSV export, detail-with-children, create, update, delete, and action dispatch:
  - Preserve pagination, search, view, sorting, filters, label attachment, field conversion, read-only field filtering, required-field checks, duplicate handling, guards, hooks, and audit behavior.
  - Implement the shared dispatcher and platform/system actions present in the locked reference.
  - Keep production, quality, equipment, ERP, and other business-domain action handlers out of scope for their later domain phases.

### Excluded

- Changes to `web/`, frontend contracts, or frontend dependencies.
- Production cutover, Python runtime integration, and concurrent Python/.NET writes to one database.
- Business-domain API/action migrations beyond the generic CRUD seam.
- Backup/restore, database administration endpoints, collectors, AI, ERP, and other separate domains.
- Rebuilding, silently migrating, or reshaping an existing database.

## Approved architecture

- The locked Python revision is the behavior oracle; no Python process or Python package is required by the .NET runtime.
- Generate and check in a versioned schema metadata snapshot from the locked Python schema. The snapshot describes the entity/table/field metadata required for generic API behavior. CI must detect drift by regenerating it from the pinned reference and comparing the result.
- At startup, map the snapshot to EF Core dynamic shared-type entities. Do not infer metadata from a live database or mutate the schema during startup.
- EF Core remains the only business-data write path. Dapper is used for read/query operations.
- Authentication, permission checks, and scope filtering are shared application services, not endpoint-specific copies. Preserve Python-compatible password/token semantics from the foundation.
- Keep the dispatcher behind a stable handler seam so later business-domain phases can register their handlers without changing the generic CRUD contract.

## Request and persistence flow

1. Resolve the caller from the existing Python-compatible session/token or API key.
2. Resolve effective permission patterns and data scope from the caller, roles, and API-key grants.
3. Validate the requested route/entity/action against the snapshot and apply the same authorization/error precedence as the reference.
4. For reads, use Dapper and apply permission, scope, query, sort, pagination, and relationship-label behavior before forming the response.
5. For writes, use EF Core; apply field conversion, read-only filtering, validation, scope checks, guards, and hooks.
6. Persist the business change and its audit entry within one database transaction. Preserve append-only audit sequencing and hash-chain behavior.
7. Return the Python-compatible response shape, status, and error body. Unexpected failures remain visible through the repository's standard error handling and logging.

Scope semantics must match the reference: permission patterns support wildcards and negation; roles combine as the Python implementation does; explicit user/API-key scope takes precedence according to reference behavior; plant/workshop/line ancestry is respected; unassigned rows remain visible where the reference permits them. Single-record reads outside scope return the reference's not-found behavior, writes return its forbidden behavior, and lists exclude out-of-scope rows.

## Compatibility requirements

- Preserve route paths, methods, authentication requirements, status codes, response field presence/types, pagination limits, CSV headers/encoding, and localized messages where observable.
- Preserve permission names and checks, including role-management, audit-view/export, and API-key-management permissions.
- Preserve API-key hash-only storage, prefix lookup, expiration, IP allowlist validation, revocation, and one-time return of the generated key.
- Preserve audit record fields, transaction coupling, export audit event, and hash-chain verification results.
- Keep existing SQLite and PostgreSQL schema/data/history usable without startup DDL or implicit migration. This phase may add only explicitly reviewed migration work if required and approved separately.
- Never treat unsupported business-domain actions as successful no-ops; they remain for later phases and must not be registered as platform handlers.

## Testing and verification

Use focused tests plus differential scenarios against the locked Python reference:

- Authentication and platform endpoints: anonymous/authenticated behavior, password change, current-user/meta/whoami response shapes, and error precedence.
- Permissions and scopes: wildcard/negated grants, role unions, API-key grants, plant/workshop/line inheritance, orphan records, list filtering, single-read not-found, and write-forbidden behavior.
- Dynamic metadata and generic CRUD: snapshot reproducibility/drift, entity lookup, query filters/sorts/pagination, child/reference labels, field coercion, read-only/required fields, uniqueness, guards/hooks, and action dispatch.
- API keys: create/secret disclosure, hashed persistence, update, invalid IP allowlist, expiry, scope, and revoke behavior.
- Audit: transaction rollback consistency, append-only sequence/hash chain, filtered query/facets, CSV export, and verification.
- Run integration coverage on SQLite and PostgreSQL; PostgreSQL coverage must run in CI rather than being silently omitted.
- Verify existing databases are not rebuilt or altered by application startup, and assert there are no changes under `web/`.
- Run the Release build with zero warnings and the test suite with a nonzero executed-test count; report any skips explicitly.

## Deliverables and acceptance

Deliver the versioned schema snapshot and drift check, dynamic EF Core mapping, platform authorization/scope services, generic CRUD and dispatcher, platform/system endpoints, audit/API-key behavior, and focused regression/differential tests.

This phase is accepted when:

1. All included routes and generic operations match the pinned Python reference for covered SQLite and PostgreSQL scenarios.
2. Existing database data, schema, and migration history remain intact; application startup performs no implicit schema change.
3. Writes and audit records are atomic, and audit verification passes after supported writes.
4. No frontend files change and no business-domain action is represented as implemented unless its handler is in the approved platform/system scope.
5. The Release build has zero warnings/errors and required tests pass with a nonzero test count; all skips and environment prerequisites are stated.

Acceptance of this phase does not authorize production cutover. The Python service remains the production writer until full migration parity and the overall cutover criteria in the parent design are accepted.
