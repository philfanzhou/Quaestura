# Quaestura 部署与运行指南

本文档涵盖了项目环境搭建、配置要求和 Docker 容器部署等指南。完整规范见 [`docs/development/Deployment.md`](../../docs/development/Deployment.md)。

## 1. 快速启动

### 1.1 启动要求

1. **SeaweedFS**: 必须先启动 SeaweedFS 服务（S3 端口 8333）
2. **数据库**: PostgreSQL，启动时自动建表

### 1.2 WebAPI 端口

- **HTTP**: 5007
- **Swagger UI**: http://localhost:5007/swagger（仅开发环境）
- **健康检查**: http://localhost:5007/health
- **WebUI（管理前端）**: http://localhost:5007/（与 API 同端口同进程，详见 [`frontend/docs/admin-frontend-spec.md`](../../frontend/docs/admin-frontend-spec.md)）

### 1.3 配置项

在 `appsettings.json` 中需要配置如下关键连接信息：

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

`APP_TITLE` 配置（**可选**，默认 `Quaestura Admin`）：

```json
{
  "APP_TITLE": "quaestura"
}
```

后端启动时把 `index.html` 中的 `__APP_TITLE__` 占位符替换为该值，同时把 `window.__APP_TITLE__` 注入到 `<head>` 供前端 JS 读取。`start.sh` 默认传 `-e APP_TITLE="${CONTAINER_NAME}"`。

## 2. 端点清单

完整端点规范见 [`docs/overview/ApiSpec.md`](../../docs/overview/ApiSpec.md)。

### 题目管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/questions` | 搜索/分页 |
| GET | `/admin/questions/{id}` | 详情 |
| POST | `/admin/questions` | 上传题目（multipart/form-data） |
| DELETE | `/admin/questions/{id}` | 删除 |

### 知识点管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/knowledges` | 列表/搜索/分页 |
| GET | `/admin/knowledges/{id}` | 详情 |
| POST | `/admin/knowledges` | 新增/更新 |
| DELETE | `/admin/knowledges/{id}` | 删除 |

### 题目-知识点关联

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/question-knowledges/batch-tag` | 批量打标签 |
| GET | `/admin/question-knowledges` | 查询关联 |
| DELETE | `/admin/question-knowledges` | 移除（按 questionId） |

### Tag 管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/tags` | 列表/搜索/排序 |
| GET | `/admin/tags/{id}` | 详情 |
| POST | `/admin/tags` | 新增/更新 |
| DELETE | `/admin/tags/{id}` | 删除（无引用时） |

### 题目-Tag 关联

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/question-tags/batch-tag` | 批量打标签 |
| GET | `/admin/question-tags` | 查询关联（按 questionId 或 tagId） |
| DELETE | `/admin/question-tags` | 移除（按 questionId，可选 tagId） |

## 3. 路径分流

后端在同一 HTTP 端口（5007）同时提供 **WebAPI**、**管理前端 SPA** 和 **健康检查**：

| 路径前缀 | 服务方 |
|---------|--------|
| `/admin/*` | WebAPI（题目/知识点/Tag 增删改查） |
| `/health` | 健康检查 |
| `/swagger` | Swagger UI（仅开发环境） |
| `/` 等其他 | 管理前端 SPA（Vue Router history fallback） |

SPA fallback 由 ASP.NET Core `MapWhen` 实现（详见 `Program.cs`），仅在 HTTP 端口生效；不存在 `__APP_TITLE__` 注入失败导致 SPA 不可用的情况（注入失败时返回原始 index.html）。 The SPA branch is registered before `UseAuthentication()` / `UseAuthorization()`, so static assets and the SPA fallback are served without authentication; `/admin/*` still requires a valid JWT.

## 4. Docker 环境部署

### 4.1 部署 SeaweedFS

Quaestura 依赖一个 S3 兼容对象存储（如 SeaweedFS，S3 端口 8333）存放题目图片，其部署不属于本仓库；连接信息经 `Oss:*` 配置或 Consul KV 注入。

### 4.2 构建与部署

Docker 镜像是**单 docker 同时包含 backend + frontend**（多阶段构建，`Dockerfile` 位于仓库根目录，build context 为仓库根）：

```bash
docker build -t quaestura:latest .
```

部署网络：`start.sh` 默认使用 `quaestura-net` 桥接网络；与 Ruoyu.Study 平台同机部署时可改用平台网络，SeaweedFS 等依赖也可通过 Consul 配置的独立服务器地址访问。

### 4.3 本地开发

如需修改管理前端并立即看到效果，单独构建前端即可（开发模式 `npm run dev` 走 vite proxy 5173 → 5007）：

```bash
cd ../frontend
npm install
npm run dev   # 开发模式（Vite dev server + 代理到 :5007）
npm run build # 生产构建（生成 dist/，由 Docker 阶段 1 复制到 wwwroot/）
```
