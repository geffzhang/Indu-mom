# .NET 10 Platform API Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the approved platform/system APIs and generic entity CRUD/action seam to .NET 10 with locked-Python behavior parity.

**Architecture:** Generate a versioned schema snapshot from the pinned Python reference and use it to configure EF Core shared-type entities for generic writes and Dapper queries. Add shared caller, permission, scope, API-key, and audit services, then expose the Python-compatible platform and entity routes through ASP.NET Core endpoint modules.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, Dapper, SQLite, PostgreSQL, xUnit, Python `unittest` porting tools.

**Spec:** `docs/superpowers/specs/2026-11-17-dotnet-platform-api-design.md`

## Global Constraints

- The locked Python revision is the behavior oracle; no Python process or Python package is required by the .NET runtime.
- EF Core remains the only business-data write path. Dapper is used for read/query operations.
- At startup, map the snapshot to EF Core dynamic shared-type entities. Do not infer metadata from a live database or mutate the schema during startup.
- Keep existing SQLite and PostgreSQL schema/data/history usable without startup DDL or implicit migration.
- Keep `web/` unchanged.
- Single-record reads outside scope return the reference's not-found behavior, writes return its forbidden behavior, and lists exclude out-of-scope rows.
- The Python service remains the production writer until full migration parity and the overall cutover criteria in the parent design are accepted.

## Review Focus

- Unknown entity, field, filter, or sort names must be rejected/ignored exactly as the reference dictates and must never become interpolated SQL identifiers. Pin this in Task 5's query-validation tests.
- Missing, null, empty, read-only, and malformed values must preserve reference conversion and validation precedence. Pin this in Task 5's create/update tests.
- Incomplete hierarchy and unassigned plant/workshop/line references must not broaden a caller's scope. Pin this with `OrphanAndUnassignedRowsFollowReferenceScope` in Task 3's scope tests.
- Expired, revoked, malformed, or IP-disallowed API keys must retain the distinct reference status behavior and stale-cache bound. Pin this with `MalformedKeyReturns401` and `CredentialCacheExpiresWithinTenSeconds` in Task 7's authentication tests.
- Concurrent audit writes, rollback, and tampering must not produce a broken or falsely verified hash chain. Pin this with `ConcurrentAppendsRemainVerifiable` in Task 4's transaction/concurrency tests.

---

### Task 1: Versioned Schema Snapshot and Drift Check

**Files:**
- Create: `tools/porting/export_schema_snapshot.py`
- Create: `tests/porting/test_schema_snapshot.py`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/schema-v1.json`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- CLI: `python tools/porting/export_schema_snapshot.py --reference <locked-checkout> --output <snapshot> [--check]`
- Snapshot contains `version`, the ordered `ENTITIES` descriptors from `honeymom.schema.spec_json()`, and physical table/column/key/index metadata from `honeymom.schema.metadata.tables`. Serialize UTF-8 JSON with stable key ordering and a final newline.
- `--check` exits nonzero and names the differing snapshot when generated content is stale; it does not rewrite files.

- [ ] **Step 1: Write failing Python tests**

Add `test_snapshot_is_stable_for_locked_reference`, `test_snapshot_covers_all_generic_entities_and_columns`, and `test_check_mode_detects_stale_output`. Assert all reference entity names are represented, physical column names/types and keys are present, repeated generation is byte-identical, and stale output fails without being rewritten.

- [ ] **Step 2: Run the tests and verify the missing exporter fails**

Run: `python -m unittest discover -s tests/porting -p "test_schema_snapshot.py" -v`
Expected: FAIL because the exporter and snapshot do not exist.

- [ ] **Step 3: Implement the exporter and commit the snapshot**

Load only the checkout whose commit matches `porting/reference-lock.json`; derive metadata from `spec_json()` and SQLAlchemy `metadata.tables`, not from a live database. Add a CI check after reference checkout verification:

```sh
python tools/porting/export_schema_snapshot.py --reference honeymom-python-reference --output dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/schema-v1.json --check
```

- [ ] **Step 4: Run snapshot and existing porting tests**

Run: `python -m unittest discover -s tests/porting -v`
Expected: all porting tests pass; the exporter `--check` command exits 0 for the committed snapshot.

- [ ] **Step 5: Commit**

```sh
git add tools/porting/export_schema_snapshot.py tests/porting/test_schema_snapshot.py dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/schema-v1.json .github/workflows/ci.yml
git commit -m "feat: add versioned Python schema snapshot" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Dynamic EF Core Model and Entity Store

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Metadata/EntitySchema.cs`
- Create: `dotnet/src/HoneyMOM.Application/Metadata/IEntitySchemaCatalog.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/SchemaSnapshotCatalog.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/DynamicEntityModelBuilder.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/DynamicEntityStore.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Persistence/HoneyMomDbContext.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Persistence/PersistenceServiceCollectionExtensions.cs`
- Modify: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/`

