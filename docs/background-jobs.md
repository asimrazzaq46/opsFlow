# Background jobs, SLA monitoring, outbox and cache

## Required services

PostgreSQL (source of truth and Hangfire storage), Redis (optional: SignalR backplane and application cache) and an SMTP server (Mailpit in development). From the repository root, `docker compose up -d` starts all three: PostgreSQL on `127.0.0.1:5434`, Redis on `6380`, Mailpit SMTP on `1025` and its UI on `8025`.

- Use `127.0.0.1:6380`, not `localhost:6380`, as the Redis connection string on Windows; `localhost` resolves to `::1` first and the container only publishes IPv4.
- The same applies to SMTP: `appsettings.Development.json` sets `Smtp:Host` to `127.0.0.1`.
- The Mailpit service runs with `--smtp-disable-rdns`. Without it Mailpit performs a reverse-DNS lookup of every client before greeting it, which can stall each SMTP connection for about 10 seconds in Docker Desktop.

## Database setup

There are two independent schemas, and nothing here is applied automatically.

1. **OpsFlow schema** (EF Core migrations, including the outbox tables and the SLA marker columns). Apply it yourself:

   ```
   dotnet ef database update --project backend/src/OpsFlow.Infrastructure --startup-project backend/src/OpsFlow.Api
   ```

   `appsettings.Development.json` sets `Database:AutoApplyMigrationsOnStartup=false`, so starting the Development host never migrates your database silently; with unapplied migrations it stops with an explicit message. To let the host apply migrations in Development, opt in per machine: `dotnet user-secrets set "Database:AutoApplyMigrationsOnStartup" "true" --project backend/src/OpsFlow.Api`.
2. **Hangfire schema** (`hangfire`, created by the Hangfire.PostgreSql provider, not by EF). Startup never creates it silently:
   - `Hangfire:PrepareSchema=false` (default): the host fails to start with an actionable message when the schema is missing or PostgreSQL is unreachable.
   - On the first start with a role that may `CREATE SCHEMA`, set `Hangfire:PrepareSchema=true` once, then turn it off and use a least-privilege role.
   - `Hangfire:ConnectionString` may point to a dedicated database or role; it defaults to `ConnectionStrings:Default`.

## Running the worker

`Background:ServerEnabled` is `true` in `appsettings.Development.json` and `false` everywhere else; other environments must opt in. Without a worker nothing is dispatched: no outbox-based SignalR live events, no background notification e-mails, no cache-invalidation retries and no SLA monitoring. A host with `ServerEnabled=false` logs a prominent warning at startup.

One-time local setup:

1. `docker compose up -d`.
2. Apply the EF migrations (see above).
3. Create the Hangfire schema once: `dotnet user-secrets set "Hangfire:PrepareSchema" "true" --project backend/src/OpsFlow.Api`, start the API, then `dotnet user-secrets remove "Hangfire:PrepareSchema" --project backend/src/OpsFlow.Api`.
4. Optional cache: `dotnet user-secrets set "Cache:RedisConnectionString" "127.0.0.1:6380" --project backend/src/OpsFlow.Api`. Full StackExchange.Redis strings such as `host:6379,password=...,ssl=true` work; a malformed value fails startup clearly.
5. Verify: the log shows `Starting Hangfire Server`. With `Hangfire:DashboardEnabled=true`, open `http://localhost:<port>/hangfire` (loopback only) and check the recurring jobs below. Create a ticket and watch its outbox row complete: `SELECT "Status" FROM "OutboxMessages" ORDER BY "CreatedAtUtc" DESC LIMIT 5;`.

Several instances may run at once: recurring registration is an idempotent add-or-update, outbox events are claimed with atomic conditional updates, and SLA markers are set by a single atomic UPDATE.

| Recurring job id | Default schedule | What it does |
|---|---|---|
| `opsflow-outbox-dispatch` | every 10 s | Claims due outbox events (batch 50, up to 10 batches per run) and delivers every pending channel |
| `opsflow-sla-scan` | every minute | Records SLA warnings and breaches of active cycles and stages notifications |
| `opsflow-outbox-maintenance` | hourly, at minute 15 | Deletes completed events older than 14 days and logs pending, retry and dead-letter counts |

The jobs do not use Hangfire's own retry (`AutomaticRetry(Attempts = 0)`): the schedule re-runs them, and failures appear in the Hangfire "Failed" list and the logs.

## Configuration

Appsettings sections: `Background` (worker count, cron expressions, polling, `MaxDispatchBatches`), `Hangfire` (schema, storage, dashboard), `Outbox` (batch size, lease, attempts, backoff, retention, e-mailed notification types), `Sla` (`WarningPercent` 75, `BatchSize` 200), `Cache` (`RedisConnectionString`, TTLs: categories 600 s, SLA policies 300 s), `Smtp`, `App:BaseUrl` (public frontend origin used in e-mail links) and `Realtime`. Secrets such as connection strings with passwords belong in user-secrets or environment variables, never in source.

## Dashboard

Off by default. `Hangfire:DashboardEnabled=true` serves it at `/hangfire`, only to loopback requests without proxy-forwarding headers (use an SSH tunnel for remote access). Outside Development it also needs `Hangfire:DashboardAllowInProduction=true` and is read-only. Tenant users and JWTs are never accepted and no cookie authentication is involved.

