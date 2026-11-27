# .NET Work-Order Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the generic work-order `release`, `close`, and `cancel` actions with Python-compatible scheduling, demo first-article and outsource side effects.

**Architecture:** Add three handlers behind the generic entity-action dispatcher and a lifecycle service that runs within the endpoint-owned EF transaction. The release service rebuilds operations, applies demo-only first-article rows, recomputes the full active schedule, creates missing outsource drafts, and calls the label service defined by the preceding queue plan.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, Dapper, SQLite, PostgreSQL, xUnit, Python differential harness.

**Spec:** `docs/superpowers/specs/2026-11-23-dotnet-production-work-order-lifecycle-design.md`

**Prerequisite plan:** Complete `docs/superpowers/plans/2026-11-23-dotnet-label-release-queue.md` first. It provides `IWorkOrderReleaseLabelService.QueueAsync(WorkOrderReleaseLabelContext, CancellationToken) -> Task<int>`.

## Global Constraints

- Keep all business-data writes on EF Core and reads on the existing metadata/Dapper query path.
- Keep the work-order changes, operation/schedule changes, demo first-article rows, outsource drafts, print-job rows, and action audit in the existing action transaction.
- The generic action endpoint continues to own the transaction, authorization, scope checks, reread/diff, and single action audit record. Handlers must not start or commit nested transactions.
- Lifecycle handlers return only action-specific fields; the generic endpoint adds the single `{"ok": true}` response wrapper.
- Do not add schema migrations or implicit database migrations; keep `web/` unchanged.
- Preserve Python behavior; Python remains the production writer until overall cutover criteria are accepted.
- Run Python and .NET only against isolated database copies during differential validation. Never run both writers against one database.
- Use an injectable clock; tests must not connect to factory devices.
- PostgreSQL lifecycle coverage must run in CI with `FC_PG` configured.

## Review Focus

- Draft release with no configured or active product route must return HTTP 400 without mutating state or audit. Pin this with `ReleaseWithoutRouteReturns400WithoutWrites` in Task 1.
- A configured route takes precedence over an active product route; route steps sort by `seq`, and an empty route remains releasable. Pin this with `ReleaseUsesConfiguredRouteAndAllowsEmptyRoute` in Task 2.
- Existing `start_plan` and `completed_at` values must remain intact while absent timestamps are filled using the business clock. Pin this with `ReleasePreservesStartPlanAndClosePreservesCompletedAt` in Tasks 2 and 1.
- Demo first-article approvals must be limited to marked demo work orders and steps requiring FAI, and existing FAI rows must not be duplicated. Pin this with `DemoOrdersOnlyApproveMissingRequiredFirstArticles` in Task 2.
- Scheduling must update every released/in-progress work order and honor shift, downtime, skills, tooling, kitting, outsourced-step, and changeover constraints. Pin this with `ReleaseRecomputesAllActiveWorkOrdersWithConstraints` in Task 3.

---

