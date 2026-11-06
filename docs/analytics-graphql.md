# GraphQL read layer, dashboard and reports

All application reads go through GraphQL; commands, authentication and file downloads stay REST. The CSV export is the only report endpoint outside GraphQL.

## Endpoint and authorization

`POST /api/v1/tenants/{tenantId}/graphql`: read-only, no mutations.

- Authentication is the same JWT bearer as REST, and the same `TenantMember` policy applies: the active membership of `{tenantId}` is validated in the database on every request, so a client-supplied tenant is never trusted.
- Resolvers delegate to the Application services that the REST endpoints use (`TicketService`, `CustomerService`, `MembershipService`, `AnalyticsService`, `TicketTaskService`). Role visibility is enforced once in those services: `TicketAccess.Visible` for tickets, staff-only team directory, manager-only customers and reports.
- Results are flat, fully authorized read models with no entity graph. There are no nested resolvers, so no N+1 queries and no DataLoader.
- Introspection and the Banana Cake Pop IDE are enabled only in Development.

## Limits

| Limit | Value |
|---|---|
| Execution depth | 12 |
| Execution timeout | 20 s |
| Cost per request (`MaxFieldCost`) | 250 |
| `page` | 1..1,000,000 (out of range returns `VALIDATION_ERROR`; REST clamps instead) |
| `pageSize` | 1..100 (`kanbanBoard.perColumn` 1..50) |

Root-field cost weights are declared in `Api/GraphQL/Queries/Query.cs`: `ticket`, `ticketSla`, `customer` 5; `tickets`, `ticketActivity`, `customers`, `teamMembers`, `ticketTaskProgress` 10; `dashboard` 30; `kanbanBoard` 40; `report` 60. A request combining dashboard, report, board and lists fits the budget; many aliased heavy fields are rejected before any resolver runs.

Pagination is `page`/`pageSize` with `totalCount`, the same contract as REST. Default ordering is deterministic and ends in a unique key.

Errors carry a stable `extensions.code`: `VALIDATION_ERROR`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `CONFLICT`, `CANCELLED`, `INTERNAL_ERROR`. Unexpected errors are masked and logged server-side only. A root field slower than 1 s logs `Slow GraphQL field {Field} took {ElapsedMs} ms (tenant …)`; query text, variables and tokens are never logged.

## Angular code generation

`frontend/graphql/schema.graphql` is the committed schema; the integration test `GraphQlSchemaTests` fails when it drifts. Refresh it, then regenerate the typed client:

```
OPSFLOW_UPDATE_GRAPHQL_SCHEMA=1 dotnet test backend/tests/OpsFlow.Tests.Integration -c Verify --filter GraphQlSchemaTests
cd frontend && npm run graphql:codegen
```

`npm run graphql:check` verifies that the generated client is current. Operations live in `frontend/src/app/core/graphql/operations/*.graphql`; `core/graphql/generated.ts` is generated and never edited by hand.

## Reporting timezone

| Concept | Source | Used for |
|---|---|---|
| Display timezone | The user's browser | Formatting single timestamps in the UI only |
| **Tenant reporting timezone** | `TenantSettings.ReportingTimeZone`, one IANA id per tenant, default `UTC` | "Resolved today", day and month buckets, report ranges, CSV file-name dates |
| SLA calendar | Persisted cycle deadlines (calendar minutes) | Never recomputed; unaffected by timezone changes |

- `TenantReportingTimeZoneProvider` reads the setting for the membership-validated tenant once per request. A missing row or an id the host cannot resolve falls back to UTC with a warning. The `timeZone` field of the dashboard and report echoes the id that was applied.
- Only a TenantOwner changes it: `PUT /api/v1/tenants/{tenantId}/settings { reportingTimeZone, name? }`. The id is validated; unknown ids are rejected with 400. The browser timezone is only ever a suggestion and no request header can change the setting.
- All instants are stored in UTC. `ReportingCalendar` converts each local-day boundary to UTC separately, so 23-, 24- and 25-hour days are exact, and ranges are split at UTC-offset changes.
- Range: `from` and `to` are inclusive local days (UTC `[start, end)`), at most 366 days, default the last 30 days. Ranges longer than 92 days use monthly buckets.

## Metric definitions

All metrics live in `AnalyticsService` and are shared by GraphQL and the CSV export.

| Metric | Definition |
|---|---|
| Open | Visible tickets that are not Resolved or Closed (current state, ignores the date range) |
| Unassigned | Open tickets with no effective assignee (`TicketAccess.EffectiveAssignments`) |
| Overdue | Open tickets with an active SLA cycle whose `ResolutionDueAtUtc` has passed |
| Resolved today | Distinct tickets with an SLA cycle resolved during the current tenant-local day |
| Average first response | Mean of `FirstResponseAtUtc − StartedAtUtc` over cycles started in range that have a recorded first response (a customer-visible staff reply only); `null` without a sample |
| SLA compliance | Completed cycles resolved in range with `ResolvedAtUtc ≤ ResolutionDueAtUtc`, divided by all completed cycles; `null` when none. Each reopen is its own cycle with its own persisted deadline |
| Resolution average and median | `ResolvedAtUtc − StartedAtUtc` of completed cycles, computed by PostgreSQL (`avg` and the exact middle rows); no row cap |
| Workload | Open assignments on open tickets per active agent; a ticket counts once per assignee, so the sum is not a distinct-ticket count |
| Reopen rate | Completed cycles in range that have a following cycle started before the range end, divided by completed cycles in range |
| Resolved by (agent) | Actor of the persisted `Resolved` timeline entry in range, not the current assignees |

Per-agent first response and per-agent SLA compliance are deliberately not provided: a first response is recorded per SLA cycle, and assignments change over time, so attributing it to current assignees would be wrong.

Visibility: Agents aggregate only the tickets they may see, Customers get only open and resolved-today counts of their own tickets, and reports and CSV exports are Owner and Manager only.

## CSV export

```
GET /api/v1/tenants/{tenantId}/reports/export
    ?kind=Tickets|Agents|Categories|Customers
    &from&to&priority&categoryId&status&agentMembershipId&customerId
```

- UTF-8 with BOM, CRLF line ends, RFC 4180 quoting. The filters match the Reports screen exactly.
- Ticket rows stream from the database reader straight into the response and are never buffered as one string. The export is capped at 10,000 rows; `X-Export-Truncated: true` says when the cap applied. Cancelling the request cancels the reader.
- Spreadsheet formula injection is neutralized: a text cell whose first meaningful character is `= + - @`, TAB, CR or a full-width `＝ ＋ － ＠` gets a leading `'`.
- Headers: tickets `ticket,title,status,priority,category,customer,created_at_utc,resolved_at_utc`; agents `agent,current_workload,resolved_in_period`; categories `category,tickets,share_percent`; customers `customer,tickets,open,resolved,open_overdue`.
- An invalid `priority` or `status` value returns a 400 validation problem.