**Interfaces:**
- `EntitySchema` exposes entity name, module, internal flag, default order, actions, child links, field definitions, and physical table metadata.
- `IEntitySchemaCatalog.Get(string entityName)` returns the matching descriptor or `null`; `IEntitySchemaCatalog.Entities` returns the snapshot entities in reference order.
- `DynamicEntityStore` reads/writes generic row dictionaries through EF Core; its public methods take `string entityName`, row `IDictionary<string, object?>`, and `CancellationToken`.
- Keep `SystemUserRow`, `SystemRoleRow`, `SettingRow`, and `AuditLogRow` mapped by their existing typed configurations; do not map the same table a second time as a shared type.
- Generic CRUD for those typed core tables must route through typed EF adapters exposed by `DynamicEntityStore`; all other snapshot entities use EF shared-type dictionaries.

- [ ] **Step 1: Add failing catalog and model tests**

Add `SnapshotCatalogLoadsEveryEntity`, `DynamicModelMapsEveryGenericTable`, and `DynamicStoreWritesAndReadsSharedTypeRows`. Assert unknown entity lookup returns `null`, known fields map to their snapshot column names/types, existing typed core tables have one EF entity mapping each, and a `plant` row can be inserted/read through EF on SQLite.

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~SnapshotCatalog|FullyQualifiedName~DynamicModel|FullyQualifiedName~DynamicStore"`
Expected: FAIL because the catalog and dynamic mapping are not implemented.

- [ ] **Step 3: Implement snapshot loading, EF model mapping, and the store**

Load the embedded snapshot as a versioned resource and fail startup on unsupported versions or invalid descriptors. Map generic entities as named `Dictionary<string, object?>` shared types using only snapshot identifiers; use the existing typed mappings for the four core tables. Register one singleton catalog and a scoped EF store.

