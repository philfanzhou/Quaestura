# Deployment and Operation

## 1. Quick start

### 1.1 Requirements

1. **Database**: PostgreSQL (injected through the Consul shared configuration)
2. **Object storage**: SeaweedFS (S3 port 8333)
3. **Authentication service**: SignaCore (issues JWTs and distributes public keys; must be reachable for OIDC discovery)
4. **Network port**: HTTP 5007 (fixed inside the container; the host port mapping is controlled by the `Port` variable in `start.sh`)

### 1.2 How to start

#### Option 1: local .NET

```bash
dotnet run --project src/Host --configuration Release
```

#### Option 2: Docker

```bash
# 1. Build the image (the build context is the repository root)
docker build -t quaestura:latest .

# 2. Start the container
./start.sh
```

`start.sh` injects the configuration needed for containerized deployment through environment variables (the database connection is overridden by the Consul shared configuration).

### 1.3 Verify startup

```bash
# Health check
curl http://localhost:5007/health
# Expected: Healthy

# Swagger UI
open http://localhost:5007/swagger
```

## 2. Configuration

### 2.1 Database connection

The database connection string is built by `SharedPostgreSqlConnectionStringFactory.BuildOrFallback`: it first composes the production connection string from the Consul shared configuration (`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`), and falls back to the local `ConnectionStrings:Default` when that is not possible.

`appsettings.json` only keeps a local dev-friendly connection string (no password) and the database name:

```json
{
  "Database": {
    "Name": "quaestura"
  },
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=quaestura;Username=phil"
  }
}
```

The connection string is built by `SharedPostgreSqlConnectionStringFactory.BuildOrFallback`: it first composes the production connection string from the Consul shared configuration (`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`), and falls back to the local `ConnectionStrings:Default` when that is not possible. In production, the PostgreSQL host/port/username/password are overridden by the Consul `PostgreSql:*` keys and do not need to be written to `appsettings.json`.

The database schema is created automatically on startup: `DatabaseInitializer.InitializeAsync` applies the EF Core migrations in `src/Database/Migrations/`, and the raw SQL inside the initializer only recreates missing tables when there are no pending migrations; no manual database setup is required.

#### Overriding through the Consul shared configuration

In production, the following keys are injected through Consul KV (prefix `config/ruoyu`), and `SharedPostgreSqlConnectionStringFactory` composes the connection string from them:

| Consul key | Purpose |
|-----------|------|
| `PostgreSql:Host` | PostgreSQL host |
| `PostgreSql:Port` | PostgreSQL port |
| `PostgreSql:Username` | PostgreSQL username |
| `PostgreSql:Password` | PostgreSQL password |
| `Database:Name` | Database name (default `quaestura`) |

### 2.2 Object storage

