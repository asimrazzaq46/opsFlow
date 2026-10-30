# Background jobs, SLA monitoring, outbox and cache (Milestone 4)

## Required services
PostgreSQL (source of truth + Hangfire storage), Redis (optional: SignalR backplane and application cache), SMTP (Mailpit in dev).
`docker compose up -d` starts all three (PostgreSQL 127.0.0.1:5434, Redis 6380, Mailpit SMTP 1025 / UI 8025).
Use `127.0.0.1:6380` (not `localhost:6380`) as the Redis connection string on Windows: `localhost` failed to connect during verification.

## Database initialization (needs approval - nothing here runs automatically)
There are two independent schemas:

1. **OpsFlow EF schema** - migration `AddSlaAutomationAndTransactionalOutbox` (generated, NOT applied by any automated step). It contains:
   - new table `OutboxMessages` (unique `(TenantId, DedupKey)`; filtered indexes for the claim query, completed retention and dead letters; FK to `Tenants`);
   - `TicketSlaCycles`: new nullable columns `ResponseWarnedAtUtc`, `ResponseBreachedAtUtc`, `ResolutionWarnedAtUtc`, `ResolutionBreachedAtUtc` + indexes `IX_TicketSlaCycles_ResolutionMonitor`, `IX_TicketSlaCycles_ResponseMonitor`;
   - `NotificationType` gains `SlaWarning`, `SlaBreached` (stored as string, no DDL).
   Review it, then apply it yourself: `dotnet ef database update --project backend/src/OpsFlow.Infrastructure --startup-project backend/src/OpsFlow.Api`.
   `appsettings.Development.json` sets `Database:AutoApplyMigrationsOnStartup=false`, so starting the Development host never migrates your persistent database silently; with an unapplied migration it stops with an explicit message. To let the host apply migrations in Development, opt in per machine: `dotnet user-secrets set "Database:AutoApplyMigrationsOnStartup" "true" --project backend/src/OpsFlow.Api`.
2. **Hangfire schema** (`hangfire`, created by the Hangfire.PostgreSql provider, not by EF). Startup never creates it silently:
   - `Hangfire:PrepareSchema=false` (default): the host fails to start with an actionable message if the schema is missing or PostgreSQL is unreachable.
   - First start with a role that may `CREATE SCHEMA`: set `Hangfire:PrepareSchema=true` once, then turn it off again and use a least-privilege role.
   - `Hangfire:ConnectionString` may point to a dedicated database/role; it defaults to `ConnectionStrings:Default`.

## Running the worker
`Background:ServerEnabled` is **true in `appsettings.Development.json`** and **false everywhere else** (base `appsettings.json`, Testing, production must opt in explicitly). Without a worker nothing is dispatched: no outbox-based SignalR live events, no background e-mails, no cache-invalidation retries, no SLA monitoring. A host with `ServerEnabled=false` therefore logs a prominent warning at startup (`BACKGROUND WORKER IS DISABLED ...`); the Testing environment logs only an informational "intentionally disabled" line.

Safe one-time local setup (nothing is created automatically):
1. `docker compose up -d` (PostgreSQL, Redis, Mailpit).
2. Review and apply the EF migration yourself (see above).
3. Create the Hangfire schema once: `dotnet user-secrets set "Hangfire:PrepareSchema" "true" --project backend/src/OpsFlow.Api`, start the API once, then `dotnet user-secrets remove "Hangfire:PrepareSchema" --project backend/src/OpsFlow.Api`. Without the schema the Development host stops at startup with an actionable message (it does not run jobs unpersisted).
4. Optional cache: `dotnet user-secrets set "Cache:RedisConnectionString" "127.0.0.1:6380" --project backend/src/OpsFlow.Api` (full StackExchange.Redis strings such as `host:6379,password=...,ssl=true` work; a malformed value fails the startup clearly).
5. Verify: the log shows `Starting Hangfire Server`; with `Hangfire:DashboardEnabled=true` open `http://localhost:<port>/hangfire` (loopback only) and check the three recurring jobs below; create a ticket and watch its outbox row become `Completed` (`SELECT "Status" FROM "OutboxMessages" ORDER BY "CreatedAtUtc" DESC LIMIT 5;`).

Several instances may run at once: recurring registration is an idempotent add-or-update, outbox events are claimed with atomic conditional updates, SLA markers are set by one atomic UPDATE.