- [ ] **Step 4: Verify provider and legacy-schema behavior**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~Dynamic|FullyQualifiedName~SchemaBaseline"`
Expected: SQLite dynamic mapping/write tests pass; existing schema baseline tests still preserve Python migration history and business row counts; PostgreSQL tests execute in CI and are explicitly skipped locally if `FC_PG` is absent.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Metadata dotnet/src/HoneyMOM.Infrastructure/Persistence dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence
git commit -m "feat: map snapshot entities with EF Core" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Caller, Permission, and Data-Scope Services

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Security/CallerPrincipal.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/CallerCredentials.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/PermissionEvaluator.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/ScopeEvaluator.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/ICallerResolver.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Security/EfCallerResolver.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Security/PermissionEvaluatorTests.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Security/ScopeEvaluatorTests.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`

**Interfaces:**
- `CallerPrincipal` contains username/name, role codes, effective permission-pattern groups, optional explicit scope, API-key identity, and active state.
- `CallerCredentials` contains the API-key header, authorization header, `token` query value, `mes_token` cookie value, and remote IP; resolve API-key headers first, then bearer/API-key authorization, then bearer token, query token, and cookie as in the reference.
- `ICallerResolver.ResolveAsync(CallerCredentials credentials, CancellationToken cancellationToken)` returns a resolved principal or a typed failure result for unauthenticated/inactive/forbidden credentials.
- `PermissionEvaluator.Allows(CallerPrincipal caller, string permission)` implements Python `fnmatch` wildcard semantics, role union, and `!` exclusion.
- `ScopeEvaluator.ResolveEffectiveScope(CallerPrincipal caller)` returns `null` for unrestricted scope; API keys with no explicit scope remain unrestricted rather than inheriting role scopes. User scope overrides role scopes; otherwise union role scopes only when every assigned role has a scope.

- [ ] **Step 1: Write failing permission and scope tests**

Add `WildcardPatternsSupportPositiveAndNegativeRules`, `RolePermissionsAreUnioned`, `ExplicitUserScopeOverridesRoleScopes`, `RoleScopesUnionOnlyWhenAllRolesAreScoped`, `ApiKeyWithoutExplicitScopeDoesNotInheritRoleScope`, and `OrphanAndUnassignedRowsFollowReferenceScope`. Include empty scope, negative-ID normalization, downward plant/workshop/line expansion, and upward visibility of a line's workshop and plant.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~PermissionEvaluatorTests|FullyQualifiedName~ScopeEvaluatorTests"`
Expected: FAIL because the permission/scope services do not exist.

- [ ] **Step 3: Implement caller identity, permission matching, and scope resolution**

Resolve existing signed session tokens through `ITokenService`, reload the active user and roles from persistence, and preserve the distinction between absent credentials, invalid credentials, and inactive users. Keep API-key credential validation behind the resolver seam implemented in Task 7.

- [ ] **Step 4: Verify caller and authorization tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~Security"`
Expected: all existing authentication compatibility tests and new caller/permission/scope tests pass.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Security dotnet/src/HoneyMOM.Infrastructure/Security dotnet/src/HoneyMOM.Api/Program.cs dotnet/tests/HoneyMOM.Infrastructure.Tests/Security
git commit -m "feat: add platform caller and scope policies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Transactional Append-Only Audit Service

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Auditing/AuditEntry.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/AuditService.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/AuditServiceTests.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/AuditLogRow.cs`

**Interfaces:**
- `AuditEntry` carries action, entity/entity ID, diff, detail, target, user, source, and client IP.
- `AuditService.AppendAsync(HoneyMomDbContext context, AuditEntry entry, CancellationToken cancellationToken)` appends through the caller's EF context so it participates in the same transaction as the business write.
- `AuditService.SealAsync(HoneyMomDbContext context, CancellationToken cancellationToken)` assigns sequence/previous hash/hash in ID order.
- `AuditService.VerifyAsync(HoneyMomDbContext context, CancellationToken cancellationToken)` returns `{ ok, checked, unsealed, problem }` with Python-compatible field names and values.

- [ ] **Step 1: Add failing audit tests**

Add `AppendUsesPythonCanonicalHash`, `SealSequencesRowsById`, `RollbackRemovesBusinessAndAuditWrites`, `ConcurrentSealsProduceContiguousSequence`, `ConcurrentAppendsRemainVerifiable`, `VerifyDetectsTamperedPayload`, and `SensitiveDiffValuesAreRedacted`. Use fixed timestamp/input vectors from `honeymom/audit.py`.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~AuditServiceTests"`
Expected: FAIL because the audit service does not exist.

- [ ] **Step 3: Implement append, sealing, verification, and redaction**

Use the reference canonical field order and JSON encoding; compute each hash as SHA-256 of `(previousHash or "") + canonicalJson`, with `seq` included in the canonical object. Keep audit mutations in EF Core and use the same context/transaction as the business write.

