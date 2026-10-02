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
# Liveness probe (always 200 while the process serves requests; never touches the database)
curl http://localhost:5007/health/live
# Expected: {"status":"live"}

# Readiness probe (200 only after startup initialization succeeded and the database
# answered this request's read-only probe; 503 otherwise)
curl http://localhost:5007/health/ready
# Expected: {"status":"ready","phase":"completed","migrationStatus":"succeeded","databaseStatus":"reachable","errorCode":null}

# Swagger UI
open http://localhost:5007/swagger
```

`GET /health` is an alias of `/health/ready`. The probe contract — anonymous access, the JSON envelope, and the fail-closed 503 classifications (`health.startup_incomplete`, `health.startup_failed`, `health.database_unreachable`, `health.schema_unavailable`, `health.probe_failed`, `health.probe_timeout`) — is specified in [`docs/overview/ApiSpec.md` §2.7](../overview/ApiSpec.md#27-health-probes-healthlive-healthready-health). Orchestration liveness checks should use `/health/live`; readiness/load-balancer checks should use `/health/ready` or `/health`. The former fixed `200 {"status":"Healthy"}` body no longer exists.

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

Database startup directly invokes the shared ServiceMantle 0.3.0 `StartupDatabaseGate` before the OSS check and before listening; no hosted gate runner is registered. Deployment support is independently declared as `SingleAndMultiInstance`, and the gate explicitly selects `MultiInstance` (never a lock-free fallback). It records the process-local `StartupDatabaseReceipt` only after the full gate completes. **Stage 1 — target preparation** (the shared PostgreSQL provider): the target database is observed first, and a connectable target is used as-is — no maintenance-database connection, no `CREATE`, and no `CREATEDB` or `postgres`-database privilege is required for existing databases. Only a verifiably missing target may be created, only when `Database:AllowCreate` permits it, and only through the shared provider with a fixed 30-second preparation budget (there is deliberately no timeout setting). The maintenance connection is a copy of the original configuration with just the database changed to `postgres`; no additional administrative secret is introduced or persisted. After a successful preparation the target must observe as connectable again with its own identity before stage 2 starts. Unreachable servers, authentication or permission failures, and owner or server-identity conflicts refuse startup without any creation fallback. Cancellation or timeout stops startup before any table initialization; an already created empty database is kept and reused on the next start — it is never dropped as compensation. Preparation and migration are not one atomic transaction; committed effects survive subsequent failure. To roll back this package consumption, stop the upgraded instances and restart the previous image; keep legitimate databases and history, never run `Down` or `DROP DATABASE`.

**Safe configuration and failure diagnostics**: a missing/blank `Database:AllowCreate` still means `true`, and valid booleans retain their case-insensitive parsing. Invalid booleans or missing/invalid connection configuration fail before database I/O with `database_target_preparation.invalid_target`, without echoing submitted values. `database_target_preparation.creation_not_allowed` keeps its spelling and uses the shared allowlisted constant. The former `database_target_preparation.invalid_configuration` and `database_target_preparation.post_preparation_unreachable` classifications are replaced by `database_target_preparation.invalid_target` and `database_target_preparation.not_connectable_after_preparation`. Failed gates never start the listener; cancellation never claims success. No new configuration, credential, or schema migration is required.

**Health evidence change in 0.3.0**: production uses the shared scoped `EfCoreHealthSnapshotSource<QuaesturaDbContext>` in explicit `MappedSchema` mode with the PostgreSQL failure classifier and the `health` code prefix. Before startup succeeds it performs zero database I/O and reports `pendingSetup` plus `health.startup_incomplete` or `health.startup_failed`. After success, each request uses an independent context to issue zero-row SELECTs over every EF-mapped table and column; it tracks no entities and writes nothing. Connection failures return `health.database_unreachable`; missing tables/columns and revoked read permissions return `health.schema_unavailable` (`completed` / `failed` / `reachable`) without changing the process receipt. Unclassified errors, including authentication-class failures, become `health.probe_failed` with null state fields at the HTTP endpoint. The five-second endpoint budget, routes, JSON fields and 200/503 decision remain unchanged. This is stronger evidence and a deliberate diagnostic change from the old tag-only probe, not complete behavioral equivalence. It does not validate constraints, indexes, data correctness, S3, or future availability. Monitoring should recognize the new classifications; rollback restores the previous probe and codes.

**Stage 2 — migration orchestration under the advisory lock**: the database schema is created and verified automatically on startup. The shared ServiceMantle `DatabaseMigrationOrchestrator` first acquires a real PostgreSQL **session advisory lock** derived from the service id `quaestura`, then runs a strict flow entirely under that single authority: a **read-only inspection** classifies the target database, migrations execute **only** for the verified `Empty`/`PendingMigration` states (the consuming executor `QuaesturaMigrationExecutor` owns the inspection/execution contract), and a **final inspection** must report `CurrentVersionCompatible` before success is reported and the host may start listening. Unknown or corrupt databases are never silently stamped or auto-repaired. The lock-acquire budget is fixed at **30 seconds** (there is deliberately no configuration for it); it bounds only waiting for the lock, never the execution itself. A second instance starting concurrently waits for the lock, then re-reads the state, sees `CurrentVersionCompatible`, and starts without executing anything, so concurrently starting containers converge to exactly one migration execution. A failed inspection, execution, or final check, a lost lock lease, or a lock-acquire timeout refuses startup through stable `migration.*` error classifications, and the lock is always released for the next session. The non-relational `Testing` path skips target preparation and the lock entirely and keeps using `EnsureCreated`.

| Observed state | Classification | Startup behavior |
|------|------|------|
| No business tables and no migration history (or the target database does not exist yet) | `Empty` | EF Core runs both original migrations (`20260504115924_InitialCreate`, `20260926094240_AddTags`) |
| Applied history is a known ordered prefix and the schema verifiably matches that version | `PendingMigration` | EF Core runs the remaining migrations and the result is re-verified |
| Full known history and the complete current schema verify | `CurrentVersionCompatible` | Nothing is written |
| History contains migration ids this application does not know | `VersionTooNew` | Startup is refused; deploy a version that knows the history or restore a supported backup |
| Missing/extra columns, wrong types or nullability, missing/renamed PK/FK/indexes, partial table sets, or history that contradicts the schema | `InspectionFailed` | Startup is refused with zero writes; repair the structure from a backup and restart |

**Limited legacy takeover**: a database created by the historical `EnsureCreated` path (no migration history) is taken over only when its four initial tables (`knowledge`, `question`, `question_content`, `question_knowledge`) match the `InitialCreate` version exactly and the two tag tables (`tag`, `question_tag`) are either both absent or both match `AddTags` exactly. The takeover registers only the verified `InitialCreate` baseline in an independent parameterized transaction and then really executes `AddTags`; existing business data (including tag usage counts) is preserved. Constraint and index names are compared case-insensitively, but their semantics must match exactly; unrelated non-business tables may coexist.

**Cancellation and recovery**: every database operation observes the host shutdown token, and caller cancellation leaves the shared receipt `Running`, propagates on the original token without recording a business failure or success, and outranks any lock-timeout or lease-loss classification. The baseline commit and the remaining EF migrations use separate transactions, so a cancellation between phases leaves only the real committed baseline; the next startup re-acquires the lock, re-verifies the legitimate pending state, and completes the recovery. An already-committed migration is never rolled back by cancelling startup.

**Multi-instance and mixed-version rollout**: the advisory lock serializes migration only among instances that use it. When upgrading from a pre-lock version, stop the old instances before the new ones start (the old unlock orchestration is not covered by the lock); running old and new versions concurrently during a migration is not a supported state. Normal multi-instance starts of the current version are safe: the lock holder executes, every other instance waits, re-inspects, and skips.

**Rollback**: rolling the application back never deletes legitimate migration history and never executes `Down` migrations. Rolling back the orchestration itself means deploying the previous single-instance startup order — stop the upgraded instances first, then start the previous version; mixed old/new operation during a migration is not lock-protected. If the previous lenient behavior is truly required, stop writes and restore a full database backup first; the old version must not be used as a schema repair tool.

#### Database:AllowCreate

| Configuration key | Environment variable | Default | Purpose |
|--------|---------|------|------|
| `Database:AllowCreate` | `Database__AllowCreate` | `true` | Whether startup may create the target database when it is verifiably missing. `false` refuses startup on a missing target with zero writes; an unparsable boolean fails startup. When creation is needed, the configured user requires the `CREATEDB` attribute and permission to connect to the `postgres` maintenance database; existing databases only need their own access privileges. |

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
| 5007 (fixed inside the container) | HTTP | WebAPI entry point + Swagger UI (Development only) + health probes `/health/live`, `/health/ready`, `/health` + admin frontend SPA |
| Host-mapped port | — | Port mapped on the host to reach the container (`Port` variable in `start.sh`, `-p ${Port}:5007`) |

## 5. Logging

Logging runs through the shared ServiceMantle pipeline (`ServiceMantle.Logging`, pinned to `0.3.0`), registered by `AddQuaesturaLogging` in `src/Host/ServiceMantleComposition.cs`. It is the single logging entry point: a mandatory-sanitizing Serilog Console pipeline plus the opt-in Grafana Loki remote sink behind the same sanitizer. There is no second, unsanitized provider, and sanitization cannot be disabled through configuration. The former local `AddRuoyuLokiSink`/`UseRuoyuSerilog` wiring and the `Serilog` configuration section are gone.

Levels keep the previous contract: `Information` by default, with `Warning` overrides for `Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore.Database.Command`. Console and Loki observe the same filtered, sanitized events.

Startup log format:

```
Quaestura Service starting
Listening: http://+:5007
Database: PostgreSQL <Host>:<Port>/<Database>
Effective configuration diagnostics: PostgreSqlHost=..., PostgreSqlPort=..., PostgreSqlUsername=..., PostgreSqlPassword=..., DatabaseName=...
OSS: <InternalEndpoint>/<Bucket>
```

The database is always PostgreSQL, and the connection string is built by `SharedPostgreSqlConnectionStringFactory.BuildOrFallback`. The startup log also prints the Effective configuration diagnostics, reflecting the actual `PostgreSql:*` and `Database:Name` values injected by Consul (with the password redacted).

### 5.1 Structured identity

Startup logs run inside an explicit `ServiceLogContext` scope; request logs receive the same identity fields plus `CorrelationId` from the correlation middleware's request scope (§6.1). Compared with the former local pipeline, the structured `ServiceName` field is now the lowercase service id `quaestura` (was `Quaestura`), `ServiceVersion` resolves from the entry assembly informational version (was a fixed `1.0.0`), and `InstanceId` is the per-host `quaestura-{32-hex}` value (was the machine name). The old global `MachineName`/`ThreadId` enrichers are removed. The Loki **stream label** `service=Quaestura` is unchanged and is deliberately distinct from the lowercase structured field.

### 5.2 Grafana Loki export

- `Loki:Uri` (appsettings, environment, or Consul KV) stays the only remote-sink input. An empty or whitespace value disables the remote sink entirely: Console only, zero remote requests. The sink is never re-enabled from any legacy `Serilog:WriteTo` configuration.
- Every stream carries exactly the fixed non-secret labels `service=Quaestura` and the sink-owned `level`. Request, user, and instance values are structured log fields, never stream labels.
- `Loki:AllowInsecureHttp` defaults to `true` as the explicit continuation of the existing trusted-network HTTP deployment contract; set it to `false` to require HTTPS. Network trust is never inferred from the host name. Plain HTTP provides no TLS confidentiality for log content.
- Invalid endpoint shapes (relative URI, userinfo, query, fragment, or HTTP when not allowed) fail host startup with a stable error code (`loki.invalid_endpoint`) and never echo the submitted value.
- Delivery is best effort: a bounded in-memory queue with asynchronous batch retries. A Loki outage never blocks business requests, shutdown draining is bounded (`ShutdownDrainTimeout`, 5 s by default), and delivery is neither lossless nor exactly-once; a SIGKILL loses unflushed events.

### 5.3 Sanitization boundary

Structured field values are sanitized before reaching either sink: fields whose names contain sensitive fragments (`password`, `secret`, `token`, `apikey`, `connectionstring`, `credential`, `authorization`, `cookie`, and friends) are replaced with `[REDACTED]`; database connection and builder objects, HTTP message/content/header objects, and certificates are never deconstructed; exceptions keep only their type structure (messages, stack traces, and `Data` are not emitted). Free-text sanitization (secret assignments, credential URIs, connection strings, bearer/JWT-like values, PEM key blocks) is best effort: never interpolate secrets into message text. `Console.WriteLine` calls made before the logging registration are not protected by the shared sink.

### 5.4 Rollback

Reverting the code and restoring the previous Serilog configuration section is sufficient; existing Loki data needs no migration and no database or API surface is involved.

## 6. Request correlation and telemetry

The host registers the ServiceMantle composition (`src/Host/ServiceMantleComposition.cs`): a fixed service identity, the request correlation middleware as the first HTTP middleware, and the base OpenTelemetry instrumentation with **no exporter**.

### 6.1 Correlation ID

Every HTTP response — API success and error envelopes (400/401/403/500 included), the SPA and static assets, dev Swagger, and `/health` — carries an `x-correlation-id` response header:

- An inbound `x-correlation-id` request header is reused verbatim only when it is the **single** header value, is 1–64 characters long, starts with an ASCII letter or digit, and contains only ASCII letters, digits, `.`, `_`, or `-`.
- Missing, empty, whitespace, illegal, comma-joined, repeated, or overlong inputs are discarded as a whole (never trimmed or partially reused) and replaced by a generated 32-character lowercase hexadecimal id. Rejected values are not logged.
- The same resolved id is published to the response header and to the request log scope (`CorrelationId` field), so log lines emitted while handling the request can be joined with the caller's value.
- The id is a log-correlation value only: it is not unique, unguessable, or authenticated, and must never be used for authorization, idempotency, or replay protection.

### 6.2 Service identity and log scope

The ServiceMantle service id is `quaestura` (lowercase, deliberately distinct from the fixed Loki stream label `Quaestura`). The instance id is `quaestura-{32-hex}` and is regenerated on every host start; it is not a persistent identity. The service version resolves from the entry assembly informational version. Request scopes carry `ServiceName`, `ServiceVersion`, `InstanceId`, and `CorrelationId`. These scopes flow through the shared ServiceMantle logging pipeline (§5): startup logs receive the identity fields from an explicit `ServiceLogContext` scope, and request logs from the correlation middleware's request scope.

### 6.3 Telemetry and sensitive headers

The base instrumentation (ASP.NET Core incoming requests, outgoing `HttpClient` calls, .NET runtime metrics) is registered without any exporter, so telemetry stays in-process and no external collector endpoint is contacted. No bootstrap file is written (`quaestura.bootstrap.json` never appears), and no installation state is tracked. The safe request-header projector denies `X-Admin-AppSecret` in addition to the built-in authentication, cookie, and API-key header names; no automatic request-header logging is enabled.

Reverting this wiring removes the response header and the observability registrations; there is no schema, migration, or persisted state to roll back.

## 7. Dependencies

| Dependency | Required | Behavior on startup failure |
|------|----------|--------------|
| PostgreSQL | Required | The service fails to start if the connection fails (`SharedPostgreSqlConnectionStringFactory` cannot build a connection string or the database is unreachable) |
| SeaweedFS | Yes (image features) | Starts successfully; image features are unavailable and a Warning is logged |
| SignaCore | Yes (JWT validation) | Starts successfully, but every endpoint that requires authentication returns 401 (OIDC discovery fails, so the signing keys cannot be fetched) |

## Optional hosted-login rollout

The host consumes official NuGet `SignaCore.Client.AspNetCore 0.1.11-rc.5`. Hosted login is
**off by default**. Follow [Authentication §7](./Authentication.md#7-optional-hosted-administrator-login)
for Confidential/Code/PKCE registration, PerApplication audience migration, exact callback and
PostLogout URIs, and every `AdminOidc` key. Inject `AdminOidc__ClientSecret` through environment
or Consul, never image layers or committed settings. `start.sh` does not inject these optional
keys: use the existing Consul configuration source or explicit container environment injection.

Use one replica and HTTPS termination; the container still listens on **5007**. Keep forwarding
and public URIs consistent with the registered HTTPS origin. Do not log raw OIDC query URLs at
reverse proxies. Session/pending/logout-return storage is process-local and is lost on restart.
No database or object-storage migration is needed. Rollback disables `AdminOidc:Enabled` and
restores the previous image; users log in again. Existing Bearer API clients and the legacy admin
frontend remain available until their separately tracked migration. This RC adds a client package;
it does not itself perform the real SignaCore deployment acceptance.