## SLA monitoring rules

- Uses the existing cycles and targets (calendar minutes, no business hours); there is no second SLA engine.
- First response is satisfied only by a customer-visible staff message (`FirstResponseAtUtc`). Notes, customer messages, system events and status changes do not count. A late reply never erases a recorded breach.
- A deadline is overdue strictly after the due instant. A warning is raised once `WarningPercent` of the duration has elapsed, only while the cycle is active and unsatisfied, once per type and cycle, and never after the deadline.
- A reopen starts a new cycle with a resolution target only; the first-response SLA is not reset. Markers live on the cycle, so old cycles never affect the new one. Finished cycles are never notified; late breaches of finished cycles are only recorded.
- Recipients are the active Owners and Managers and the agents assigned to or creating the ticket (`TicketAudience`, recomputed when staged). Customers are never notified about SLA.
- Downtime only delays work: the scanner recomputes from persisted instants and handles up to `Sla:BatchSize` cycles per kind per scan.

## Outbox guarantees

- The business change, its notification rows and its outbox events commit in the same transaction. A rollback leaves no event; a commit leaves a durable one.
- Payloads carry ids only. Handlers re-read current data and re-check recipient eligibility at delivery time, so removed or suspended users receive nothing.
- Lifecycle: `Pending → Claimed → Completed`. A failure moves to `RetryScheduled` (exponential backoff from 5 s to 15 min, ±20 % jitter, at most 8 attempts) and then `DeadLetter`; permanent errors go straight to `DeadLetter`. A crashed worker's claim becomes recoverable after `LeaseSeconds` (120).
- Channels (`Realtime`, `Email`, `Cache`) are tracked per event, and a retry repeats only the unfinished channels.
- Every claim takes its lease from the current clock and every finished channel renews it. One SMTP send is bounded by `Smtp:TimeoutSeconds` (30), which must stay well below `Outbox:LeaseSeconds`.
- Internal effects are idempotent through unique constraints (notification dedup key, outbox dedup key, the SLA marker UPDATE). SignalR and SMTP delivery are at-least-once: a crash between sending and acknowledging can deliver twice. Clients de-duplicate by notification or message id, and the history endpoints remain the recovery path.
- Realtime pushes for notifications, chat messages and internal notes come only from the outbox, so without a worker there is no live push; the catch-up endpoints still work.
- Notification e-mails (assigned, SLA warning, SLA breach) go through the outbox. The subject carries the ticket number and title only, never message or note content.

## One-time-secret e-mails

Account confirmation, password reset, invitation and invitation-resend e-mails carry a one-time secret in their link. They are never stored in the outbox or in Hangfire job data.

- `IEmailService.SendDetached` starts a best-effort delivery on a background task: the request does not wait for SMTP, a failure is logged and not retried, and an in-flight send is lost if the process stops. The request therefore takes the same time whether or not an e-mail is sent, which keeps registration, resend and password-recovery responses from revealing whether an account exists.
- Recovery is user-driven: resend the confirmation, request a new reset link, or resend the invitation after its cooldown.
- The outbox e-mail channel uses the awaited `SendAsync` so that failures are retried.

## Redis cache

Cache-aside for tenant categories and SLA policies, which are non-sensitive and tenant-wide; the role check happens before the cache is read. Keys look like `opsflow:cache:v1:{tenantId}:{resource}:g{generation}`. Invalidation increments the generation (no stale write-back), runs right after the commit and is backed by a `cache.invalidated` outbox event. An empty `Cache:RedisConnectionString` disables caching. When Redis is down, reads fall back to PostgreSQL with one warning per 30 s and business operations are unaffected.

Not cached: dashboards and reports, ticket lists, chat, tokens and anything user-specific.

The SignalR backplane is separate: with `Realtime:RedisConnectionString` set, live delivery between instances goes through Redis; otherwise delivery is in-process.

## Troubleshooting

- **Dead letters:** `SELECT "Id","EventType","AttemptCount","LastError" FROM "OutboxMessages" WHERE "Status"='DeadLetter';`. After fixing the cause, replay with `UPDATE "OutboxMessages" SET "Status"='Pending', "AttemptCount"=0, "NextAttemptAtUtc"=now() WHERE "Id"=...;`. Completed channels are kept, so only the failed ones repeat.
- **Host will not start with "Hangfire schema … does not exist":** see Database setup.
- **SMTP down:** tickets and assignments keep working. The outbox e-mail channel retries with backoff and then dead-letters; one-time-secret e-mails are logged as failed and not retried.
- **Slow SMTP in development:** each send waits about 10 s before the greeting. Check that Mailpit runs with `--smtp-disable-rdns` (`docker compose up -d mailpit` recreates it) and that `Smtp:Host` is `127.0.0.1`.
- **Licensing:** Hangfire.Core, Hangfire.AspNetCore and Hangfire.PostgreSql are free (LGPL-3.0); Hangfire Pro is not used. `Newtonsoft.Json` is pinned to 13.0.4 because the default 11.0.1 pulled in by Hangfire.Core has a known vulnerability.
