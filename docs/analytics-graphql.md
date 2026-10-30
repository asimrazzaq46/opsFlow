# GraphQL read layer, dashboard & report metrics (Milestone 5)

## Endpoint
`POST /api/v1/tenants/{tenantId}/graphql` — read-only (no mutations; commands stay REST). Same JWT Bearer + the same
`TenantMember` policy as REST: `TenantRoleHandler` validates the ACTIVE membership of `{tenantId}` in the database, so a
client-supplied tenant is never trusted. Resolvers delegate to the same Application services as the REST endpoints
(`TicketService`, `CustomerService`, `MembershipService`, `AnalyticsService`), so role visibility (`TicketAccess.Visible`,
manager-only customers/reports) is enforced in one place. Results are flat, fully authorized read models (no entity graph),
so there is no nested resolver, no N+1 and no DataLoader (none is needed: query counts are constant, see tests).
Introspection and the Banana Cake Pop IDE are Development-only.

### Limits (all enforced at runtime and covered by `AnalyticsScaleTests`)
- Execution depth 12; 20 s execution timeout; unexpected errors are masked (`INTERNAL_ERROR`) but logged server-side.
- Cost analysis: root-field weights are set in `Queries/Query.cs` (`ticket`/`ticketSla`/`customer` 5, `tickets`/`ticketActivity`/`customers`/`teamMembers` 10,
  `dashboard` 30, `kanbanBoard` 40, `report` 60); `MaxFieldCost` = 250 per request. dashboard + report + board + lists together = 130 fits;
  e.g. 10 aliased `dashboard` fields (300) or 20 aliased `report` fields (1200) are rejected before any resolver runs.
- Paging: `page` 1..1,000,000, `pageSize` 1..100 (Kanban `perColumn` 1..50). Out-of-range values return `VALIDATION_ERROR` (REST still clamps).
  Pagination is page/pageSize with `totalCount` (same contract as REST), not cursors; the default deterministic ordering ends in the unique ticket number.
- Error codes (`extensions.code`): `VALIDATION_ERROR`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `CONFLICT`, `CANCELLED`, `INTERNAL_ERROR`.
- Diagnostics: a root field slower than 1 s logs `Slow GraphQL field {Field} took {ElapsedMs} ms (tenant …)`; no query text, variables or tokens are logged.

## Angular code generation
`backend` test `GraphQlSchemaTests` guards `frontend/graphql/schema.graphql`; refresh it with
`OPSFLOW_UPDATE_GRAPHQL_SCHEMA=1 dotnet test backend/tests/OpsFlow.Tests.Integration -c Verify --filter GraphQlSchemaTests`.
Then `cd frontend && npm run graphql:codegen` (`graphql:check` verifies it is current). Operations live in
`src/app/core/graphql/operations/*.graphql`; `core/graphql/generated.ts` is generated, never edited. Changing only `@cost` weights
changes `schema.graphql` but not `generated.ts`.

## Reporting timezone (three separate concepts)
| Concept | Source of truth | Used for |
|---|---|---|
| Display timezone | the user's browser (`Intl.DateTimeFormat().resolvedOptions().timeZone`) | formatting single timestamps in the UI only |
| **Tenant reporting timezone** | `IReportingTimeZoneProvider` (one IANA id per tenant) | "resolved today", day/month buckets, report ranges, CSV file-name dates |
| SLA calendar | persisted cycle deadlines (calendar minutes today) | never recomputed; unaffected by timezone changes |

The browser timezone is never used as the tenant-wide reporting timezone and no request header can change it. Until a validated tenant setting
exists the provider answers `UTC` (`UtcReportingTimeZoneProvider`, `TimeZone` field of dashboard/report says which id was applied).
All instants stay UTC in PostgreSQL. `ReportingCalendar` converts each local-day boundary to UTC separately (23/24/25-hour days),
splits ranges at UTC-offset changes and the database groups with `utc + constant offset` inside every segment, so DST days are exact
(`ReportingCalendarTests`, `RomeTimeZoneTests`). Range: `from`/`to` inclusive local days, UTC `[start, end)`, max 366 days, default last 30 days, > 92 days → monthly buckets.

### Future onboarding contract (needs the pending migration, see below)
- Persist `TenantSettings.ReportingTimeZone` (IANA id, default `UTC`). The signed-in TenantOwner sends the browser suggestion once during onboarding
  (`PUT /tenants/{id}/settings { reportingTimeZone }`, Owner policy), the server validates it with `ReportingCalendar.Resolve` (unknown ids → 400) and a
  replacement `IReportingTimeZoneProvider` reads it (cached per tenant). The browser value is a *suggested default*, never applied silently.