- [ ] **Step 4: Verify audit tests on available providers**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~AuditServiceTests"`
Expected: all SQLite audit tests pass; equivalent PostgreSQL transaction/sequencing tests run in CI.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Auditing dotnet/src/HoneyMOM.Infrastructure/Persistence dotnet/tests/HoneyMOM.Infrastructure.Tests/Persistence/AuditServiceTests.cs
git commit -m "feat: add transactional audit hash chain" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Generic Entity Query, CRUD, and Platform Action Dispatcher

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Entities/EntityQueryRequest.cs`
- Create: `dotnet/src/HoneyMOM.Application/Entities/IEntityActionHandler.cs`
- Create: `dotnet/src/HoneyMOM.Application/Entities/EntityActionContext.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/EntityQueryService.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/EntityCommandService.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/EntityActionDispatcher.cs`
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/EntityEndpoints.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/EntityEndpointTests.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`

**Interfaces:**
- `EntityQueryRequest` contains entity, page, size, query, view, sort, and `f_*` filters.
- `EntityQueryResult(long Total, IReadOnlyList<IReadOnlyDictionary<string, object?>> Items)` and `EntityDetailResult(IReadOnlyDictionary<string, object?> Item, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Children)` are the query service return types.
- `EntityOperationResult(bool Succeeded, long? Id, string? ErrorCode, string? ErrorMessage)` is the command service return type; the endpoint layer maps its error code to the Python-compatible status/body.
- `EntityQueryService.ListAsync(EntityQueryRequest request, CallerPrincipal caller, CancellationToken cancellationToken)` returns total count and ordered rows; `GetAsync(string entityName, long id, CallerPrincipal caller, CancellationToken cancellationToken)` returns a row plus child collections or not-found.
- `EntityCommandService.CreateAsync(string entityName, IReadOnlyDictionary<string, JsonElement> body, CallerPrincipal caller, CancellationToken cancellationToken)` returns the generated integer ID; `UpdateAsync(string entityName, long id, IReadOnlyDictionary<string, JsonElement> body, CallerPrincipal caller, CancellationToken cancellationToken)` and `DeleteAsync(string entityName, long id, CallerPrincipal caller, CancellationToken cancellationToken)` return a typed operation result.
- `IEntityActionHandler` exposes `string EntityName`, `string ActionName`, and `Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(EntityActionContext context, CancellationToken cancellationToken)`.
- `MapEntityEndpoints(IEndpointRouteBuilder endpoints)` maps the generic `/api/e/{entity}` routes.
- Query identifiers must resolve through `IEntitySchemaCatalog`; all user values remain Dapper parameters.
- Generic write methods use `DynamicEntityStore` and the same `HoneyMomDbContext`/transaction passed to `AuditService`.
- Register only platform/system action `sys_user.reset_pw`; no production, quality, equipment, ERP, or other domain handlers.

- [ ] **Step 1: Add failing entity endpoint tests**

Add `ListAppliesFiltersAndScope`, `SortAndPaginationMatchReference`, `DetailReturnsLabelsAndChildren`, `CreateRejectsMissingRequiredField`, `NullAndEmptyValuesFollowReferenceCoercion`, `MalformedValueReturnsReferenceErrorBeforeWrite`, `UpdateIgnoresReadOnlyAndLabelFields`, `DuplicateConstraintReturnsReferenceError`, `UnknownSortCannotBecomeSql`, `OutOfScopeReadIsNotFoundAndWriteIsForbidden`, and `SystemUserResetPasswordIsDispatchedAndAudited`. Assert status, response shape, and persisted state.

- [ ] **Step 2: Run the focused endpoint tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~EntityEndpointTests"`
Expected: FAIL because generic entity routes are not mapped.

- [ ] **Step 3: Implement read/query endpoints**

Map `GET /api/e/{entity}`, `GET /api/e/{entity}.csv`, and `GET /api/e/{entity}/{rid}`. Apply permission/scope policy before returning rows; preserve reference pagination limits, sort tie-breaking, CSV UTF-8 BOM/headers, child ordering, and label attachment.

