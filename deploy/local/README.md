# Local Postgres

```powershell
docker compose -f deploy/local/docker-compose.yml up -d

$env:ConnectionStrings__PostgresMigrator = "Host=localhost;Port=5433;Database=truvoid;Username=truvo_migrator;Password=truvo_migrator_dev"
dotnet run --project src/TruvoID.API -- migrate
```

Roles (see `postgres-init/01-roles.sql`):

| Role | Used by | Can |
|---|---|---|
| `truvo_migrator` | `migrate` command only | Own schemas, run DDL, create per-Organization roles |
| `truvo_app` | The running API (`ConnectionStrings__Postgres`) | DML grants only; subject to RLS |

Migrations live in `src/TruvoID.API/TruvoID.Infrastructure/Postgres/Migrations/ControlPlane/`.
Never edit an applied migration — add the next numbered file instead; the migrator
refuses to run if an applied script's checksum changes.

Reset everything: `docker compose -f deploy/local/docker-compose.yml down -v`.
