# TruvoID Handoff

## Architecture

- **API:** `src/TruvoID.API/` — .NET 10 minimal API + PostgreSQL (control plane + per-tenant schemas with RLS). Deployed to Railway via `Dockerfile`.
- **Web:** `web/` — React + Vite SPA. Deployed to Vercel. **This is the only frontend.**
- **Tests:** `tests/TruvoID.Tests/` — xUnit + Testcontainers (needs Docker).

The former Blazor frontend (`Components/`, root `Program.cs`, `TruvoID.csproj`, `wwwroot/`) has been **removed**. The solution (`TruvoID.slnx`) contains the API and test projects.

## Required environment variables (API)

```text
ConnectionStrings__Postgres        DML-only truvo_app role (runtime)
ConnectionStrings__PostgresMigrator  DDL-owning migrator role (migrate/worker only)
Postgres__AppRole                  e.g. truvo_app
Postgres__TenantCredentialKey      base64 32-byte key (openssl rand -base64 32)
Postgres__TenantCredentialKeyId    e.g. k1
Jwt__SecretKey                     >= 32 chars; required outside Development
Jwt__Issuer / Jwt__Audience        default TruvoID
Cors__Origins__0                   web app origin, e.g. https://gettruvoid.com
App__BaseUrl                       web app base URL (invite links + payment redirect allowlist)
Resend__ApiKey (or RESEND_API_KEY) Resend API key for email
EMAIL_FROM_ADDRESS                 verified Resend sender (default noreply@gettruvoid.com)
Verification__Provider             idaccess | sandbox
IDACCESS_API_KEY                   required when Verification__Provider=idaccess
Flutterwave__SecretKey             Flutterwave secret key
Flutterwave__WebhookHash           Flutterwave webhook verif-hash
```

`GET /health` reports `environment` and whether `email` is configured.

## Required environment variables (web)

```text
VITE_API_BASE_URL        API origin, e.g. https://api.gettruvoid.com
VITE_SUPPORT_WHATSAPP    optional; support number for bank-transfer receipts
```

If `VITE_API_BASE_URL` is unset in a production build, the SPA falls back to
`window.location.origin`, which is almost never the API — always set it.

## Email (Resend)

Email powers invitations and password resets. If `Resend__ApiKey`/`RESEND_API_KEY`
is missing the API logs a startup warning and `/health` shows `email: not_configured`.
A `403` from Resend means the sending domain is not verified; verify the domain in
Resend and set `EMAIL_FROM_ADDRESS` to a sender on it.

## Payments

- **Flutterwave:** the React wallet starts hosted checkout and verifies on redirect;
  the webhook credits idempotently. The checkout `redirectUrl` must be an origin in
  `Cors:Origins` or `App:BaseUrl` (enforced server-side).
- **Bank transfer:** the React wallet shows the transfer accounts; platform admins
  credit the wallet with `POST /v1/admin/tenant-wallets/{organizationId}/credit`
  (`amountKobo`). There is no self-service manual-topup endpoint.

## Migrations

```powershell
dotnet run --project src/TruvoID.API -- migrate
dotnet run --project src/TruvoID.API -- worker
```

Migrations run as the DDL-owning migrator role and refuse to run as a superuser.
The runtime API only ever holds DML-only credentials.

## Verification

```powershell
dotnet build TruvoID.slnx -c Release
dotnet test TruvoID.slnx -c Release        # needs Docker (Testcontainers)
cd web; npm.cmd run build
cd web; npm.cmd test                       # Vitest
```

Tests: `tests/TruvoID.Tests` (unit/DB, links backend source),
`tests/TruvoID.IntegrationTests` (hosts the real API via `WebApplicationFactory`
against a real Postgres — auth policies, webhook signature + idempotent crediting,
login lockout), and Vitest specs under `web/src/*.test.ts`.

## Known remaining work

- The admin UI is React (`/admin/agencies`, `/admin/pricing`, `/admin/api-keys`).
  The old Blazor admin screens for financials, NIMC config, and audit log had no
  backing endpoints; build them only if those features are still wanted.
- Invitation-accept HTTP coverage (the store itself is already unit-tested).