- [ ] **Step 4: Implement writes and action dispatch**

Map `POST /api/e/{entity}`, `PUT /api/e/{entity}/{rid}`, `DELETE /api/e/{entity}/{rid}`, and `POST /api/e/{entity}/{rid}/do/{act}`. Preserve internal-entity restrictions, coercion/required/unique behavior, read-only field filtering, before/after scope checks, guard/hook seams, reference error precedence, and atomic audit writes. Implement only `sys_user.reset_pw` as an action handler.

- [ ] **Step 5: Run all entity endpoint tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~EntityEndpointTests"`
Expected: all entity list/export/detail/CRUD/scope/action tests pass; no SQL identifier is constructed from unvalidated request text.

- [ ] **Step 6: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Entities dotnet/src/HoneyMOM.Infrastructure/Persistence dotnet/src/HoneyMOM.Api dotnet/tests/HoneyMOM.Api.Tests/EntityEndpointTests.cs
git commit -m "feat: add generic entity APIs and dispatcher" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Current-User, Metadata, Role, User, and Organization APIs

**Files:**
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/PlatformEndpoints.cs`
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/AdminIdentityEndpoints.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/PlatformEndpointTests.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/AdminIdentityEndpointTests.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`

**Interfaces:**
- `MapPlatformEndpoints(IEndpointRouteBuilder endpoints)` maps `/api/me`, `/api/me/password`, `/api/meta`, and `/api/whoami`.
- `MapAdminIdentityEndpoints(IEndpointRouteBuilder endpoints)` maps `/api/admin/perms/catalog`, `/api/admin/roles`, `/api/admin/users`, and `/api/admin/org` methods present in the reference.
- Endpoint responses use the existing request-language middleware and the caller/permission/scope services from Task 3.

- [ ] **Step 1: Add failing platform and identity endpoint tests**

Add `MeReturnsEffectivePermissionsScopeAndWritableModules`, `PasswordChangeChecksOldPasswordAndMinimumLength`, `MetaUsesSnapshotAndReturnsReferenceShape`, `WhoamiReturnsSessionAndApiKeyIdentity`, `RoleMutationRequiresManagePermission`, `BuiltinRoleCannotBeDeleted`, `AdminCannotRemoveOwnAdminRole`, and `OrganizationTreeIncludesOrphans`.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~PlatformEndpointTests|FullyQualifiedName~AdminIdentityEndpointTests"`
Expected: FAIL because these endpoints are not mapped.

- [ ] **Step 3: Implement current-user and metadata routes**

Return the reference fields for `/api/me`, `/api/meta`, and `/api/whoami`; password change checks the old password before enforcing the 6-character minimum and updates the password through EF Core.

- [ ] **Step 4: Implement role, user, and organization routes**

Preserve permission gates, role validation/default/reset behavior, builtin and self-admin protections, user role/scope normalization, permission catalog labels, and nested plant/workshop/line/orphan response shape.

- [ ] **Step 5: Run platform endpoint tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~PlatformEndpointTests|FullyQualifiedName~AdminIdentityEndpointTests"`
Expected: all tests pass for anonymous, authorized, and forbidden requests in both configured request languages.

- [ ] **Step 6: Commit**

