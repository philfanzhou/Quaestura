# Quaestura

Quaestura is a self-hosted question bank and knowledge-point catalog service. It manages reusable questions, their content and images, a hierarchical knowledge-point tree, and free-form tags, and serves a Vue 3 administration console from the same process as its HTTP API.

Learner-specific data such as mistake records, root-cause analysis, and practice history is out of scope; Quaestura only owns the reusable catalog. See [CONTEXT.md](CONTEXT.md) for the domain language.

## Capabilities

- Question management with multipart upload of text and images, search, filtering, and paging
- Hierarchical knowledge points organized by subject and grade
- Tags maintained by staff, with usage counts
- Many-to-many links between questions and knowledge points, and between questions and tags
- Question images in S3-compatible object storage (for example SeaweedFS) under the `questions/` prefix
- RS256 JWT authentication against SignaCore or any issuer that publishes OIDC discovery and JWKS
- Request correlation IDs (`x-correlation-id` on every response) and base OpenTelemetry instrumentation with no exporter
- Optional configuration from Consul KV, with a shared sanitizing logging pipeline (Console output plus optional Grafana Loki export)
- A single container image that serves the API and the admin console on port 5007

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Host` | ASP.NET Core host: composition, authentication, Consul, logging, and SPA hosting |
| `src/Service` | Minimal API endpoints and request validation |
| `src/Domain` | Domain services |
| `src/Database` | EF Core model and migrations for PostgreSQL |
| `src/Common` | Shared authentication, database, and object-storage helpers |
| `src/Consul` | Consul KV configuration source, shared PostgreSQL connection-string factory, and startup diagnostics formatting |
| `src/Tests` | Unit and endpoint tests |
| `frontend` | Vue 3 administration console |
| `docs` | API, database, authentication, deployment, and testing documentation |

Project references flow one way: Host → Service → Domain → Database → Common.

## Build and test

Requirements: the .NET SDK specified in [global.json](global.json), Node.js 20+ with npm, and Docker to build the container image.

```bash
dotnet build src/Quaestura.sln --configuration Release
dotnet test src/Quaestura.sln --configuration Release --no-build
cd frontend && npm ci && npm run build
```

## Run locally

Quaestura needs PostgreSQL, S3-compatible object storage, and a reachable token issuer. Configure the database connection, the `Oss` section, and the `IdentityService` section in `src/Host/appsettings.Development.json` or through environment variables, then run:

```bash
dotnet run --project src/Host
```

The service listens on `http://localhost:5007`. Useful endpoints include the health probes `/health/live` (liveness) and `/health/ready`/`/health` (readiness), `/swagger` (Development only), the API under `/admin/*`, and the admin console at `/`. The database itself (when missing and `Database:AllowCreate` permits, which is the default) and its tables are created on first start.

For frontend work, `npm run dev` in `frontend/` starts the Vite dev server and proxies API calls to port 5007.

## Container

The root `Dockerfile` builds the admin console and the .NET host into one image:

```bash
docker build -t quaestura:latest .
./start.sh
```

`start.sh` runs the container on the `quaestura-net` network and passes `CONSUL_HTTP_ADDR` and `CONSUL_TOKEN` through, so database and object-storage settings can come from Consul KV. The listen port inside the container is fixed at 5007.

## Configuration

| Section | Purpose |
| --- | --- |
| `ConnectionStrings:Default`, `PostgreSql:*`, `Database:Name` | PostgreSQL connection; the shared `PostgreSql:*` keys take precedence when present |
| `Database:AllowCreate` | Whether startup may create a verifiably missing target database (default `true`; `false` refuses startup with zero writes) |
| `Oss:*` | S3 endpoint, credentials, bucket, and public base URL for presigned links |
| `IdentityService:*` | Token issuer authority, issuer, audience, HTTPS metadata requirement, and clock skew |
| `APP_TITLE` | Title shown by the admin console |

Supply credentials through environment variables or Consul KV, never through committed files. See [deployment](docs/development/Deployment.md) and [authentication](docs/development/Authentication.md) for the full reference.

## Documentation

Start with [the documentation index](docs/README.md), [the API specification](docs/overview/ApiSpec.md), and [deployment](docs/development/Deployment.md). Some documents inherited from the Ruoyu.Study monorepo are still in Chinese; their translation is tracked in [#3](https://github.com/philfanzhou/Quaestura/issues/3).

Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md), and vulnerabilities should be reported through [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)

Database startup and readiness consume ServiceMantle 0.3.1: the direct startup gate retains default missing-database creation and real multi-instance migration locking, while scoped MappedSchema health probes verify all EF-mapped tables and columns without reading rows or writing data. Startup and schema health classifications changed; see [Deployment](docs/development/Deployment.md#21-database-connection) and [API health probes](docs/overview/ApiSpec.md#27-health-probes-healthlive-healthready-health) for monitoring and rollback guidance.