## Metric definitions (all in `AnalyticsService`, shared by GraphQL and CSV)
| Metric | Definition |
|---|---|
| Open | visible tickets not Resolved/Closed (current state, ignores the date range) |
| Unassigned | open tickets with no *effective* assignee (`TicketAccess.EffectiveAssignments`) |
| Overdue | open tickets with an active SLA cycle whose `ResolutionDueAtUtc` < now |
| Resolved today | distinct tickets with an SLA cycle resolved during the current tenant-local day |
| Avg first response | mean of `FirstResponseAtUtc − StartedAtUtc` over cycles started in range that have a recorded first response (customer-visible staff reply only; see chat store); `null` with no sample |
| SLA compliance | completed cycles (resolved in range) with `ResolvedAtUtc ≤ ResolutionDueAtUtc` ÷ all completed cycles; `null` when none. Each reopen is its own cycle with its own persisted deadline |
| Resolution avg / median | `ResolvedAtUtc − StartedAtUtc` of completed cycles, **computed by PostgreSQL** (`avg`, and the exact middle row(s) via `ORDER BY … OFFSET/LIMIT`); no row cap |
| Workload | open assignments on OPEN tickets per active agent; one ticket counts once per assignee (the sum is not a distinct-ticket count) |
| Reopen rate | completed cycles in range that have a following cycle (`CycleNumber+1`) started before range end ÷ completed cycles in range |
| Resolved by (agent) | actor of the persisted `Resolved` timeline entry in range (not the current assignees) |

Not provided because no trustworthy historical attribution exists: per-agent first-response and per-agent SLA compliance. A first response
is recorded per SLA cycle (not per agent) and assignments change over time; inferring ownership from current assignees would be wrong.

Agents aggregate only tickets they may see; Customers get only open/resolved-today counts of their own tickets; reports and CSV exports are Owner/Manager only.

## CSV export
`GET /api/v1/tenants/{tenantId}/reports/export?kind=Tickets|Agents|Categories|Customers&from&to&priority&categoryId&status&agentMembershipId&customerId`
UTF-8 with BOM, CRLF, RFC 4180 quoting. Ticket rows are streamed from the database reader straight into the response (never buffered as one string);
the cap is 10,000 rows and `X-Export-Truncated: true` says when it applied. Text cells whose first meaningful character (after leading whitespace/control
characters) is `= + - @ TAB CR` or a full-width `＝ ＋ － ＠` get a leading `'`. A cancelled request cancels the reader.
Headers: tickets `ticket,title,status,priority,category,customer,created_at_utc,resolved_at_utc`; agents `agent,current_workload,resolved_in_period`;
categories `category,tickets,share_percent`; customers `customer,tickets,open,resolved,open_overdue`.

## Pending migration (not generated, awaiting approval): `AddTenantTimeZoneAndAnalyticsSupport`
Why: the tenant reporting timezone has nowhere to be stored today, and the what-if run (100,000 tickets, isolated Testcontainers database) showed one clear index win.
Proposed model changes (nothing is applied; the model in the repository is unchanged, so the Development migration guard stays green):
1. `TenantSettings.ReportingTimeZone` — `varchar(64) NOT NULL DEFAULT 'UTC'` (existing tenants keep UTC; no backfill is invented).
2. `IX_Tickets_TenantId_UpdatedAtUtc_Number` on `Tickets (TenantId, UpdatedAtUtc DESC, Number)` — the default ticket list and every Kanban column sort by it.
   Measured: ticket-page statements 45.7 ms → 8.2 ms, Kanban statements 63.1 ms → 34.0 ms at 100k tickets (Seq Scan → Index Scan).

Evaluated and NOT proposed: `TicketSlaCycles (TenantId, ResolvedAtUtc) WHERE ResolvedAtUtc IS NOT NULL` (used, but only ~15 ms saved per statement; dashboard
p50 512 → 449 ms is within run noise) and `TicketActivities (TenantId, Type, OccurredAtUtc)` (never among the slow statements). Every extra index slows
ticket/cycle writes, so they wait for evidence.

Command for the developer to run after approval (from the repository root):
`dotnet ef migrations add AddTenantTimeZoneAndAnalyticsSupport --project backend/src/OpsFlow.Infrastructure --startup-project backend/src/OpsFlow.Api`

## Backend readiness audit (REST + GraphQL) for the Stitch screens
| Area | Backend status |
|---|---|
| Auth/onboarding | register, confirm-email, resend, login, refresh, logout, logout-all, sessions list/revoke, forgot/reset, change password, deactivate, `GET /auth/me`, create/list organizations — complete |
| Tickets | list/detail (REST + GraphQL), create, **update** (title/description/priority/category with `version`), assignments add/remove/history, status transitions, reopen, activity, SLA history, chat, notes, attachments — complete |
| Customers | list/detail/create/update/activate (REST + GraphQL), tickets of a customer via `tickets(filter:{customerId})`, per-customer stats via report `customers` — complete |
| Team/invitations | members list, role change, status change, invite, list pending, revoke, accept (expiry enforced server-side) — complete; **no resend endpoint** |
| Dashboard/reports | KPIs, trends, workload, filters, CSV — complete |
| Account/organization | **gaps (later milestone/product decision):** update profile display name, update organization settings (name/ticket prefix/reporting timezone — blocked on the migration above), invitation resend |
| Categories/SLA/notifications | list/create/rename/activate (categories), list/create/update/activate (SLA policies), list/unread/read/read-all (notifications) — complete |