```sh
git add dotnet/src/HoneyMOM.Api/Endpoints dotnet/src/HoneyMOM.Api/Program.cs dotnet/tests/HoneyMOM.Api.Tests
git commit -m "feat: add platform and role management APIs" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 7: API-Key Lifecycle and Authentication

**Files:**
- Create: `dotnet/src/HoneyMOM.Infrastructure/Persistence/Entities/ApiKeyRow.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/ApiKeyCredential.cs`
- Create: `dotnet/src/HoneyMOM.Application/Security/ApiKeyCreateRequest.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Security/ApiKeyService.cs`
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/AdminApiKeyEndpoints.cs`
- Create: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Security/ApiKeyServiceTests.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/ApiKeyEndpointTests.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Persistence/HoneyMomDbContext.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Security/EfCallerResolver.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`

**Interfaces:**
- `ApiKeyService.CreateAsync(ApiKeyCreateRequest request, string createdBy, CancellationToken cancellationToken)` returns the persisted public record and one-time plaintext key.
- `ApiKeyService.AuthenticateAsync(string key, string? remoteIp, CancellationToken cancellationToken)` returns a `CallerPrincipal` or typed invalid/expired/revoked/IP-denied failure.
- `MapAdminApiKeyEndpoints(IEndpointRouteBuilder endpoints)` maps list/create/update/revoke under `/api/admin/apikeys`.
- Map the existing `api_key` table exactly: `id`, `name`, `prefix`, `key_hash`, `roles`, `perms`, `scope`, `ip_allow`, `expires_at`, `revoked_at`, `revoked_by`, `last_used_at`, `last_used_ip`, `use_count`, `remark`, `created_by`, `created_at`; do not add a migration.
- Persist only SHA-256 key hash and lookup prefix; invalidate local cache immediately and bound cross-process stale credential state to 10 seconds.

- [ ] **Step 1: Add failing key service and endpoint tests**

Add `CreateReturnsSecretOnlyOnceAndStoresHash`, `KeyAuthenticationCombinesRoleAndExtraPermissions`, `ExplicitKeyScopeIsApplied`, `MalformedKeyReturns401`, `ExpiredAndRevokedKeysReturn401`, `DisallowedIpReturns403`, `InvalidIpListReturns400`, `RevocationInvalidatesCacheImmediately`, and `CredentialCacheExpiresWithinTenSeconds`. Assert list/update responses never contain plaintext or key hashes; inject a clock to test the cross-process cache TTL without sleeping.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~ApiKeyServiceTests|FullyQualifiedName~ApiKeyEndpointTests"`
Expected: FAIL because API-key persistence and routes are not implemented.

- [ ] **Step 3: Implement EF key storage and authentication**

Map the existing `api_key` schema without DDL, create `hmk_<prefix>_<secret>` credentials, hash with SHA-256, compare hashes in constant time, parse IP/CIDR allowlists, enforce expiry/revocation, and update use metadata without storing or returning the secret again.

- [ ] **Step 4: Implement key management endpoints and caller integration**

Enforce `system.apikey.manage`; preserve roles/perms/scope/expiration/IP updates and one-time create response. Resolve `X-API-Key`, `Authorization: Bearer hmk_…`, and `Authorization: ApiKey hmk_…` using the reference precedence.

- [ ] **Step 5: Run focused API-key tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~ApiKey"`
Expected: all service and endpoint tests pass; key hashes/plaintext do not appear in list/detail responses.

- [ ] **Step 6: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Security dotnet/src/HoneyMOM.Infrastructure/Persistence dotnet/src/HoneyMOM.Infrastructure/Security dotnet/src/HoneyMOM.Api dotnet/tests/HoneyMOM.Infrastructure.Tests/Security/ApiKeyServiceTests.cs dotnet/tests/HoneyMOM.Api.Tests/ApiKeyEndpointTests.cs
git commit -m "feat: add API key management and authentication" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 8: Audit Query, Export, and Verification APIs

**Files:**
- Create: `dotnet/src/HoneyMOM.Api/Endpoints/AdminAuditEndpoints.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/AdminAuditEndpointTests.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Persistence/AuditService.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`

**Interfaces:**
- `MapAdminAuditEndpoints(IEndpointRouteBuilder endpoints)` maps `/api/admin/audit`, `/api/admin/audit/facets`, `/api/admin/audit.csv`, and `/api/admin/audit/verify`.
- Query endpoints seal pending rows before returning data; the API maps Python-compatible audit row/facet fields and CSV encoding.