```json
{
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

- `InternalEndpoint`: the address the backend actually connects to, in `host:port` format; containers on the same host can use `ruoyu-seaweedfs:8333`, a standalone server uses its IP and published port
- `InternalSecure`: whether the internal connection uses HTTPS
- `PublicBaseUrl`: the public pre-signed base URL used by browsers; the `/oss` entry point is currently proxied by the User Web Nginx
- `AccessKey` / `SecretKey`: S3 credentials

OSS connectivity is checked automatically on startup (`OssService.CheckConnectivityAsync` in `Program.cs`); a failure logs a Warning but does not block startup.

### 2.3 Authentication service (JWT)

The service authenticates with JWT Bearer tokens and trusts JWTs issued by SignaCore. See [Authentication.md](./Authentication.md) for details.

```json
{
  "IdentityService": {
    "Authority": "https://identity.test.ruoyu.study",
    "Issuer": "https://identity.test.ruoyu.study",
    "AdditionalValidIssuers": ["QuantumZhou.Identity"],
    "Audience": "QuantumZhou.microservices",
    "RequireHttpsMetadata": true,
    "ClockSkewSeconds": 30
  }
}
```

- `Authority`: base URL of the Identity service, used for OIDC discovery (fetches `/.well-known/openid-configuration` and `/.well-known/jwks`)
- `Issuer`: exact issuer of new tokens; during a migration the old value only goes into `AdditionalValidIssuers`
- `Audience`: expected audience; must be configured explicitly
- `RequireHttpsMetadata`: defaults to `true`; set to `false` only when the SignaCore database settings also explicitly allow HTTP, never inferred from the environment name or address
- `ClockSkewSeconds`: tolerance for token time validation

**Required**: Authority, Issuer, Audience, RequireHttpsMetadata, and ClockSkewSeconds must together form a complete trust snapshot; missing configuration, or HTTP without an explicit opt-in, makes the service fail to start.

Signing public keys are fetched automatically through OIDC discovery; no keys need to be configured manually.

### 2.4 Admin login (SignaCore application)

Admin login (`POST /admin/auth/login`, see [Authentication.md §6](./Authentication.md#6-admin-login)) needs Quaestura registered as an application in SignaCore:

1. Register a Quaestura application in SignaCore and obtain its AppId and AppSecret.
2. Set the application's callback URL to `<Quaestura address reachable from SignaCore>/admin/auth/callback`. The address must satisfy SignaCore's callback policy (`Callback:AllowedDomains`, `Callback:RequireHttps`, `Callback:AllowPrivateAddresses`; see SignaCore `docs/development/Configuration.md`). Plain-HTTP or private-address callbacks must be enabled explicitly in SignaCore.
3. Inject the credentials and the admin whitelist through environment variables or Consul, never through committed files:

| Configuration key | Environment variable | Notes |
|--------|---------|------|
| `IdentityService:AppId` | `IdentityService__AppId` | `start.sh` passes `IDENTITY_APP_ID` |
| `IdentityService:AppSecret` | `IdentityService__AppSecret` | `start.sh` passes `IDENTITY_APP_SECRET` |
| `AdminPortal:AdminUserIds` | `AdminPortal__AdminUserIds__0`, `__1`, ... | SignaCore user IDs that receive the `admin` role; Consul KV works as well |

Without AppId/AppSecret the service still starts and every other endpoint works; login returns 503. Provide TLS between browsers and Quaestura (or keep it on an internal network), because passwords pass through it.

## 3. Docker deployment

### 3.1 Image build (3-stage multi-stage build)

The Dockerfile is `Dockerfile` in the repository root. **A single image contains both the backend (.NET 10 ASP.NET Core) and the frontend (Vue 3 build output)**:

| Stage | Base image | Purpose |
|------|--------|------|
| 1. `frontend-build` | `node:20-alpine` | Builds the Vue 3 frontend and outputs `dist/` |
| 2. `build` | `mcr.microsoft.com/dotnet/sdk:10.0` | Restores and publishes the .NET Host, **copying `dist/` from stage 1 into `Host/wwwroot/`** |
| 3. `final` | `mcr.microsoft.com/dotnet/aspnet:10.0` | Runtime image containing only the .NET runtime and the published output |

**Key points**:
- The build context is the repository root (aligned with the 3-stage build of the `Identity` service)
- Stage 1 is independent: a frontend build failure does not pollute the backend image
- `COPY --from=frontend-build /app/dist .../Host/wwwroot` in stage 2 is the key line that injects the Vite output into the ASP.NET Core default web root

### 3.2 Start

```bash
./start.sh
```

The container uses the `quaestura-net` network to reach services on the same host; SeaweedFS can also be reached through a standalone server IP and port configured in Consul.

### 3.3 Environment variables

`start.sh` injects the following environment variables:

| Variable | Value |
|------|-----|
| `TZ` | `Asia/Shanghai` |
| `CONSUL_HTTP_ADDR` | Consul address; the OSS configuration is loaded from `config/ruoyu/shared.json` |
| `CONSUL_TOKEN` | Consul ACL token |
| `IdentityService__Authority` | Usually not injected by `start.sh`; the stable HTTPS Authority is read from Consul |
| `IdentityService__AppId` | `${IDENTITY_APP_ID:-}`; SignaCore AppId for admin login (see §2.4) |
| `IdentityService__AppSecret` | `${IDENTITY_APP_SECRET:-}`; SignaCore AppSecret for admin login (see §2.4) |

> The HTTP listen port is fixed at 5007 (hardcoded in `Program.cs`) and is no longer controlled by the `ASPNETCORE_URLS` environment variable. The host port mapping is controlled by the `Port` variable in `start.sh` (`-p ${Port}:5007`).
>
> The database connection is no longer injected through the `ConnectionStrings__Default` environment variable; it is composed at runtime from the Consul shared configuration (`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`).

`start.sh` runs the locally built `quaestura:latest` by default; to use a published image, set `IMAGE_REPO` and `IMAGE_TAG`:

```bash
docker pull ghcr.io/philfanzhou/quaestura:0.1.0
IMAGE_REPO=ghcr.io/philfanzhou/quaestura IMAGE_TAG=0.1.0 ./start.sh
```

### 3.4 Prebuilt images and releases

GitHub Actions (`.github/workflows/ci.yml`) runs the build, tests, and image build on PRs, pushes to `main`, and release tags; only pushes to `main` and tags publish images to `ghcr.io/philfanzhou/quaestura`.

| Trigger | Published image tags | GitHub Release |
|------|---------------|----------------|
| Merge to `main` | `edge` (moves with the latest commit; not a formal release) | None |
| `MAJOR.MINOR.PATCH` tag | `MAJOR.MINOR.PATCH`, `MAJOR.MINOR`, `latest` | Final release, marked as latest |
| `MAJOR.MINOR.PATCH-rc.NUMBER` tag | Only `MAJOR.MINOR.PATCH-rc.NUMBER` | Pre-release; does not move `MAJOR.MINOR` or `latest` |

Releases are driven entirely by pushing tags; do not create Releases or push images manually. Tags have no `v` prefix and are placed on commits already merged into `main`:

```bash
git switch main && git pull
git tag -a 0.1.0 -m "Quaestura 0.1.0"
git push origin 0.1.0
```

Release candidates use the `-rc.NUMBER` suffix, for example `git tag -a 0.1.0-rc.1 -m "Quaestura 0.1.0-rc.1"`. Tags in any other format are rejected by CI.

After a tag is pushed, the full `Build & Test` runs first; once it passes, the following run in order:

1. **Publish GHCR Image**: builds and pushes the image with provenance and SBOM.
2. **Publish GitHub Release**: creates a Release for the tag, records the digest of the image actually published, and attaches the changelog generated by GitHub.

A Release is only created for tags whose tests passed and whose image can already be pulled. If the pipeline fails for a tag, neither the image nor the Release is published; fix the cause and tag a new version instead of moving the failed tag. Re-running the pipeline for a tag that already has a Release does not overwrite manually edited Release notes.

Every `edge` push leaves the old manifest behind as an untagged package version in GHCR. GHCR does not clean these up automatically; delete them manually in the package settings when needed.

## 4. Port allocation

| Port | Protocol | Purpose |
|------|------|------|
| 5007 (fixed inside the container) | HTTP | WebAPI entry point + Swagger UI (Development only) + health check `/health` + admin frontend SPA |
| Host-mapped port | — | Port mapped on the host to reach the container (`Port` variable in `start.sh`, `-p ${Port}:5007`) |

## 5. Logging

Startup log format:

```
Quaestura Service starting
Listening: http://+:5007
Database: PostgreSQL <Host>:<Port>/<Database>
Effective configuration diagnostics: PostgreSqlHost=..., PostgreSqlPort=..., PostgreSqlUsername=..., PostgreSqlPassword=..., DatabaseName=...
OSS: <InternalEndpoint>/<Bucket>
```

The database is always PostgreSQL, and the connection string is built by `SharedPostgreSqlConnectionStringFactory.BuildOrFallback`. The startup log also prints the Effective configuration diagnostics, reflecting the actual `PostgreSql:*` and `Database:Name` values injected by Consul (with the password redacted).

## 6. Request correlation and telemetry

The host registers the ServiceMantle composition (`src/Host/ServiceMantleComposition.cs`): a fixed service identity, the request correlation middleware as the first HTTP middleware, and the base OpenTelemetry instrumentation with **no exporter**.

### 6.1 Correlation ID

Every HTTP response — API success and error envelopes (400/401/403/500 included), the SPA and static assets, dev Swagger, and `/health` — carries an `x-correlation-id` response header:

- An inbound `x-correlation-id` request header is reused verbatim only when it is the **single** header value, is 1–64 characters long, starts with an ASCII letter or digit, and contains only ASCII letters, digits, `.`, `_`, or `-`.
- Missing, empty, whitespace, illegal, comma-joined, repeated, or overlong inputs are discarded as a whole (never trimmed or partially reused) and replaced by a generated 32-character lowercase hexadecimal id. Rejected values are not logged.
- The same resolved id is published to the response header and to the request log scope (`CorrelationId` field), so log lines emitted while handling the request can be joined with the caller's value.
- The id is a log-correlation value only: it is not unique, unguessable, or authenticated, and must never be used for authorization, idempotency, or replay protection.

### 6.2 Service identity and log scope

The ServiceMantle service id is `quaestura` (lowercase, deliberately distinct from the fixed Loki stream label `Quaestura`). The instance id is `quaestura-{32-hex}` and is regenerated on every host start; it is not a persistent identity. The service version resolves from the entry assembly informational version. Request scopes carry `ServiceName`, `ServiceVersion`, `InstanceId`, and `CorrelationId`. The existing Serilog Console/Loki pipeline is unchanged and coexists with these scopes.

### 6.3 Telemetry and sensitive headers

The base instrumentation (ASP.NET Core incoming requests, outgoing `HttpClient` calls, .NET runtime metrics) is registered without any exporter, so telemetry stays in-process and no external collector endpoint is contacted. No bootstrap file is written (`quaestura.bootstrap.json` never appears), and no installation state is tracked. The safe request-header projector denies `X-Admin-AppSecret` in addition to the built-in authentication, cookie, and API-key header names; no automatic request-header logging is enabled.

Reverting this wiring removes the response header and the observability registrations; there is no schema, migration, or persisted state to roll back.

## 7. Dependencies

| Dependency | Required | Behavior on startup failure |
|------|----------|--------------|
| PostgreSQL | Required | The service fails to start if the connection fails (`SharedPostgreSqlConnectionStringFactory` cannot build a connection string or the database is unreachable) |
| SeaweedFS | Yes (image features) | Starts successfully; image features are unavailable and a Warning is logged |
| SignaCore | Yes (JWT validation) | Starts successfully, but every endpoint that requires authentication returns 401 (OIDC discovery fails, so the signing keys cannot be fetched) |
