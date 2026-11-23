# .NET Production Work-Order Lifecycle Design

**Status:** Proposed for written review
**Reference commit:** `cf7abbbe215677022d22d3edef3554a86d18a924`
**Parent design:** [2026-11-17-dotnet10-port-design.md](./2026-11-17-dotnet10-port-design.md)
**Parent phase:** Core MOM domains

## Goal

Port the Python reference's generic work-order lifecycle actions (`release`, `close`, and `cancel`) to the .NET 10 backend. Preserve the observable behavior of those actions, including the production scheduling, outsource-order, demo first-article, and label-print side effects triggered by release.

This is an independently testable production-domain slice. It does not complete the Core MOM domains phase or authorize production cutover.

## Scope

### Included

- Register `work_order.release`, `work_order.close`, and `work_order.cancel` handlers with the existing generic entity-action dispatcher.
- Preserve the generic action endpoint's route, permission and scope checks, response localization, transaction, and action audit behavior.
- Port the release pipeline:
  - Require a draft work order.
  - Use its configured route, or select the product's active route when absent; reject release when no route is available.
  - Replace any existing work-order operations using route steps ordered by `seq`. Set each operation's planned quantity from the work order, initialize good/scrap quantities, and mark the first step ready and later steps waiting.
  - Set route, released status, release time, and start plan, preserving a preconfigured start plan.
  - Update the work order's `updated_at` using the business clock, matching the reference's shared update path.
  - Recompute and persist the schedule for all released and in-progress work orders, including operation start/end, machine assignment, setup, and conflicts. Preserve the reference constraints for shifts, downtime, skills, tooling, kitting, outsourced steps, and changeovers.
  - Apply demo first-article approval only to work orders identified by the existing demo marker, not to newly created production orders.
  - Create missing outsource-order drafts for outsourced route steps and apply the work-order-release label rules.
- Port the label queue worker needed to process release-triggered jobs: render supported printer/marker payloads, send over configured TCP connections, record job/device outcomes, retry at the reference delays, and recover jobs left in `sending` after restart.
- Preserve close and cancel state transitions and localized responses as specified below.
- Keep all business-data writes on EF Core and reads on the existing metadata/Dapper query path. Keep the work-order changes, operation/schedule changes, demo first-article rows, outsource drafts, print-job rows, and action audit in the existing action transaction. Device I/O runs asynchronously after commit.

### Excluded

- Changes under `web/`, frontend contracts, or frontend dependencies.
- Production cutover, Python runtime integration, schema changes, or implicit database migrations.
- Manual/mobile/terminal reporting and its production gates.
- Standalone production board, Gantt, task list, and schedule APIs.
- Label template/rule/printer/marker management, manual print, reprint, and other label-management endpoints.
- Quality, inventory, equipment/maintenance, shop-floor, traceability, energy, dashboards, and reports beyond data read or side effects required by these lifecycle actions.
- Running Python and .NET writers or print workers concurrently against one database.

The .NET label worker consumes the existing `print_job` contract needed by release-triggered tasks. It is not a replacement for the excluded label-management APIs.

## Approved architecture

- Extend the existing `IEntityActionHandler` seam; keep `/api/e/{entity}/{id}/do/{action}` as the only lifecycle action route.
- Add an application-level work-order lifecycle service and a scheduling service. Keep HTTP concerns in the API layer and EF/Dapper details in infrastructure.
- The generic action endpoint continues to own the transaction, authorization, scope checks, reread/diff, and single action audit record. Handlers must not start or commit nested transactions.
- The scheduling computation may read its inputs through Dapper, but every persisted business change, including dynamic entities, uses EF Core.
- Register a hosted label queue worker in the .NET process. It claims queued work, performs network I/O outside database transactions, and persists state transitions through EF Core.
- Run the Python reference and .NET port only against isolated database copies during differential validation. The .NET worker must not be enabled against the production database until Python has stopped writing to it.
- Use an injectable clock and printer/marker transport so tests are deterministic and never contact factory hardware.

