# Local Postgres

```powershell
docker compose -f deploy/local/docker-compose.yml up -d

$env:ConnectionStrings__PostgresMigrator = "Host=127.0.0.1;Port=5433;Database=truvoid;Username=truvo_migrator;Password=truvo_migrator_dev"
dotnet run --project src/TruvoID.API -- migrate

# Create schema + database role for every 'pending' Organization.
# Keep this key stable — it encrypts each Organization's DB password (openssl rand -base64 32).
$env:Postgres__TenantCredentialKey = "<base64 32-byte key>"
dotnet run --project src/TruvoID.API -- provision-tenants

# Deliver tenant credit-sale/outlet-resale events to the central revenue ledger.
dotnet run --project src/TruvoID.API -- relay-revenue

# Runtime API credentials use the DML-only role and the same credential key.
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5433;Database=truvoid;Username=truvo_app;Password=truvo_app_dev"
$env:Postgres__TenantCredentialKey = "<same base64 32-byte key>"
dotnet run --project src/TruvoID.API
```

Each Institution/Agency gets its own schema (`org_<id>`) and login role
(`org_<id>_rw`); Outlets are rows inside it, separated by row-level security.

Roles (see `postgres-init/01-roles.sql`):

| Role | Used by | Can |
|---|---|---|
| `truvo_migrator` | `migrate` command only | Own schemas, run DDL, create per-Organization roles |
| `truvo_app` | The running API (`ConnectionStrings__Postgres`) | DML grants only; subject to RLS |

Migrations live in `src/TruvoID.API/TruvoID.Infrastructure/Postgres/Migrations/` —
`ControlPlane/` for the central schema, `Tenant/` for every Organization's schema.
Never edit an applied migration — add the next numbered file instead; the migrator
refuses to run if an applied script's checksum changes.

Reset everything: `docker compose -f deploy/local/docker-compose.yml down -v`.