### Task 1: Lifecycle Action Handlers and State Transitions

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Production/IWorkOrderLifecycleService.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderLifecycleService.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderReleaseHandler.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderCloseHandler.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderCancelHandler.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Program.cs`
- Test: `dotnet/tests/HoneyMOM.Api.Tests/EntityEndpointTests.cs`

**Interfaces:**
- `IWorkOrderLifecycleService.ReleaseAsync(EntityActionContext action, CancellationToken cancellationToken) -> Task<IReadOnlyDictionary<string, object?>>`
- `IWorkOrderLifecycleService.CloseAsync(EntityActionContext action, CancellationToken cancellationToken) -> Task<IReadOnlyDictionary<string, object?>>`
- `IWorkOrderLifecycleService.CancelAsync(EntityActionContext action, CancellationToken cancellationToken) -> Task<IReadOnlyDictionary<string, object?>>`
- Each handler implements `IEntityActionHandler`, exposes `EntityName == "work_order"` and one `ActionName` (`release`, `close`, or `cancel`), and delegates to the matching method.
- The service uses injected `TimeProvider.GetLocalNow().DateTime` to preserve Python local business-time behavior and the endpoint-scoped `HoneyMomDbContext`; it never opens or commits a transaction.

- [ ] **Step 1: Add failing endpoint behavior tests**

Add `ReleaseWithoutRouteReturns400WithoutWrites`, `ReleaseRejectsNonDraftWithoutAudit`, `ReleaseWithConfiguredRouteSetsBaseState`, `ReleaseActionPreservesGenericPermissionAndScopePrecedence`, `CloseWorksFromAnyStatusAndPreservesCompletedAt`, `CancelRejectsInProgressAndCompleted`, `CancelAllowsOtherStatuses`, and `LifecycleActionsWriteOneAuditOnSuccess`. Assert exact HTTP status/body, persisted status/timestamps, and audit count. For permission/scope precedence, assert the release permission denial occurs before row/scope handling, and an authorized caller outside the work order's line scope is denied without writes or audit.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~EntityEndpointTests"`

Expected: lifecycle routes fail as unknown actions because no work-order handlers are registered.

- [ ] **Step 3: Implement close/cancel and release validation**

Implement `close` for any state, preserving an existing `completed_at`; implement `cancel` for all states except `in_progress` and `completed`; implement release state and route validation. Resolve a configured route first, otherwise use the product's active route; reject missing routes. On a valid draft, set the route, `released` status, `released_at`, `updated_at`, and `start_plan` only when absent. Leave operation rebuilding and later release side effects to Tasks 2–4. Use the reference messages exactly: `只有草稿状态的工单可以下达`, `这个产品还没有生效的工艺路线`, and `已开工的工单请用"关闭"`. Close/cancel handlers return no additional fields; the generic endpoint supplies the single `{ "ok": true }` wrapper.

- [ ] **Step 4: Register handlers and run focused tests**

Register all three handlers as `IEntityActionHandler` in `Program.cs`. Run:

`dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~EntityEndpointTests"`

Expected: all existing entity tests and the new action state/error/audit tests pass; release will still be incomplete until Tasks 2–4.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Production/IWorkOrderLifecycleService.cs dotnet/src/HoneyMOM.Infrastructure/Production dotnet/src/HoneyMOM.Api/Program.cs dotnet/tests/HoneyMOM.Api.Tests/EntityEndpointTests.cs
git commit -m "feat: add work-order lifecycle action handlers" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Release Route Operations and Demo First-Article Side Effect

**Files:**
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderLifecycleService.cs`
- Test: `dotnet/tests/HoneyMOM.Api.Tests/WorkOrderReleaseTests.cs`

**Interfaces:**
- `WorkOrderLifecycleService.ReleaseAsync` retains the interface from Task 1 and executes these steps in order: load work order and route, validate draft, delete existing `wo_op` rows for this work order, insert route operations ordered by `seq`, update work order, create demo FAI rows if marked, then invoke scheduling and side effects in later tasks. Keep the scoped route queries and EF operations in this service.
- Route operation writes use `DynamicEntityStore`/EF Core on the existing context and transaction. Each row uses route-step `seq`, `process`, `station_group`, work-order `qty`, `qty_good = 0`, `qty_ng = 0`; status is `ready` for the first sorted step and `waiting` for the rest.
- Demo FAI membership is read from setting `demo_fai_orders` as comma-separated work-order IDs. A row is added only when matching `route_step.fai` is true and no `first_article` exists for that `wo_op`; status is `approved`, trigger is `start`, and requested/decided-by is `演示数据`.

- [ ] **Step 1: Add failing release route and operation tests**

Add `ReleaseUsesConfiguredRouteAndAllowsEmptyRoute`, `ReleaseFallsBackToActiveProductRoute`, `ReleaseRebuildsOperationsInSequence`, `ReleasePreservesStartPlanAndSetsTimestamps`, `DemoOrdersOnlyApproveMissingRequiredFirstArticles`, and `NonDemoReleaseDoesNotAutoApproveFirstArticle`. Assert route precedence, empty-route acceptance, operation fields/order, and exact FAI row state.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderReleaseTests"`