## Behavior contract

### Release

- Only `draft` work orders can be released. Other statuses return the reference's HTTP 400 business error without changing the work order, operations, schedule, or audit chain.
- Resolve the route from the work order first; if absent, select the active route for its product. Missing route returns HTTP 400. A route with no steps remains releasable, matching the reference.
- Rebuild operations in route-step sequence. The first is `ready`; remaining operations are `waiting`.
- Update the work order to `released`, set `released_at`, and set `start_plan` only if it was absent.
- Update `updated_at` using the business clock.
- Apply scheduling to the full set of released/in-progress work orders, not only the new order.
- For demo-marked work orders, create approved first-article rows only for operations that require them and do not already have one.
- Create an outsource draft only when one does not already exist for the same work order and process. Apply the reference material-selection and lead-time rules.
- Resolve enabled `wo_release` label rules and create the corresponding `print_job` records in the action transaction.
- Return the localized `ok`/`msg` response, including the count of generated route steps and any outsource drafts.

### Close

- Set status to `closed` for any current status.
- Preserve `completed_at` when already present; otherwise set it to the current business time.
- Update `updated_at` using the business clock.
- Return `{ "ok": true }` and write the normal single action audit record.

### Cancel

- Reject `in_progress` and `completed` work orders with the reference's HTTP 400 message.
- Set other statuses to `cancelled`, return `{ "ok": true }`, and write the normal single action audit record.
- Update `updated_at` using the business clock.

### Errors, transactions, and device I/O

- Unknown action, missing row, permission denial, and scope denial retain the generic endpoint's existing status and error precedence.
- Any release-side database or queue-generation failure rolls back the release and its audit entry.
- Printer/marker transmission is asynchronous and begins only after the release transaction commits. A device failure does not undo a released work order; it updates the job/device status and is retried using the reference delays of 5, 20, and 60 seconds before terminal failure.
- A process restart re-queues jobs left in `sending`. Worker tests use loopback fake devices; no physical printer or marker is contacted by tests.
- Delivery is at-least-once: a process crash after a device accepts a job but before its completion state is committed can cause a resend after restart, matching the reference queue's recovery behavior.

## Testing and verification

- Handler and service tests cover each state transition, route selection and rejection, operation order/status/quantities, schedule constraints and multi-order updates, demo-only first-article behavior, outsource deduplication, and label-rule matching.
- API tests cover permission/scope/error precedence, localized response shape, one audit event on success, and no partial writes or audit entries after a failed action.
- Differential scenarios run the pinned Python reference and .NET against separate copies of the same SQLite fixture. Compare action responses and persisted work-order, operation, schedule, first-article, outsource, print-job, and audit state. Normalize only explicitly identified generated IDs/timestamps.
- Worker tests use a local fake printer and marker to exercise payload encoding, browser-job completion, success acknowledgments, timeout/error handling, retry delays, terminal failure, and restart recovery.
- Run the .NET Release build and complete test suite with a nonzero test count. Run PostgreSQL lifecycle coverage in CI using the existing `FC_PG` mechanism.
- Verify the schema snapshot remains current, existing schema/migration history is unchanged, and `git diff --exit-code -- web` succeeds.

## Acceptance

This slice is accepted when:

1. The three generic lifecycle actions match the locked Python reference for covered SQLite and PostgreSQL scenarios.
2. `release` preserves all specified transactional side effects, including schedule updates to existing active work orders.
3. Print/marker jobs follow the reference queue and retry contract, with no network I/O inside the action transaction.
4. Failed actions leave no partial business writes or success audit record; successful actions write exactly one action audit record.
5. No schema or frontend changes are made, and the .NET worker is never run concurrently with the Python writer against one database.
6. Release build and required tests pass with a nonzero test count; all environment skips are reported.
