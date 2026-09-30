# Alibaba Cloud API Deployment

This deployment runs the PostgreSQL-backed .NET API on Alibaba Cloud. The
React frontend can remain on Vercel and should point `VITE_API_BASE_URL` at the
public HTTPS address of the API.

## Recommended Architecture

```text
Vercel React app
       |
       v
Alibaba Cloud ALB (HTTPS)
       |
       v
ECS Linux instance(s) running the API container
       |
       +--> ACR image pull
       +--> RDS PostgreSQL over a private VPC network
```

Use the same VPC and security group strategy for ECS, ALB, and RDS:

- Expose only `443` publicly on the ALB. Redirect `80` to `443` if needed.
- Allow the ALB security group to reach ECS on TCP `8080`.
- Allow the ECS security group to reach RDS on TCP `5432`.
- Do not expose RDS or the ECS `8080` port to the public Internet.
- Store passwords and application keys in Alibaba Cloud KMS/Secrets Manager,
  not in the repository or an ECS user-data script.

The API listens on port `8080` and provides `GET /health` for the ALB health
check.

## Production Configuration

Set these environment variables on the ECS API container. Alibaba Cloud's
double-underscore environment variable convention maps directly to ASP.NET
Core configuration sections.

Required runtime values:

```text
ConnectionStrings__Postgres=<RDS connection string using truvo_app>
Postgres__TenantCredentialKey=<base64 encoded 32-byte key>
Postgres__TenantCredentialKeyId=k1
Jwt__SecretKey=<long random signing key>
Jwt__Issuer=TruvoID
Jwt__Audience=TruvoID
Cors__Origins__0=https://<your-vercel-domain>
```

Optional integrations:

```text
Resend__ApiKey=<Resend API key>
Flutterwave__SecretKey=<Flutterwave secret key>
Flutterwave__WebhookHash=<Flutterwave webhook hash>
```

Use the DML-only `truvo_app` database role for `ConnectionStrings__Postgres`.
Never give the long-running API container `PostgresMigrator` credentials.

Generate a tenant credential key once and keep it stable for the lifetime of
the deployment:

```powershell
openssl rand -base64 32
```

## Build And Push To ACR

Create an Alibaba Cloud Container Registry Enterprise Edition instance and a
namespace/repository such as `truvoid/truvoid-api`. Use a token with push
permission, then run from the repository root:

```powershell
$env:ACR_REGISTRY = '<instance-id>-registry.<region-id>.cr.aliyuncs.com'
$env:ACR_NAMESPACE = 'truvoid'
$env:ACR_REPOSITORY = 'truvoid-api'
$env:IMAGE_TAG = (git rev-parse --short HEAD)

docker login $env:ACR_REGISTRY
docker build -t "$env:ACR_REGISTRY/$env:ACR_NAMESPACE/$env:ACR_REPOSITORY`:$env:IMAGE_TAG" .
docker push "$env:ACR_REGISTRY/$env:ACR_NAMESPACE/$env:ACR_REPOSITORY`:$env:IMAGE_TAG"
```

Prefer immutable commit tags over `latest`. The ECS deployment should be
updated to a specific image tag so a rollback is deterministic.

## Database And Migration Order

1. Create the RDS PostgreSQL instance inside the application VPC.
2. Create the `truvo_migrator` and `truvo_app` roles using the SQL in
   `deploy/local/postgres-init/01-roles.sql`.
3. Allow the ECS security group to connect to RDS on port `5432`.
4. Run the migration command once from a temporary ECS task or a controlled
   ECS shell session. Supply only the migrator connection string:

```powershell
docker run --rm `
  -e ConnectionStrings__PostgresMigrator='<RDS migrator connection string>' `
  -e Postgres__AppRole='truvo_app' `
  -e Postgres__TenantCredentialKey='<same stable key>' `
  <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<IMAGE_TAG> migrate
```

5. Run `provision-tenants` after organizations are approved and need tenant
   schemas:

```powershell
docker run --rm `
  -e ConnectionStrings__PostgresMigrator='<RDS migrator connection string>' `
  -e Postgres__TenantCredentialKey='<same stable key>' `
  <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<IMAGE_TAG> provision-tenants
```

6. Start the API container with only the runtime configuration.
7. Run `relay-revenue` as a scheduled one-off task if revenue outbox delivery
   is enabled.

Do not run migrations automatically in the API container startup command. This
keeps schema ownership and runtime DML permissions separate.

## ECS Container

On an Alibaba Cloud Linux ECS host with Docker installed:

```bash
docker pull <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<IMAGE_TAG>
docker rm -f truvoid-api 2>/dev/null || true
docker run -d \
  --name truvoid-api \
  --restart unless-stopped \
  -p 8080:8080 \
  --env-file /etc/truvoid/api.env \
  <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<IMAGE_TAG>
```

Restrict the ECS security group so only the ALB can reach port `8080`.
Configure the ALB target group as follows:

```text
Protocol: HTTP
Port: 8080
Health check path: /health
Expected status: 200
```

Terminate TLS at the ALB with an Alibaba Cloud SSL certificate. Forward the
public API hostname to the ECS target group, then set that hostname as the
frontend's `VITE_API_BASE_URL`.

## Verification

After DNS and the ALB listener are configured:

```powershell
Invoke-RestMethod 'https://api.<your-domain>/health'
```

Expected response:

```json
{"status":"ok"}
```

Check the ECS container logs if the target is unhealthy:

```bash
docker logs --tail 200 truvoid-api
```

The application intentionally fails fast when production database, JWT, or
tenant encryption configuration is missing.

## Rollback

Deploy the previous immutable image tag, then verify `/health` and one
authenticated API request through the ALB:

```bash
docker pull <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<PREVIOUS_TAG>
docker rm -f truvoid-api
docker run -d --name truvoid-api --restart unless-stopped \
  -p 8080:8080 \
  --env-file /etc/truvoid/api.env \
  <ACR_REGISTRY>/<ACR_NAMESPACE>/<ACR_REPOSITORY>:<PREVIOUS_TAG>
```

Database migrations are forward-only in this project. Confirm application
compatibility before deploying a migration that cannot be rolled back.