| Recurring job id | Default schedule (UTC) | What it does |
|---|---|---|
| `opsflow-outbox-dispatch` | every 10 s | claims due outbox events (batch 50, up to 10 batches per run) and delivers every pending channel |
| `opsflow-sla-scan` | every minute | records SLA warnings/breaches of active cycles and stages notifications |
| `opsflow-outbox-maintenance` | hourly | deletes completed events older than 14 days, logs pending/retry/**dead-letter** counts |

Failed runs are not auto-retried by Hangfire (the schedule re-runs them); failures show in the Hangfire "Failed" list and the logs.

## Configuration (appsettings sections)
`Background` (worker count, cron, polling), `Hangfire` (storage, dashboard), `Outbox` (batch, lease, attempts, backoff, retention, e-mailed notification types), `Sla` (`WarningPercent` 75, `BatchSize`), `Cache` (`RedisConnectionString`, TTLs: categories 600 s, SLA policies 300 s), existing `Smtp`, `Realtime`.
Secrets (connection strings with passwords) belong in user-secrets / environment variables, never in source.

## Dashboard
Off by default. `Hangfire:DashboardEnabled=true` serves it at `/hangfire` only to **loopback** requests without proxy-forwarding headers (use an SSH tunnel); outside Development it additionally needs `Hangfire:DashboardAllowInProduction=true` and is read-only there. Tenant users and JWTs are never accepted; no cookie auth is involved.

## SLA monitoring rules
- Uses the existing cycles/targets (calendar minutes, no business hours); no second SLA engine.
- First response is satisfied only by a customer-visible STAFF message (existing rule: `FirstResponseAtUtc`); notes, customer messages, system events and status changes do not count. A late reply never erases a recorded breach.
- A deadline is overdue strictly AFTER the due instant. Warning: once `WarningPercent` of the duration elapsed, only while active and unsatisfied, once per type and cycle, never after the deadline.
- Reopen = new cycle with a resolution target only (existing behavior); the first-response SLA is NOT reset (interpretation of the approved rule). Markers live on the cycle, so old cycles can never affect the new one. Finished cycles are never notified; late breaches of finished cycles are only recorded.
- Recipients: active Owners/Managers and agents assigned to / creating the ticket (`TicketAudience`, recomputed when staged). Customers are never notified about SLA.
- Downtime only delays work: the scanner recomputes from persisted instants. Each scan handles up to `Sla:BatchSize` cycles per kind and continues next minute.

## Outbox guarantees and limits
- Business change, notification rows and outbox events are committed by the SAME `SaveChanges`/transaction. Rollback = no event; commit = durable event.
- Payloads carry ids only; handlers re-read current data and re-check recipient eligibility at delivery time (removed/suspended users get nothing).
- Lifecycle: `Pending -> Claimed -> Completed`; failure -> `RetryScheduled` (exponential backoff 5 s .. 15 min, +-20 % jitter, max 8 attempts) -> `DeadLetter`; permanent errors go straight to `DeadLetter`. A crashed worker's claim is recoverable after `LeaseSeconds` (120).
- Channels (`Realtime`, `Email`, `Cache`) are tracked per event; a retry only repeats the unfinished channels.
- Lease: every claim takes its lease from the CURRENT clock (not the batch start) and every finished channel renews it, so slow earlier events cannot leave later ones with an already expired lease. One SMTP send is bounded by `Smtp:TimeoutSeconds` (30); keep it well below `Outbox:LeaseSeconds`. Two workers therefore do not deliver the same event under normal bounded processing; only a crash (or a worker stalled beyond the lease) between "SMTP accepted" and the stored acknowledgement can still cause a duplicate - SMTP stays at-least-once.
- Internal effects are idempotent through unique constraints (`Notifications` dedup key, `OutboxMessages` dedup key, SLA marker UPDATE). **SignalR and SMTP are at-least-once, not exactly-once**: a crash between sending and acknowledging can deliver twice (clients de-duplicate by notification/message id; history endpoints remain the recovery path).
- Realtime push for notifications, chat messages and internal notes now comes ONLY from the outbox (the old direct publish was removed). Without a running worker there is no live push (history/catch-up endpoints still work).
- Not moved to the outbox: invitation / e-mail-confirmation / password-reset e-mails. They carry one-time secrets that must not be stored in durable job data, so they stay synchronous and best effort as before.
- Email: assigned / SLA warning / SLA breach notifications are e-mailed (subject = ticket number + title only, no message or note content).

## Redis cache
Cache-aside for tenant categories and SLA policies (non-sensitive, tenant-wide; the role check happens before the cache is read). Key `opsflow:cache:v1:{tenantId}:{resource}:g{generation}`; invalidation increments the generation (no stale write-back), is done right after commit and backed by a `cache.invalidated` outbox event. Empty `Cache:RedisConnectionString` = no caching. Redis down = reads fall back to PostgreSQL, one warning per 30 s, business operations unaffected. The SignalR backplane is separate: with a configured backplane, live delivery between instances needs Redis (in-process otherwise).
Not cached: dashboards (no endpoint exists), ticket lists, chat, tokens, anything user-specific.

## Troubleshooting / recovery
- Dead letters: `SELECT "Id","EventType","AttemptCount","LastError" FROM "OutboxMessages" WHERE "Status"='DeadLetter';`. After fixing the cause, replay with `UPDATE "OutboxMessages" SET "Status"='Pending', "AttemptCount"=0, "NextAttemptAtUtc"=now() WHERE "Id"=...;` (completed channels are kept, so only the failed ones repeat).
- Host will not start with "Hangfire schema ... does not exist": see "Database initialization".
- SMTP down: tickets/assignments keep working; the Email channel retries with backoff, then dead-letters.
- Licensing: Hangfire.Core/AspNetCore and Hangfire.PostgreSql are free (LGPL-3.0); no Hangfire Pro. `Newtonsoft.Json` is pinned to 13.0.4 because Hangfire.Core's default 11.0.1 has a known vulnerability.
