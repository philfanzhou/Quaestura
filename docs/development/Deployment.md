# 部署与运行

## 1. 快速启动

### 1.1 启动要求

1. **数据库**：PostgreSQL（由 Consul 共享配置注入）
2. **对象存储**：SeaweedFS（S3 端口 8333）
3. **认证服务**：SignaCore（用于 JWT 签发与公钥分发，必须可达以完成 OIDC discovery）
4. **网络端口**：HTTP 5007（容器内固定，host 映射端口由 `start.sh` 的 `Port` 变量控制）

### 1.2 启动方式

#### 方式一：本地 .NET 启动

```bash
dotnet run --project src/Host --configuration Release
```

#### 方式二：Docker 启动

```bash
# 1. 构建镜像（build context 为仓库根目录）
docker build -t quaestura:latest .

# 2. 启动容器
./start.sh
```

`start.sh` 通过环境变量注入容器化部署所需的配置（数据库连接由 Consul 共享配置覆盖）。

### 1.3 验证启动

```bash
# 健康检查
curl http://localhost:5007/health
# 预期：Healthy

# Swagger UI
open http://localhost:5007/swagger
```

## 2. 配置项

### 2.1 数据库连接

数据库连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 统一构建：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成生产连接串，无法合成时回退到本地 `ConnectionStrings:Default`。

`appsettings.json` 中仅保留本地 dev 友好的连接串（无密码）与数据库名：

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

连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成生产连接串，无法合成时回退到本地 `ConnectionStrings:Default`。生产环境的 PostgreSQL 主机/端口/账号/密码由 Consul 的 `PostgreSql:*` 键覆盖，无需写入 `appsettings.json`。

数据库表由 `DatabaseInitializer` 启动时自动创建（`CREATE TABLE IF NOT EXISTS`），无需手动建库。

#### 通过 Consul 共享配置覆盖

生产环境通过 Consul KV（`config/ruoyu` 前缀）注入以下键，由 `SharedPostgreSqlConnectionStringFactory` 合成连接串：

| Consul 键 | 作用 |
|-----------|------|
| `PostgreSql:Host` | PostgreSQL 主机 |
| `PostgreSql:Port` | PostgreSQL 端口 |
| `PostgreSql:Username` | PostgreSQL 用户名 |
| `PostgreSql:Password` | PostgreSQL 密码 |
| `Database:Name` | 数据库名（默认 `quaestura`） |

### 2.2 对象存储

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

- `InternalEndpoint`：后端实际连接地址，格式为 `host:port`；同机容器可用 `ruoyu-seaweedfs:8333`，独立服务器使用 IP 和发布端口
- `InternalSecure`：内部连接是否使用 HTTPS
- `PublicBaseUrl`：浏览器使用的公共预签名基础 URL；当前 `/oss` 入口由 User Web Nginx 代理
- `AccessKey` / `SecretKey`：S3 凭证

启动时会自动检测 OSS 连通性（`Program.cs` 中的 `OssService.CheckConnectivityAsync`），失败时输出 Warning 但不阻断启动。

### 2.3 认证服务（JWT）

服务通过 JWT Bearer Token 认证，信任由 SignaCore 签发的 JWT。详见 [Authentication.md](./Authentication.md)。

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

- `Authority`：Identity 服务基础 URL，用于 OIDC discovery（拉取 `/.well-known/openid-configuration` 和 `/.well-known/jwks`）
- `Issuer`：新 Token 的精确签发者；迁移期间旧值只放在 `AdditionalValidIssuers`
- `Audience`：预期 audience，必须显式配置
- `RequireHttpsMetadata`：默认 `true`；只有 SignaCore 数据库设置也显式允许 HTTP 时才设为 `false`，不按环境名或地址推断
- `ClockSkewSeconds`：Token 时间校验容差

**必需**：Authority、Issuer、Audience、RequireHttpsMetadata 与 ClockSkewSeconds 都必须形成完整信任快照；缺失配置或 HTTP 未显式 opt-in 会使服务启动失败。

