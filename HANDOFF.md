# TruvoID Development Handoff

**Last updated:** 2026-09-28
**Branch:** `master`
**Repository state:** The PostgreSQL-only cutover is in progress and the current feature work is uncommitted in the working tree.

## Current Position

TruvoID is in the middle of a multitenant PostgreSQL transition.

Completed and validated:

- PostgreSQL control-plane schema and migrator.
- Per-organization PostgreSQL schemas and login roles.
- Row-level security for organization and outlet isolation.
- PostgreSQL-backed tenant identity and password hashing.
- Tenant outlet, wallet, verification reservation, and revenue outbox APIs.
- PostgreSQL-backed API-key metadata and authentication.
- React/Vite frontend build.
- Local PostgreSQL migration and provisioning flow.

The legacy MongoDB application surface has been retired. The deployed API target is PostgreSQL-only, and the API project no longer references the MongoDB driver or MongoDB data layer.

## Recent API-Key Work

API keys are stored in `control.api_key` in PostgreSQL. The raw secret is only returned once at creation time. Only the key prefix and SHA-256 hash are stored.

Agency endpoints:

- `GET /v1/tenant/api-keys`
- `POST /v1/tenant/api-keys/outlets/{outletId}`
- `DELETE /v1/tenant/api-keys/{id}`

Platform-admin endpoint:

- `POST /v1/admin/api-keys/tenants`
- `POST /v1/admin/agencies/invite`

Agency invitation acceptance:

- `POST /v1/auth/agency-invitations/accept`

Organization setup:

- `GET /v1/tenant/setup`
- `PUT /v1/tenant/setup/{section}`
- `PUT /v1/tenant/setup/access-level`
- `PUT /v1/tenant/setup/attestation`
- `POST /v1/tenant/setup/documents`
- `POST /v1/tenant/setup/submit`

Platform-admin key revocation/listing:

- `GET /v1/admin/api-keys`
- `POST /v1/admin/api-keys/{id}/revoke`

API-key authentication:

```http
X-API-Key: trv_live_<secret>
```

An outlet key receives `organization_id`, `outlet_id`, and `api_key_id` claims. Tenant verification requests accept either JWT or API key authentication:

- `POST /v1/tenant/verification-calls/reserve`

Documentation page built by the frontend:

- `/api-docs.html`

## PostgreSQL Migrations

Migration locations:

- `src/TruvoID.API/TruvoID.Infrastructure/Postgres/Migrations/ControlPlane/`
- `src/TruvoID.API/TruvoID.Infrastructure/Postgres/Migrations/Tenant/`

Important migrations:

- `ControlPlane/0001_control_plane.sql`: organizations, users, API keys, pricing, audit, and central ledgers.
- `ControlPlane/0002_tenant_credentials.sql`: encrypted per-organization database credentials.
- `ControlPlane/0003_api_key_usage.sql`: API-key `call_count` metadata and runtime update grant.
- `ControlPlane/0004_agency_invitations.sql`: PostgreSQL agency invitation tokens and acceptance state.
- `ControlPlane/0005_organization_setup.sql`: progressive organization profile and supporting documents.
- `Tenant/0001_tenant_core.sql`: tenant tables, wallets, outlets, verification calls, RLS, and append-only ledger rules.
- `Tenant/0002_outbox_delivery.sql`: revenue outbox delivery state.

Never edit an applied migration. Add the next numbered migration. The migrator verifies checksums and rejects modified applied scripts.

## Local PostgreSQL Workflow

Start PostgreSQL:

```powershell
docker compose -f deploy/local/docker-compose.yml up -d
```

Run migrations:

```powershell
$env:ConnectionStrings__PostgresMigrator = "Host=localhost;Port=5433;Database=truvoid;Username=truvo_migrator;Password=truvo_migrator_dev"
dotnet run --project src/TruvoID.API -- migrate
```

Provision pending organizations. Use one stable 32-byte base64 key for the environment:

```powershell
$env:Postgres__TenantCredentialKey = "<base64 32-byte key>"
dotnet run --project src/TruvoID.API -- provision-tenants
```

Relay tenant revenue events:

```powershell
dotnet run --project src/TruvoID.API -- relay-revenue
```

Run the API locally:

```powershell
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5433;Database=truvoid;Username=truvo_app;Password=truvo_app_dev"
$env:Postgres__TenantCredentialKey = "<same base64 32-byte key>"
dotnet run --project src/TruvoID.API
```

Reset the local database completely:

```powershell
docker compose -f deploy/local/docker-compose.yml down -v
```

## Required Deployment Configuration

The API requires these outside Development:

- `ConnectionStrings__Postgres`: runtime DML-only PostgreSQL connection using `truvo_app` or equivalent.
- `ConnectionStrings__PostgresMigrator`: only for migration/provisioning jobs using the migrator role.
- `Postgres__TenantCredentialKey`: stable base64-encoded 32-byte AES key.
- `Postgres__TenantCredentialKeyId`: optional key identifier; defaults to `k1`.
- `Jwt__SecretKey` or `JWT_SECRET`.
- `Jwt__Issuer` and `Jwt__Audience` if non-default values are used.
- `Cors__Origins__0`: deployed frontend origin.
- `RESEND_API_KEY` if notification email is enabled.

The frontend needs this build-time variable:

- `VITE_API_BASE_URL`: deployed API base URL.

## Verification Performed

The following checks passed during this handoff:

- `dotnet build TruvoID.sln --no-restore`
- `dotnet build src/TruvoID.API/TruvoID.API.csproj --no-restore`
- `dotnet test tests/TruvoID.Tests/TruvoID.Tests.csproj --no-restore`
- Result: 129 passed, 0 failed. Legacy MongoDB-only tests were removed with the retired application surface.
- `npm.cmd run build` from `web/`
- Local PostgreSQL migration applied `ControlPlane/0003_api_key_usage.sql` successfully.
- Local `control.api_key` table verified with `call_count` present.

The test project emits two non-blocking `NU1510` package-pruning warnings for `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.Http`.

## Remaining Work

Before production deployment:

1. Review the full uncommitted diff.
2. Add endpoint-level tests for PostgreSQL agency invitation creation and acceptance.
3. Add production email delivery for agency invitation links; the current admin UI returns a copyable invitation link.
4. Run the production migration job against the target PostgreSQL database.
5. Provision all pending organizations and verify their tenant roles.
6. Configure the required production environment variables.
7. Smoke-test institution registration without outlets, keys, or wallet funding.
8. Smoke-test agency invitation acceptance, outlet creation, outlet key generation, and `/v1/tenant/verification-calls/reserve`.
9. Smoke-test the PostgreSQL-only deployment and then remove the old MongoDB Railway service.

## Useful Files

- `src/TruvoID.API/Program.cs`: service registration, startup validation, migration commands, authentication, CORS.
- `src/TruvoID.API/Endpoints/ApiKeyEndpoints.cs`: tenant and legacy API-key routes.
- `src/TruvoID.API/Endpoints/AdminDashboardEndpoints.cs`: platform-admin API-key routes.
- `src/TruvoID.API/Auth/ApiKeyAuthenticationHandler.cs`: PostgreSQL API-key authentication.
- `src/TruvoID.API/TruvoID.Infrastructure/Postgres/PostgresAuditService.cs`: PostgreSQL audit persistence.
- `src/TruvoID.API/TruvoID.Infrastructure/Postgres/PostgresApiKeyStore.cs`: PostgreSQL API-key persistence.
- `src/TruvoID.API/TruvoID.Infrastructure/Postgres/Migrations/ControlPlane/0003_api_key_usage.sql`: latest control-plane migration.
- `web/api-docs.html`: agency API documentation.
- `web/src/ApiKeysPage.tsx`: agency key-management page component.
