# 部署与运行

## 1. 快速启动

### 1.1 启动要求

1. **数据库**：PostgreSQL（本地开发与容器化部署统一使用）
2. **对象存储**：SeaweedFS（S3 端口 8333）
3. **网络端口**：HTTP 5007

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

`start.sh` 默认连接 PostgreSQL 容器 `ruoyu-postgres:5432`（库 `ruoyu_study_questionbank`）。

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

`appsettings.json` 中 `ConnectionStrings:Default`：

```json
{
  "ConnectionStrings": {
    "Default": "Host=ruoyu-postgres;Port=5432;Database=ruoyu_study_questionbank;Username=postgres;Password=postgres;"
  }
}
```

本服务仅支持 PostgreSQL（`Program.cs` 中通过 `UseNpgsql` 强制注册）。

数据库表由 `DatabaseInitializer` 启动时自动创建（`CREATE TABLE IF NOT EXISTS`），无需手动建库。

#### 通过环境变量覆盖

```bash
export ConnectionStrings__Default="Host=...;Port=...;Database=...;Username=...;Password=...;"
```

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
| `ConnectionStrings__Default` | PostgreSQL 连接串 |
| `Oss__Endpoint` | `ruoyu-seaweedfs:8333` |
| `Oss__AccessKey` | `seaweedfs_admin` |
| `Oss__SecretKey` | `seaweedfs_admin` |
| `Oss__BucketName` | `ruoyu-study` |

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
Database: PostgreSQL ruoyu-postgres:5432/ruoyu_study_questionbank
OSS: ruoyu-seaweedfs:8333/ruoyu-study
```

数据库类型固定为 PostgreSQL，启动日志直接记录连接信息。

## 6. 依赖服务

| 依赖 | 是否必需 | 启动失败行为 |
|------|----------|--------------|
| PostgreSQL | 是 | 启动失败 |
| SeaweedFS | 是（图片功能） | 启动成功，图片功能不可用，日志 Warning |
