# Google Sign-In, profile, organization settings and invitation resend

## Google Sign-In (Google Identity Services, ID-token popup flow)

Google only proves the external identity. OpsFlow keeps account state, memberships, roles, JWTs, sessions and refresh-token rotation; a Google sign-in returns exactly the same `AuthResponse` as password login.

### Configuration

| Setting | Where | Notes |
| --- | --- | --- |
| `GoogleAuth:ClientId` (`GoogleAuth__ClientId`) | `appsettings.Development.json` (public dev client), environment / secret store elsewhere | Public OAuth **Web** client id. Empty = Google disabled; password auth is unaffected. A malformed value stops startup. |
| `GoogleAuth:ChallengeLifetimeSeconds` | `appsettings.json` | One-time challenge lifetime, default 300. |
| `Cache:RedisConnectionString` | user-secrets / environment | When set, challenges live in Redis (shared, atomic `GETDEL`, fails closed with 503). Without it an in-process store is used: fine for one instance; **multi-instance deployments must configure Redis**. |

No client secret and no API key are used. The previously exposed client secret must not be configured anywhere.

The Angular app needs no Google configuration: the challenge endpoint returns the client id, and the button is hidden when the API answers `AUTH_GOOGLE_DISABLED`.

### Google Cloud console

- OAuth client type: Web application.
- Authorized JavaScript origins: `http://localhost:4200` (Angular dev server default) and `http://localhost:4300` (the `App:BaseUrl` used in email links), plus every production HTTPS origin. No redirect URI is needed (popup + callback).
- While the OAuth consent screen is in **Testing** mode only listed test users can sign in.

### Flow

1. `POST /api/v1/auth/google/challenge` -> `{ challengeId, nonce, expiresAtUtc, clientId }`.
2. The SPA calls `google.accounts.id.initialize({ client_id, nonce, callback, auto_select: false })` and renders the official button. GIS keeps one global configuration, so a new challenge means a controlled re-initialization; only the latest registration accepts a credential, and only once.
3. The callback posts `{ credential, challengeId }` to `POST /api/v1/auth/google`.
4. The API consumes the challenge atomically, validates the ID token with `Google.Apis.Auth` (signature, issuer, audience = client id, expiry, issued-at; `azp` when present), compares the token nonce with the challenge, and requires `email_verified`.
5. Resolution: linked `sub` -> session; new `sub` with a new email -> account (no membership, no role) and session for Gmail / matching Workspace `hd`, confirmation email (202) otherwise; email already used by another account -> `409 AUTH_GOOGLE_LINK_REQUIRED` (never auto-linked).

Linking (signed in): `POST /api/v1/auth/google/link/challenge { password }` (password re-authentication, lockout applies) -> GIS -> `POST /api/v1/auth/google/link { credential, challengeId }`. Link challenges are bound to the user and cannot be used for sign-in (and vice versa). Accounts without a password must first set one with "Forgot password". Unlinking is not supported.

Error codes: `AUTH_GOOGLE_DISABLED` 404, `AUTH_GOOGLE_CHALLENGE_INVALID` 400, `AUTH_GOOGLE_TOKEN_INVALID` 401, `AUTH_GOOGLE_EMAIL_NOT_VERIFIED` / `AUTH_EMAIL_NOT_VERIFIED` / `AUTH_ACCOUNT_DISABLED` / `AUTH_PASSWORD_NOT_SET` 403, `AUTH_GOOGLE_LINK_REQUIRED` / `AUTH_GOOGLE_IDENTITY_CONFLICT` / `AUTH_GOOGLE_ALREADY_LINKED` 409, `RATE_LIMITED` 429, `AUTH_GOOGLE_UNAVAILABLE` 503.

### Browser security headers (for the hosting layer; nothing is set by the dev server)

- CSP: `script-src https://accounts.google.com/gsi/client`; `frame-src https://accounts.google.com/gsi/`; `connect-src https://accounts.google.com/gsi/`; `style-src https://accounts.google.com/gsi/style`.
- COOP: `same-origin-allow-popups` (or none). `same-origin` breaks the popup.
- HTTPS is required for every non-localhost origin (tokens travel in bodies and headers).

### Database migration (manual)

New table `UserExternalIdentities` (unique `(Provider, ProviderSubject)` and `(UserId, Provider)`, FK to `Users`). No other schema change. From the repository root:

```
dotnet ef migrations add AddGoogleExternalIdentities --project backend/src/OpsFlow.Infrastructure --startup-project backend/src/OpsFlow.Api
dotnet ef database update --project backend/src/OpsFlow.Infrastructure --startup-project backend/src/OpsFlow.Api
```

Until it is applied, the Development host refuses to start (`DevelopmentMigrationGuard`).

### Manual end-to-end check (real Google account)

1. Apply the migration, start API (`dotnet run --project backend/src/OpsFlow.Api`) and SPA (`cd frontend && npm start`), open `http://localhost:4200/login`.
2. New Gmail test user -> "Sign in with Google" -> lands on the dashboard without a workspace; `GET /api/v1/organizations` is empty.
3. Sign out, sign in with Google again -> same user.
4. Register a password account with a Gmail address, then use Google with that address -> "link required" message, no session.
5. Sign in with the password, open Account, confirm the password, choose the Google account -> "linked"; sign out and sign in with Google -> same account.
6. Try linking the same Google account from a second password account -> conflict message.
7. Leave the login page open for more than 5 minutes, then click the Google button -> it has renewed its challenge; sign-in succeeds.

## Profile

- `GET /api/v1/auth/me` -> `UserProfileDto { id, email, displayName, hasPassword, googleLinked }`.
- `PUT /api/v1/auth/me { displayName }` -> updated profile. Display name: trimmed, 2-100 characters, no control or invisible formatting characters. Access tokens carry no display name, so no token refresh is needed.

## Organization settings

- `GET /api/v1/tenants/{tenantId}/settings` (Owner, Manager, Agent) -> `{ reportingTimeZone, name, slug, ticketNumberPrefix }`.
- `PUT /api/v1/tenants/{tenantId}/settings { reportingTimeZone, name? }` (Owner). `name` is optional (null keeps it). Slug and ticket number prefix are read-only. Categories and SLA policies keep their dedicated endpoints. Nothing here is cached, so no invalidation is needed.

## Invitation resend

- `POST /api/v1/tenants/{tenantId}/invitations/{invitationId}/resend` (Owner: any role; Manager: Agent/Customer) -> `InvitationDto` without the token.
- Pending or expired (not accepted/revoked) invitations only; refused when the invitee is already an active member. New token (old link invalid immediately), fresh 72-hour expiry, 2-minute cooldown (`429 INVITATION_RESEND_COOLDOWN`), concurrent resends serialized by the tenant `OwnerChangeStamp`.
- Like invitation creation the email carries a one-time secret, so it is sent directly after the commit (best effort) and never staged in the outbox or Hangfire. If delivery fails, resend again after the cooldown.
