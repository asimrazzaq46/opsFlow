# Google Sign-In and account endpoints

## Google Sign-In (Google Identity Services, ID-token popup flow)

Google only proves the external identity. OpsFlow keeps account state, memberships, roles, JWTs, sessions and refresh-token rotation, and a Google sign-in returns exactly the same `AuthResponse` as a password login. No client secret and no API key are used, and the flow never grants a membership or a role.

### Configuration

| Setting | Where | Notes |
| --- | --- | --- |
| `GoogleAuth:ClientId` (`GoogleAuth__ClientId`) | `appsettings.Development.json`, or environment and secret store elsewhere | Public OAuth **Web** client id (`<number>-<id>.apps.googleusercontent.com`). Empty disables Google sign-in and leaves password authentication unaffected; a malformed value stops startup. |
| `GoogleAuth:ChallengeLifetimeSeconds` | `appsettings.json` | Lifetime of a one-time challenge, default 300. |
| `Cache:RedisConnectionString` | User-secrets or environment | When set, challenges live in Redis: shared between instances, consumed atomically with `GETDEL`, and failing closed with `503 AUTH_GOOGLE_UNAVAILABLE` when Redis is unreachable. Without it an in-process store is used, which is fine for one instance. **Multi-instance deployments must configure Redis.** |

The Angular app needs no Google configuration: the challenge endpoint returns the client id, and the button hides itself when the API answers `AUTH_GOOGLE_DISABLED`.

### Google Cloud console

- OAuth client type: **Web application**.
- Authorized JavaScript origins: `http://localhost:4200` (Angular dev server and the default `App:BaseUrl`) and every production HTTPS origin. No redirect URI is needed (popup and callback).
- While the OAuth consent screen is in **Testing** mode, only listed test users can sign in.

### Flow

1. `POST /api/v1/auth/google/challenge` returns `{ challengeId, nonce, expiresAtUtc, clientId }`.
2. The SPA calls `google.accounts.id.initialize({ client_id, nonce, callback, auto_select: false })` and renders the official button. GIS keeps one global configuration, so a new challenge means a controlled re-initialization: only the latest registration accepts a credential, and only once.
3. The callback posts `{ credential, challengeId }` to `POST /api/v1/auth/google`.
4. The API consumes the challenge atomically, validates the ID token with `Google.Apis.Auth` (signature, issuer, audience = client id, expiry, issued-at, and `azp` when present), compares the token's nonce with the challenge and requires `email_verified`. If Google's keys cannot be retrieved the answer is `503 AUTH_GOOGLE_UNAVAILABLE`, which is distinct from an invalid credential (`401 AUTH_GOOGLE_TOKEN_INVALID`).
5. Resolution:
   - A linked Google `sub` signs in to its account.
   - A new `sub` with an unused email creates an account with no membership and no role. Gmail addresses and Workspace accounts whose `hd` matches the email domain are signed in immediately; other addresses receive a confirmation email (202) first.
   - An email already used by another account returns `409 AUTH_GOOGLE_LINK_REQUIRED`. Accounts are never matched or linked by email.

**Linking (signed in):** `POST /api/v1/auth/google/link/challenge { password }` re-authenticates the user (the login lockout applies), then GIS, then `POST /api/v1/auth/google/link { credential, challengeId }`. Link challenges are bound to the user and cannot be used for sign-in, and sign-in challenges cannot be used for linking. Accounts without a local password must first set one with "Forgot password". Unlinking is not supported.

### Error codes

| Status | Codes |
| --- | --- |
| 400 | `AUTH_GOOGLE_CHALLENGE_INVALID` |
| 401 | `AUTH_GOOGLE_TOKEN_INVALID` |
| 403 | `AUTH_GOOGLE_EMAIL_NOT_VERIFIED`, `AUTH_EMAIL_NOT_VERIFIED`, `AUTH_ACCOUNT_DISABLED`, `AUTH_PASSWORD_NOT_SET` |
| 404 | `AUTH_GOOGLE_DISABLED` |
| 409 | `AUTH_GOOGLE_LINK_REQUIRED`, `AUTH_GOOGLE_IDENTITY_CONFLICT`, `AUTH_GOOGLE_ALREADY_LINKED` |
| 429 | `RATE_LIMITED` |
| 503 | `AUTH_GOOGLE_UNAVAILABLE` |

### Data model

`UserExternalIdentities` stores the link: unique `(Provider, ProviderSubject)` and `(UserId, Provider)`, with a foreign key to `Users`. It is part of the EF Core migrations; apply them as described in [background-jobs.md](background-jobs.md#database-setup). The Development host refuses to start while migrations are unapplied. Google-only users have an empty password hash, and the password endpoints reject them generically.

### Browser security headers

Set these in the hosting layer; the dev server sets none.

- CSP: `script-src https://accounts.google.com/gsi/client`; `frame-src https://accounts.google.com/gsi/`; `connect-src https://accounts.google.com/gsi/`; `style-src https://accounts.google.com/gsi/style`.
- COOP: `same-origin-allow-popups` (or none); `same-origin` breaks the popup.
- HTTPS is required for every non-localhost origin, because tokens travel in request and response bodies and headers.

### Local setup and manual check

1. Put the OAuth client id in `GoogleAuth:ClientId` and add `http://localhost:4200` as an authorized origin.
2. Start the API (`dotnet run --project backend/src/OpsFlow.Api`) and the SPA (`cd frontend && npm start`), then open `http://localhost:4200/login`.
3. Sign in with a new Gmail test user: you land without a workspace and `GET /api/v1/organizations` is empty.
4. Sign out and sign in with Google again: same user.
5. Register a password account with a Gmail address, then use Google with that address: "link required", no session.
6. Sign in with the password, open Account, confirm the password and choose the Google account: it is linked, and signing in with Google then reaches the same account.
7. Try linking the same Google account from a second password account: conflict message.
8. Leave the login page open for more than five minutes and click the Google button: the challenge has been renewed and the sign-in succeeds.

## Related account endpoints

### Profile

- `GET /api/v1/auth/me` returns `UserProfileDto { id, email, displayName, hasPassword, googleLinked }`.
- `PUT /api/v1/auth/me { displayName }` returns the updated profile. The display name is trimmed, 2 to 100 characters, without control or invisible formatting characters. Access tokens carry no display name, so no token refresh is needed.

### Organization settings

- `GET /api/v1/tenants/{tenantId}/settings` (Owner, Manager, Agent) returns `{ reportingTimeZone, name, slug, ticketNumberPrefix }`.
- `PUT /api/v1/tenants/{tenantId}/settings { reportingTimeZone, name? }` (Owner). `name` is optional and `null` keeps the current one. The slug and ticket number prefix are read-only. Categories and SLA policies have their own endpoints.

### Invitation resend

- `POST /api/v1/tenants/{tenantId}/invitations/{invitationId}/resend` (Owner: any role; Manager: Agent and Customer) returns the `InvitationDto` without the token.
- Only pending or expired invitations can be resent (not accepted or revoked ones), and not when the invitee is already an active member. The token is replaced (the old link stops working immediately), the 72-hour expiry restarts, and a 2-minute cooldown applies (`429 INVITATION_RESEND_COOLDOWN`). Concurrent resends are serialized by the tenant `OwnerChangeStamp`.
- The e-mail carries a one-time secret, so it is never stored in the outbox or in Hangfire. It is sent as a detached best-effort delivery after the commit; if it fails, resend after the cooldown. See [background-jobs.md](background-jobs.md#one-time-secret-e-mails).