签名公钥通过 OIDC discovery 自动获取，无需手动配置密钥。

## 3. Docker 部署

### 3.1 镜像构建（4 阶段多阶段构建）

Dockerfile 位于仓库根目录 `Dockerfile`，**单个镜像同时包含 backend（.NET 8 ASP.NET Core）+ frontend（Vue 3 构建产物）**：

| 阶段 | 基镜像 | 作用 |
|------|--------|------|
| 1. `frontend-build` | `node:20-alpine` | 构建 Vue 3 frontend，输出 `dist/` |
| 2. `build` | `mcr.microsoft.com/dotnet/sdk:8.0` | 还原 + 发布 .NET Host，**把阶段 1 的 `dist/` 复制到 `Host/wwwroot/`** |
| 3. `final` | `mcr.microsoft.com/dotnet/aspnet:8.0` | 运行时镜像，仅含 .NET 运行时 + 发布产物 |

**关键点**：
- build context 是仓库根目录（与 `Identity` 服务的 3 阶段构建对齐）
- 阶段 1 独立：frontend 构建失败不会污染 backend 镜像
- 阶段 2 的 `COPY --from=frontend-build /app/dist .../Host/wwwroot` 是把 Vite 产物注入 ASP.NET Core 默认 web root 的关键一行

### 3.2 启动

```bash
./start.sh
```

容器使用 `quaestura-net` 网络访问同机服务；SeaweedFS 也可通过 Consul 配置的独立服务器 IP 和端口访问。

### 3.3 环境变量

`start.sh` 注入以下环境变量：

| 变量 | 值 |
|------|-----|
| `TZ` | `Asia/Shanghai` |
| `CONSUL_HTTP_ADDR` | Consul 地址；OSS 配置从 `config/ruoyu/shared.json` 加载 |
| `CONSUL_TOKEN` | Consul ACL Token |
| `IdentityService__Authority` | 通常不由 `start.sh` 注入；从 Consul 读取稳定 HTTPS Authority |

> HTTP 监听端口固定为 5007（`Program.cs` 硬编码），不再通过 `ASPNETCORE_URLS` 环境变量控制。host 端口映射通过 `start.sh` 的 `Port` 变量控制（`-p ${Port}:5007`）。
>
> 数据库连接不再通过 `ConnectionStrings__Default` 环境变量注入，改由 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）在运行时合成。

## 4. 端口分配

| 端口 | 协议 | 用途 |
|------|------|------|
| 5007（容器内固定） | HTTP | WebAPI 入口 + Swagger UI（仅开发环境） + 健康检查 `/health` + 管理前端 SPA |
| host 映射端口 | — | host 访问容器服务的映射端口（`start.sh` 的 `Port` 变量，`-p ${Port}:5007`） |

## 5. 日志

启动日志格式：

```
Quaestura Service starting
Listening: http://+:5007
Database: PostgreSQL <Host>:<Port>/<Database>
Effective configuration diagnostics: PostgreSqlHost=..., PostgreSqlPort=..., PostgreSqlUsername=..., PostgreSqlPassword=..., DatabaseName=...
OSS: <InternalEndpoint>/<Bucket>
```

数据库固定使用 PostgreSQL，连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建。启动日志同时输出 Effective configuration diagnostics，反映 Consul 注入的 `PostgreSql:*` 与 `Database:Name` 实际值（密码脱敏）。

## 6. 依赖服务

| 依赖 | 是否必需 | 启动失败行为 |
|------|----------|--------------|
| PostgreSQL | 必需 | 连接失败时服务启动失败（`SharedPostgreSqlConnectionStringFactory` 无法构建连接串或数据库不可达） |
| SeaweedFS | 是（图片功能） | 启动成功，图片功能不可用，日志 Warning |
| SignaCore | 是（JWT 验证） | 启动成功，但所有需认证的端点返回 401（OIDC discovery 失败导致签名密钥无法获取） |
