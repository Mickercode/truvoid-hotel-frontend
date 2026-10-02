# Sandbox environment

A sandbox is the same build as production with one setting changed:

```
Verification__Provider=sandbox
```

That single variable makes the deployment:

- answer `/v1/verify/*` from `SandboxIdentityProvider` — no IDAccess calls, no upstream cost
- issue `trv_test_…` API keys (production issues `trv_live_…`)
- serve `GET /v1/verify/test-numbers` and `POST /v1/tenant/wallet/sandbox-funds` (both 404 on live)
- report `"environment": "sandbox"` from `/health` and in every verification response; the
  dashboard shows a sandbox banner

## Test numbers

| Type  | Match         | No match      | Provider error (refunded) |
|-------|---------------|---------------|---------------------------|
| NIN   | `00000000001` | `00000000002` | `00000000003`             |
| BVN   | `22222222221` | `22222222222` | `22222222223`             |
| Phone | `08000000001` | `08000000002` | `08000000003`             |

Any other well-formed number returns "no match", so a real NIN never appears to verify.

## Railway setup

Use a separate **environment** in the same Railway project (Project → environment dropdown →
New Environment → "sandbox"), so it gets its own Postgres, API and worker, with nothing shared
with production.

1. Create the `truvo_migrator` / `truvo_app` roles on the sandbox Postgres (same SQL as production,
   different passwords).
2. API service variables: copy production's, then change
   - `Verification__Provider=sandbox`
   - `ConnectionStrings__Postgres` / `ConnectionStrings__PostgresMigrator` → sandbox database
   - `Jwt__SecretKey`, `Postgres__TenantCredentialKey` → **new values** (a sandbox token must
     never be valid in production)
   - `App__BaseUrl` → the sandbox dashboard URL
   - remove `IDACCESS_API_KEY` and live Flutterwave keys (use Flutterwave **test** keys if needed)
3. Worker service: same variables, start command `dotnet /app/TruvoID.API.dll worker`.
4. Domain: `sandbox.api.gettruvoid.com` → sandbox API; point a sandbox build of the dashboard at it
   with `VITE_API_BASE_URL=https://sandbox.api.gettruvoid.com`.
5. Set prices once (Admin → Pricing) after creating a platform admin with
   `dotnet /app/TruvoID.API.dll create-platform-admin you@gettruvoid.com`.