Expected: FAIL because release does not yet rebuild operations or apply demo-only FAI behavior.

- [ ] **Step 3: Implement transactional operation rebuild and FAI behavior**

Use `route_step` ordered by `seq`; use the work-order route if present, otherwise the first active route for its product. Set `released_at` to current business time, preserve non-null `start_plan`, otherwise set it to current business time, set `updated_at`, and status `released`. An empty valid route still releases.

- [ ] **Step 4: Run focused API and release tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderReleaseTests|FullyQualifiedName~EntityEndpointTests"`

Expected: all release route, operation, demo FAI, state transition, and audit tests pass.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Infrastructure/Production dotnet/tests/HoneyMOM.Api.Tests/WorkOrderReleaseTests.cs
git commit -m "feat: rebuild operations when releasing work orders" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Finite-Capacity Scheduling Recalculation

**Files:**
- Create: `dotnet/src/HoneyMOM.Application/Production/IWorkOrderScheduler.cs`
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderSchedulingService.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderLifecycleService.cs`
- Test: `dotnet/tests/HoneyMOM.Infrastructure.Tests/Production/WorkOrderSchedulingServiceTests.cs`

**Interfaces:**
- `IWorkOrderScheduler.RecomputeAsync(CancellationToken cancellationToken) -> Task<IReadOnlyList<string>>` returns late work-order codes, matching the reference scheduler result.
- `WorkOrderSchedulingService` loads scheduling inputs with Dapper through `HoneyMomDbContext.Database.GetDbConnection()` and the active EF transaction, computes the plan without writes, then persists `wo_op.plan_start`, `plan_end`, `machines`, `conflicts`, and `setup_min` through EF Core.
- Inject `TimeProvider` and use `GetLocalNow().DateTime`, truncated to the start of the minute for schedule computation.
- Port time horizon `HORIZON_D = 45`, work-order order `(priority, due, id)`, reference shift-window behavior, and capacity/resource constraints from `honeymom/services/scheduling.py`. Release invokes `RecomputeAsync` after marking the new order released.

- [ ] **Step 1: Add failing scheduler tests**

Add `ScheduleRecomputesReleasedAndInProgressOrders`, `ScheduleUsesShiftWindowsAndDowntime`, `ScheduleRestrictsAvailabilityBySkills`, `ScheduleReservesToolingAndHonorsMaintenance`, `ScheduleDelaysForKittingAndChangeover`, `ScheduleUsesSupplierLeadTimeForOutsourcedSteps`, and `SchedulePreservesCompletedOperationPlan`. Assert plan timestamps, machine strings, setup minutes, conflicts, and that pre-existing active work orders are updated.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderSchedulingServiceTests"`

Expected: FAIL because the scheduler interface and implementation do not exist.

- [ ] **Step 3: Implement reference computation and EF persistence**

Port `shift_windows`, `merge`, `subtract`, `place`, context loading, skill filtering, tooling allocation, outsourced-step planning, changeover calculation, and ordered operation placement. Use Dapper only for input reads and EF Core for persisted schedule writes. Only update operations belonging to released/in-progress work orders; preserve done operations' schedule fields as the Python scheduler does.

- [ ] **Step 4: Integrate scheduling and run focused tests**

Invoke the scheduler from release within the existing action transaction. Run:

`dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderSchedulingServiceTests|FullyQualifiedName~WorkOrderReleaseTests"`

Expected: schedule tests pass, including changes to an already active work order, and any thrown persistence error rolls back release and schedule changes.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Application/Production/IWorkOrderScheduler.cs dotnet/src/HoneyMOM.Infrastructure/Production dotnet/tests/HoneyMOM.Infrastructure.Tests/Production/WorkOrderSchedulingServiceTests.cs
git commit -m "feat: recompute finite-capacity schedule on release" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Outsource Drafts and Release Label Integration

