# AWS API Deployment

The React frontend remains hosted on Vercel. This package is for the
PostgreSQL-backed .NET API only.

## Runtime Shape

- Container image built from the repository `Dockerfile`.
- AWS ECR stores the image.
- ECS on AWS Fargate runs the API behind an HTTPS load balancer.
- Amazon RDS for PostgreSQL hosts the control-plane and tenant schemas.
- AWS Secrets Manager stores database credentials and application secrets.

The API container listens on port `8080` and exposes `GET /health` for the
load-balancer health check. Database migrations must run as a separate release
step with the migrator role; never give the long-running API task the
`PostgresMigrator` credentials.

## Required Configuration

Configure these values as ECS task secrets or environment variables:

```text
ConnectionStrings__Postgres
Postgres__TenantCredentialKey
Jwt__SecretKey
Cors__Origins__0=https://<the-vercel-domain>
```

Optional integrations:

```text
Resend__ApiKey
Flutterwave__SecretKey
Flutterwave__WebhookHash
```

The runtime database connection must use the DML-only `truvo_app` role. The
migrator connection must be supplied only to a one-off migration task:

```text
ConnectionStrings__PostgresMigrator
```

## Build Locally

From the repository root:

```powershell
docker build -t truvoid-api .
docker run --rm -p 8080:8080 truvoid-api
```

The container will fail fast without its required production configuration;
that is intentional. It must not silently start with development database or
JWT credentials.

## Push To ECR

After creating an AWS account, configuring the AWS CLI, and creating an ECR
repository named `truvoid-api`:

```powershell
$env:AWS_REGION = 'us-east-1'
$env:AWS_ACCOUNT_ID = (aws sts get-caller-identity --query Account --output text)
$env:ECR_REGISTRY = "$env:AWS_ACCOUNT_ID.dkr.ecr.$env:AWS_REGION.amazonaws.com"

aws ecr get-login-password --region $env:AWS_REGION |
  docker login --username AWS --password-stdin $env:ECR_REGISTRY

docker build -t truvoid-api .
docker tag truvoid-api:latest "$env:ECR_REGISTRY/truvoid-api:latest"
docker push "$env:ECR_REGISTRY/truvoid-api:latest"
```

Deploy the image through ECS after configuring the task definition, secrets,
security groups, and target group health check (`/health`).

## Migration Order

1. Provision RDS and the required database roles.
2. Run the API migration command from a one-off task using the migrator secret:

   ```powershell
   docker run --rm --env-file .env.migrator truvoid-api migrate
   ```

3. Run `provision-tenants` when pending organizations need tenant schemas.
4. Start or update the API ECS service with only the runtime secret.
5. Set the Vercel `VITE_API_BASE_URL` to the API HTTPS URL.

Do not commit `.env.migrator`, database passwords, JWT keys, or API provider
keys. Use Secrets Manager for the deployed service.