- [ ] **Step 1: Add failing audit API tests**

Add `AuditListRequiresViewPermissionAndSupportsFilters`, `AuditFacetsReturnSortedDistinctValues`, `AuditExportRequiresExportPermissionAndRecordsExport`, `VerifyReturnsPythonShape`, and `CsvUsesUtf8BomAndReferenceHeaders`.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~AdminAuditEndpointTests"`
Expected: FAIL because the audit routes are not mapped.

- [ ] **Step 3: Implement audit reads, facets, export, and route authorization**

Use Dapper for read queries and EF Core for pending-row sealing/export audit writes. Preserve `system.audit.view` versus `system.audit.export`, filter names, response fields, source labels, CSV escaping/BOM, and audit verification details.

- [ ] **Step 4: Run audit endpoint tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~AdminAuditEndpointTests|FullyQualifiedName~AuditServiceTests"`
Expected: all audit API and service tests pass; exported audits are included in a valid chain.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Api/Endpoints/AdminAuditEndpoints.cs dotnet/src/HoneyMOM.Api/Program.cs dotnet/src/HoneyMOM.Infrastructure/Persistence/AuditService.cs dotnet/tests/HoneyMOM.Api.Tests/AdminAuditEndpointTests.cs
git commit -m "feat: add audit query and verification APIs" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 9: Differential Corpus and Phase Acceptance

**Files:**
- Create: `tests/porting/platform_api_differential.py`
- Create: `tests/porting/platform_api_cases/`
- Modify: `.github/workflows/ci.yml`
- Verify unchanged: `web/`

**Interfaces:**
- CLI: `python tests/porting/platform_api_differential.py --reference honeymom-python-reference`
- The harness creates two isolated copies of the same locked-reference SQLite fixture, starts Python and .NET against separate copies, executes the same ordered scenario corpus, and compares status, content language, response shape/values, and persisted state.
- Normalize only generated timestamps/IDs that are demonstrably nondeterministic; each normalization is named in the case metadata. Do not normalize business-visible behavior.

- [ ] **Step 1: Add corpus scenarios and verify the harness fails without the .NET routes**

Cover login/current-user/meta/password, role/scope management, API-key create/auth/revoke, audit list/export/verify, generic CRUD/filter/scope, and `sys_user.reset_pw`. Include setup actions separately from compared requests and use each server's own login token.

- [ ] **Step 2: Run differential scenarios and inspect mismatch output**

Run: `python tests/porting/platform_api_differential.py --reference honeymom-python-reference`
Expected before implementation: report mismatches for unimplemented routes and exit nonzero; errors identify scenario and expected/actual responses.

- [ ] **Step 3: Complete differential parity and wire CI**

Run the harness in the existing `dotnet-foundation` CI job after the pinned reference checkout and .NET build. Install the repository's Python requirements only in that test job if the Python reference server needs them; do not add Python dependencies to the .NET runtime.

- [ ] **Step 4: Run full phase verification**

Run:

```sh
python tools/porting/export_schema_snapshot.py --reference honeymom-python-reference --output dotnet/src/HoneyMOM.Infrastructure/Persistence/Metadata/schema-v1.json --check
python -m unittest discover -s tests/porting -v
python tests/porting/platform_api_differential.py --reference honeymom-python-reference
dotnet build dotnet/HoneyMOM.sln -c Release
dotnet test dotnet/HoneyMOM.sln -c Release
git diff --exit-code -- web
```

Expected: snapshot is current; all porting/differential tests pass; Release build has zero warnings/errors; tests execute a nonzero count and pass (report any local PostgreSQL skips); `web/` has no tracked changes. CI must execute PostgreSQL integration tests with `FC_PG` configured.

- [ ] **Step 5: Commit**

```sh
git add tests/porting/platform_api_differential.py tests/porting/platform_api_cases .github/workflows/ci.yml
git commit -m "test: verify platform APIs against Python reference" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
