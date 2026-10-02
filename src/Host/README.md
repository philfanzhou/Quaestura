# Quaestura Deployment and Operation Guide

This document covers environment setup, configuration requirements, and Docker container deployment. See [`docs/development/Deployment.md`](../../docs/development/Deployment.md) for the complete specification.

## 1. Quick start

### 1.1 Requirements

1. **SeaweedFS**: the SeaweedFS service (S3 port 8333) must be started first
2. **Database**: PostgreSQL; startup directly invokes ServiceMantle 0.3.0 `StartupDatabaseGate` in explicit `MultiInstance` mode; on startup the target database is created first when it is verifiably missing and `Database:AllowCreate` (default `true`) permits it, then the shared ServiceMantle migration orchestrator holds the real PostgreSQL session advisory lock for the service id `quaestura` (fixed 30-second acquire budget) while the `QuaesturaMigrationExecutor` strictly inspects the target and applies the EF Core migrations in `src/Database/Migrations/` only for verified empty or pending states; success requires the held-lock final inspection to pass. Verified legacy (EnsureCreated-era) databases are taken over without data loss; unknown or corrupt schemas are refused at startup instead of being silently stamped; concurrently starting instances serialize on the lock and the later ones skip (see [`docs/development/Deployment.md`](../../docs/development/Deployment.md) §2.1)

### 1.2 WebAPI ports

- **HTTP**: 5007
- **Swagger UI**: http://localhost:5007/swagger (Development only)
- **Liveness probe**: http://localhost:5007/health/live (always 200 while serving; never touches the database)
- **Readiness probe**: http://localhost:5007/health/ready (`/health` is an alias; 200 only after startup initialization succeeded and this request's scoped, read-only MappedSchema probe of all EF tables and columns passed, 503 otherwise)
- **WebUI (admin frontend)**: http://localhost:5007/ (same port and process as the API; see [`frontend/docs/admin-frontend-spec.md`](../../frontend/docs/admin-frontend-spec.md))

### 1.3 Configuration

