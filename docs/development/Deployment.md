# 部署与运行

## 1. 快速启动

### 1.1 启动要求

1. **数据库**：PostgreSQL（生产，由 Consul 共享配置注入）；本地开发可回退 SQLite
2. **对象存储**：SeaweedFS（S3 端口 8333）
3. **认证服务**：QuantumZhou.Identity（用于 JWT 签发与公钥分发，必须可达以完成 OIDC discovery）
4. **网络端口**：HTTP 5007

### 1.2 启动方式

#### 方式一：本地 .NET 启动

```bash
cd src/services/ruoyu.questionBank/src/Host
dotnet run --configuration Release
```

#### 方式二：Docker 启动

```bash
# 1. 构建镜像
./script/build-script/06-questionbank.build.sh

# 2. 启动容器
cd src/services/ruoyu.questionBank
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
    "Name": "ruoyu_study_questionbank"
  },
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=ruoyu_study_questionbank;Username=phil"
  }
}
```

连接串包含 `Host=` 或 `Server=` 时走 PostgreSQL（`UseNpgsql`），否则走 SQLite 回退（`Data Source=data/sqlite/ruoyu_study_questionbank.db`）。生产环境的 PostgreSQL 主机/端口/账号/密码由 Consul 的 `PostgreSql:*` 键覆盖，无需写入 `appsettings.json`。

数据库表由 `DatabaseInitializer` 启动时自动创建（`CREATE TABLE IF NOT EXISTS`），无需手动建库。

#### 通过 Consul 共享配置覆盖

生产环境通过 Consul KV（`config/ruoyu` 前缀）注入以下键，由 `SharedPostgreSqlConnectionStringFactory` 合成连接串：

| Consul 键 | 作用 |
|-----------|------|
| `PostgreSql:Host` | PostgreSQL 主机 |
| `PostgreSql:Port` | PostgreSQL 端口 |
| `PostgreSql:Username` | PostgreSQL 用户名 |
| `PostgreSql:Password` | PostgreSQL 密码 |
| `Database:Name` | 数据库名（默认 `ruoyu_study_questionbank`） |

### 2.2 对象存储

```json
{
  "Oss": {
    "Endpoint": "ruoyu-seaweedfs:8333",
    "AccessKey": "seaweedfs_admin",
    "SecretKey": "seaweedfs_admin",
    "BucketName": "ruoyu-study",
    "PublicEndpoint": "https://ry.zhoufan.asia"
  }
}
```

- `Endpoint`：内部通信地址（容器内用容器名 `ruoyu-seaweedfs:8333`，本地用 `localhost:8333`）
- `PublicEndpoint`：外部访问地址，用于生成预签名 URL
- `AccessKey` / `SecretKey`：S3 凭证

启动时会自动检测 OSS 连通性（`Program.cs` 中的 `OssService.CheckConnectivityAsync`），失败时输出 Warning 但不阻断启动。

### 2.3 认证服务（JWT）

服务通过 JWT Bearer Token 认证，信任由 QuantumZhou.Identity 签发的 JWT。详见 [Authentication.md](./Authentication.md)。

```json
{
  "IdentityService": {
    "Authority": "http://ruoyu-identity:8080",
    "Audience": "QuantumZhou.microservices",
    "RequireHttpsMetadata": false
  }
}
```

- `Authority`：Identity 服务基础 URL，用于 OIDC discovery（拉取 `/.well-known/openid-configuration` 和 `/.well-known/jwks`）
- `Audience`：预期 audience，默认 `QuantumZhou.microservices`
- `RequireHttpsMetadata`：是否强制 HTTPS 元数据，开发环境可设为 false

**必需**：`IdentityService:Authority` 必须配置，否则服务启动时抛 `InvalidOperationException`。

签名公钥通过 OIDC discovery 自动获取，无需手动配置密钥。

## 3. Docker 部署

### 3.1 镜像构建（4 阶段多阶段构建）

Dockerfile 位于 `src/Host/Dockerfile`，**单个镜像同时包含 backend（.NET 8 ASP.NET Core）+ frontend（Vue 3 构建产物）**：

| 阶段 | 基镜像 | 作用 |
|------|--------|------|
| 1. `frontend-build` | `node:20-alpine` | 构建 Vue 3 frontend，输出 `dist/` |
| 2. `build` | `mcr.microsoft.com/dotnet/sdk:8.0` | 还原 + 发布 .NET Host，**把阶段 1 的 `dist/` 复制到 `Host/wwwroot/`** |
| 3. `final` | `mcr.microsoft.com/dotnet/aspnet:8.0` | 运行时镜像，仅含 .NET 运行时 + 发布产物 |

**关键点**：
- build context 是仓库根 `$REPO_ROOT`（与 `Identity` 服务的 3 阶段构建对齐）
- 阶段 1 独立：frontend 构建失败不会污染 backend 镜像
- 阶段 2 的 `COPY --from=frontend-build /app/dist .../Host/wwwroot` 是把 Vite 产物注入 ASP.NET Core 默认 web root 的关键一行

### 3.2 启动

```bash
cd src/services/ruoyu.questionBank
./start.sh
```

容器使用 `ruoyu-net` 网络，通过容器名解析其他服务（`ruoyu-postgres`、`ruoyu-seaweedfs`）。

### 3.3 环境变量

`start.sh` 注入以下环境变量：

| 变量 | 值 |
|------|-----|
| `TZ` | `Asia/Shanghai` |
| `ASPNETCORE_URLS` | `http://+:5007` |
| `Oss__Endpoint` | `ruoyu-seaweedfs:8333` |
| `Oss__AccessKey` | `seaweedfs_admin` |
| `Oss__SecretKey` | `seaweedfs_admin` |
| `Oss__BucketName` | `ruoyu-study` |
| `IdentityService__Authority` | `http://ruoyu-identity:5002`（JWT 签发方，用于 OIDC discovery） |

> 数据库连接不再通过 `ConnectionStrings__Default` 环境变量注入，改由 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）在运行时合成。

## 4. 端口分配

| 端口 | 协议 | 用途 |
|------|------|------|
| 5007 | HTTP | WebAPI 入口 |
| 5007 | HTTP | Swagger UI（仅开发环境） |
| 5007 | HTTP | 健康检查 `/health` |

## 5. 日志

启动日志格式：

```
QuestionBank Service starting
Listening: http://+:5007
Database: PostgreSQL <Host>:<Port>/<Database>   # 或 Database: SQLite
Effective configuration diagnostics: PostgreSqlHost=..., PostgreSqlPort=..., PostgreSqlUsername=..., PostgreSqlPassword=..., DatabaseName=...
OSS: <Endpoint>/<Bucket>
```

数据库类型由连接串内容决定：包含 `Host=`/`Server=` 走 PostgreSQL，否则走 SQLite 回退。启动日志同时输出 Effective configuration diagnostics，反映 Consul 注入的 `PostgreSql:*` 与 `Database:Name` 实际值（密码脱敏）。

## 6. 依赖服务

| 依赖 | 是否必需 | 启动失败行为 |
|------|----------|--------------|
| PostgreSQL | 生产必需 | 本地 dev 缺失时回退 SQLite（`data/sqlite/ruoyu_study_questionbank.db`）；生产环境 Consul 未注入 `PostgreSql:*` 时同样回退 SQLite |
| SeaweedFS | 是（图片功能） | 启动成功，图片功能不可用，日志 Warning |
| QuantumZhou.Identity | 是（JWT 验证） | 启动成功，但所有需认证的端点返回 401（OIDC discovery 失败导致签名密钥无法获取） |