**Files:**
- Create: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderReleaseOutsourceService.cs`
- Modify: `dotnet/src/HoneyMOM.Infrastructure/Production/WorkOrderLifecycleService.cs`
- Modify: `dotnet/src/HoneyMOM.Api/Middleware/RequestLanguageMiddleware.cs`
- Test: `dotnet/tests/HoneyMOM.Api.Tests/WorkOrderReleaseTests.cs`

**Interfaces:**
- `WorkOrderReleaseOutsourceService.CreateMissingDraftsAsync(long workOrderId, CancellationToken cancellationToken) -> Task<int>` creates only missing drafts, keyed by work order and process, and returns created count.
- `WorkOrderLifecycleService.ReleaseAsync` calls `IWorkOrderReleaseLabelService.QueueAsync` using work-order ID/code/product/customer and `CallerPrincipal.Name ?? CallerPrincipal.Username` after scheduling and outsource creation, before the endpoint commits.
- For outsource material selection, choose the first semi-finished BOM component when an outsourced step is before the middle route sequence; otherwise use the work-order product. Default supplier is `待定供应商`; default lead time is 3 days.
- Release response `msg` is the reference string `已下达，生成 {len} 道工序并完成排产`, with `；委外工序已建委外单草稿 {len} 张` appended only when drafts were created. Preserve the generic endpoint's `ok` wrapper and action audit format; the audit action label falls back to `release`, matching the reference metadata.

- [ ] **Step 1: Add failing outsource/label/response tests**

Add `ReleaseCreatesOutsourceDraftsOncePerWorkOrderAndProcess`, `OutsourceDraftUsesSemiFinishedMaterialAndLeadTimeRules`, `ReleaseQueuesWorkOrderLabelRulesInSameTransaction`, `ReleaseMessageIncludesOnlyCreatedOutsourceDrafts`, `ReleaseMessageIsLocalizedWithAndWithoutOutsourceCount`, and `LabelJobFailureRollsBackReleaseAndAudit`. Use the queue service contract from the prerequisite plan and assert persisted job/draft state.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderReleaseTests"`

Expected: FAIL because release has no outsource/label side effects and no Python-compatible release message.

- [ ] **Step 3: Implement outsource deduplication and release integration**

Read outsourced route steps and existing drafts through Dapper; insert missing `outsource_order` rows through EF Core. Use route-step supplier/lead time and Python's sequence/material-selection rule. Invoke label generation in the caller-owned transaction with `CallerPrincipal.Name ?? CallerPrincipal.Username`. Add localized translations for `只有草稿状态的工单可以下达`, `这个产品还没有生效的工艺路线`, and `已开工的工单请用"关闭"` in `RequestLanguageMiddleware`; recognize release messages with and without the outsource suffix, preserve each numeric count, and match the exact English strings `Released, generated {len} operations and completed scheduling` and `; {len} outsourcing order drafts have been created for outsourced operations` from `honeymom/locale/en.json`.

- [ ] **Step 4: Run focused lifecycle and labeling tests**

Run: `dotnet test dotnet/HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderReleaseTests|FullyQualifiedName~EntityEndpointTests|FullyQualifiedName~WorkOrderReleaseLabelServiceTests"`

Expected: release response text, outsource count, label jobs, audit, and rollback behavior match the reference.

- [ ] **Step 5: Commit**