Configure the following key connection settings in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Host=ruoyu-postgres;Port=5432;Database=quaestura;Username=postgres;Password=postgres;"
  },
  "Oss": {
    "InternalEndpoint": "ruoyu-seaweedfs:8333",
    "InternalSecure": false,
    "AccessKey": "seaweedfs_admin",
    "SecretKey": "seaweedfs_admin",
    "BucketName": "ruoyu-study",
    "PublicBaseUrl": "https://ry.zhoufan.asia/oss"
  }
}
```

`APP_TITLE` setting (**optional**, defaults to `Quaestura Admin`):

```json
{
  "APP_TITLE": "quaestura"
}
```

On startup, the backend replaces the `__APP_TITLE__` placeholder in `index.html` with this value and injects `window.__APP_TITLE__` into `<head>` for the frontend JS to read. `start.sh` passes `-e APP_TITLE="${CONTAINER_NAME}"` by default.

## 2. Endpoints

See [`docs/overview/ApiSpec.md`](../../docs/overview/ApiSpec.md) for the complete endpoint specification.

### Question management

| Method | Path | Description |
|------|------|------|
| GET | `/admin/questions` | Search/paginate |
| GET | `/admin/questions/{id}` | Details |
| POST | `/admin/questions` | Upload a question (multipart/form-data) |
| DELETE | `/admin/questions/{id}` | Delete |

### Knowledge point management

| Method | Path | Description |
|------|------|------|
| GET | `/admin/knowledges` | List/search/paginate |
| GET | `/admin/knowledges/{id}` | Details |
| POST | `/admin/knowledges` | Create/update |
| DELETE | `/admin/knowledges/{id}` | Delete |

### Question–knowledge point associations

| Method | Path | Description |
|------|------|------|
| POST | `/admin/question-knowledges/batch-tag` | Batch tag |
| GET | `/admin/question-knowledges` | Query associations |
| DELETE | `/admin/question-knowledges` | Remove (by questionId) |

### Tag management

| Method | Path | Description |
|------|------|------|
| GET | `/admin/tags` | List/search/sort |
| GET | `/admin/tags/{id}` | Details |
| POST | `/admin/tags` | Create/update |
| DELETE | `/admin/tags/{id}` | Delete (only when unreferenced) |

### Question–tag associations

| Method | Path | Description |
|------|------|------|
| POST | `/admin/question-tags/batch-tag` | Batch tag |
| GET | `/admin/question-tags` | Query associations (by questionId or tagId) |
| DELETE | `/admin/question-tags` | Remove (by questionId, optionally tagId) |

## 3. Path routing

The backend serves the **WebAPI**, the **admin frontend SPA**, and the **health check** on the same HTTP port (5007):

| Path prefix | Served by |
|---------|--------|
| `/admin/*` | WebAPI (question/knowledge point/tag CRUD) |
| `/health/live` | Liveness probe (anonymous, always 200 while serving, never resolves the readiness evidence) |
| `/health/ready`, `/health` | Readiness probe (anonymous; `/health` aliases `/health/ready`; 200/503 with the ServiceMantle JSON envelope — see [`docs/overview/ApiSpec.md`](../../docs/overview/ApiSpec.md) §2.7) |
| `/swagger` | Swagger UI (Development only) |
| `/` and everything else | Admin frontend SPA (Vue Router history fallback) |

The SPA fallback is implemented with ASP.NET Core `MapWhen` (see `Program.cs`) and only applies to the HTTP port; a failed `__APP_TITLE__` injection never makes the SPA unavailable (the original index.html is returned when injection fails). The SPA branch is registered before `UseAuthentication()` / `UseAuthorization()`, so static assets and the SPA fallback are served without authentication; `/admin/*` still requires a valid JWT.

## 4. Request correlation and telemetry

Every response (API, SPA, static assets, `/health`, and error envelopes) carries an `x-correlation-id` header, resolved by the ServiceMantle correlation middleware registered as the first HTTP middleware: a single, shape-valid inbound value (1–64 chars, first char alphanumeric, then alphanumeric/`.`/`_`/`-`) is echoed verbatim; anything else is replaced by a generated 32-char lowercase hex id. The same id and the host identity (`ServiceName=quaestura`, `ServiceVersion`, per-start `InstanceId`) are attached to the request log scope. The base OpenTelemetry instrumentation runs **without any exporter** (telemetry stays in-process) and no bootstrap file is written. See [`docs/development/Deployment.md`](../../docs/development/Deployment.md) §6 for the full contract.

## 5. Docker deployment

### 5.1 Deploying SeaweedFS

Quaestura depends on an S3-compatible object store (such as SeaweedFS, S3 port 8333) for question images. Deploying it is outside the scope of this repository; connection details are injected through the `Oss:*` configuration or Consul KV.

### 5.2 Build and deploy

The Docker image is **a single image containing both the backend and the frontend** (multi-stage build; `Dockerfile` is in the repository root, and the build context is the repository root):

```bash
docker build -t quaestura:latest .
```

Deployment network: `start.sh` uses the `quaestura-net` bridge network by default; when deployed on the same host as the Ruoyu.Study platform, the platform network can be used instead, and dependencies such as SeaweedFS can also be reached through standalone server addresses configured in Consul.

### 5.3 Local development

To change the admin frontend and see the result immediately, build the frontend on its own (dev mode `npm run dev` goes through the Vite proxy 8091 → 5007):

```bash
cd ../frontend
npm install
npm run dev   # Dev mode (Vite dev server + proxy to :5007)
npm run build # Production build (outputs dist/, copied into wwwroot/ by Docker stage 1)
```

Production health snapshots use the shared receipt and PostgreSQL classifier: unfinished/failed startup performs zero database I/O (`pendingSetup`, `health.startup_incomplete` / `health.startup_failed`); connection failures report `health.database_unreachable`; missing mapped objects or revoked SELECT report `health.schema_unavailable`; authentication-class and other unclassified failures report `health.probe_failed` with null state fields. The routes, JSON fields and five-second budget remain unchanged. Non-relational Testing still uses `EnsureCreated` and a minimal tag-query adapter. See [Deployment §2.1](../../docs/development/Deployment.md#21-database-connection) for compatibility, diagnostic changes and rollback.

## Optional official hosted login

`SignaCore.Client.AspNetCore 0.1.11-rc.5` is consumed from NuGet. `AdminOidc:Enabled=false` keeps
legacy login/Bearer/SPA behavior and returns 503 for `/admin/auth/oidc/*`. Enabled deployment needs
Confidential Code+PKCE registration, exact HTTPS callback/PostLogout URIs, a ClientId audience,
and environment/Consul-injected secret; see [Authentication §7](../../docs/development/Authentication.md#7-optional-hosted-administrator-login).
The Host adapts admin admission, JSON responses, route metadata and scheme selection; all protocol,
state, ticket keys/storage/cleanup and logout gates belong to the package. Sessions are single
instance and process-local; restart requires login. Rollback disables the switch and restores the
image, with no database migration. The port remains 5007; frontend migration is separate.