```sh
git add dotnet/src/HoneyMOM.Infrastructure/Production dotnet/src/HoneyMOM.Api/Middleware/RequestLanguageMiddleware.cs dotnet/tests/HoneyMOM.Api.Tests/WorkOrderReleaseTests.cs
git commit -m "feat: add release outsource and label side effects" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Differential Corpus, PostgreSQL Lifecycle Coverage, and Acceptance Checks

**Files:**
- Modify: `tests/porting/platform_api_cases/platform-api.json`
- Modify: `dotnet/tests/HoneyMOM.Api.Tests/ApiTestHost.cs`
- Create: `dotnet/tests/HoneyMOM.Api.Tests/WorkOrderLifecyclePostgreSqlTests.cs`
- Verify unchanged: `web/`

**Interfaces:**
- Differential scenarios use the existing `tests/porting/platform_api_differential.py`; do not create a second server harness.
- Add setup requests for material, route, route steps, work orders, FAI requirement, outsource step, and `wo_release` label rule. Capture IDs separately for Python and .NET using the existing per-server variable mechanism.
- Persisted-state comparison includes `work_order`, `wo_op`, `first_article`, `outsource_order`, `print_job`, and `audit_log` columns changed by the actions; normalize only generated timestamps/IDs already supported by the corpus.
- PostgreSQL tests use `FC_PG`, a unique temporary schema per test, and cleanup of only that schema. Extend `ApiTestHost` with `ApiTestHost(bool enableLabelQueueWorker = false, Action<DbContextOptionsBuilder<HoneyMomDbContext>>? configureDatabase = null)`; apply the provider callback both when initializing the database and registering the host context, with SQLite remaining the default. The PostgreSQL test passes a callback using `UseNpgsql` and disposes the host before dropping its schema. The existing `.github/workflows/ci.yml` `dotnet-foundation` job already configures `FC_PG` and runs the .NET tests; do not add a duplicate CI job.
- The differential `wo_release` label rule must omit both printer and marker IDs, producing a browser job and guaranteeing that the corpus never contacts factory devices.

- [ ] **Step 1: Add failing differential and PostgreSQL tests**

Add corpus coverage for configured/fallback route, no-route rejection, operation rebuild, demo-only FAI, close timestamp preservation, cancel rejection, global schedule updates, outsource deduplication, and release label jobs. Add `LifecycleActionsRoundTripOnPostgreSql` to assert transactional action and audit behavior against an isolated schema.

- [ ] **Step 2: Run targeted differential and PostgreSQL checks**

Run:

```powershell
dotnet build dotnet\HoneyMOM.sln -c Release
dotnet test dotnet\HoneyMOM.sln -c Release --filter "FullyQualifiedName~WorkOrderReleaseTests|FullyQualifiedName~EntityEndpointTests|FullyQualifiedName~WorkOrderLifecyclePostgreSqlTests"
python tests\porting\platform_api_differential.py --reference honeymom-python-reference
```

Expected: Python and .NET differential scenarios match; PostgreSQL tests execute when configured and otherwise report an explicit skip. Any mismatch must identify the scenario and persisted field; do not normalize business-visible state.

- [ ] **Step 3: Resolve any parity mismatch**

If Step 2 reports a mismatch, correct the lifecycle or side-effect implementation—not the reference expectation—then rerun the failing API test and differential scenario. Repeat until both the scenario response and persisted state match.

- [ ] **Step 4: Verify complete lifecycle acceptance**

Run:

```powershell
dotnet build dotnet\HoneyMOM.sln -c Release
dotnet test dotnet\HoneyMOM.sln -c Release
python -m unittest discover -s tests\porting -v
python tests\porting\platform_api_differential.py --reference honeymom-python-reference
git diff --exit-code -- web
```

Expected: Release build has zero warnings/errors; the suite executes a nonzero test count and all required tests pass with environment skips stated; the Python differential corpus passes; no frontend files change.

- [ ] **Step 5: Commit**

```sh
git add tests/porting/platform_api_cases/platform-api.json dotnet/tests/HoneyMOM.Api.Tests/ApiTestHost.cs dotnet/tests/HoneyMOM.Api.Tests/WorkOrderLifecyclePostgreSqlTests.cs
git commit -m "test: verify work-order lifecycle parity" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
