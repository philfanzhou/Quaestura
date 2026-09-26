# 认证与授权规范

本文件定义 `Quaestura` 服务的认证（Authentication）与授权（Authorization）规则。所有 HTTP 端点必须遵循本规范。

## 1. 认证机制

### 1.1 JWT Bearer

服务使用 **JWT Bearer Token** 认证，信任由独立
[SignaCore](https://github.com/philfanzhou/SignaCore)
服务签发的 JWT。

| 项 | 值 |
|----|----|
| 算法 | RS256（RSA 2048 非对称） |
| Issuer | `https://identity.test.ruoyu.study`（按环境配置） |
| Audience | `QuantumZhou.microservices` |
| 公钥分发 | OIDC discovery（`/.well-known/openid-configuration`）+ JWKS（`/.well-known/jwks`） |
| 过期容差 | 30 秒（ClockSkew） |

### 1.2 配置项

通过 `appsettings.json` 或环境变量配置：

| 配置键 | 环境变量 | 必填 | 说明 |
|--------|---------|------|------|
| `IdentityService:Authority` | `IdentityService__Authority` | 是 | OIDC metadata URL；默认 HTTPS，HTTP 部署必须显式 opt-in |
| `IdentityService:Issuer` | `IdentityService__Issuer` | 是 | 新 Token 的精确 Issuer，不从 Authority 推导 |
| `IdentityService:AdditionalValidIssuers` | `IdentityService__AdditionalValidIssuers__0` | 否 | 迁移窗口内允许的旧 Issuer；迁移结束后删除 |
| `IdentityService:Audience` | `IdentityService__Audience` | 是 | 必须与 SignaCore App 的 Shared Audience 一致 |
| `IdentityService:RequireHttpsMetadata` | `IdentityService__RequireHttpsMetadata` | 是 | 默认 `true`；仅在 SignaCore 也显式允许 HTTP 时设为 `false` |
| `IdentityService:ClockSkewSeconds` | `IdentityService__ClockSkewSeconds` | 是 | Token 时间校验容差，当前为 30 秒 |

### 1.3 JWT Claim 规范

服务从 JWT 读取以下 claim：

| Claim Key | 用途 | 提取方式 |
|-----------|------|---------|
| `sub` / `ClaimTypes.NameIdentifier` | 当前用户 ID | `User.GetRequiredUserId()`（来自 `ClaimsPrincipalExtensions`） |
| `role` / `ClaimTypes.Role` | 角色（teacher/assistant/admin/student） | `User.GetRoles()` / `User.IsInRole(...)` / `User.IsStaff()` |

> 提取方法由 [`Quaestura.Common` 认证共享组件](../../src/Common/Authentication/) 提供。

## 2. 授权规则

### 2.1 全局规则

- 所有 `/admin/*` 端点必须经过认证（`FallbackPolicy = RequireAuthenticatedUser`）
- `/health` 端点免认证（健康检查）
- 静态文件和 SPA fallback 免认证（前端资源）
- Swagger UI 仅在开发环境暴露，免认证
- `POST /admin/auth/login` and `POST /admin/auth/callback` are the only `/admin/*` endpoints marked `AllowAnonymous` (see [§6 Admin login](#6-admin-login))

### 2.2 Tag 端点权限矩阵

| 端点 | 角色要求 | 归属校验 | 备注 |
|------|---------|---------|------|
| `GET /admin/tags` | 任意认证用户（含 student） | 无 | 学生需查询 tag 列表以给错题打标 |
| `GET /admin/tags/{id}` | 任意认证用户 | 无 | 同上 |
| `POST /admin/tags`（创建） | teacher / assistant / admin | 无 | 学生不可创建 |
| `POST /admin/tags`（更新） | teacher / assistant / admin | **必须 `tag.CreatedBy == 当前 userId`** | 严格归属制 |
| `DELETE /admin/tags/{id}` | teacher / assistant / admin | **必须 `tag.CreatedBy == 当前 userId`** | 严格归属制 |

### 2.3 其他端点权限（本次仅加登录要求）

| 端点组 | 角色要求 | 备注 |
|--------|---------|------|
| `/admin/questions/*` | 任意认证用户 | 后续可细化为教师写、学生读 |
| `/admin/knowledges/*` | 任意认证用户 | 同上 |
| `/admin/question-knowledges/*` | 任意认证用户 | 同上 |
| `/admin/question-tags/*` | 任意认证用户 | 学生可给自己错题对应的 question 打标 |

### 2.4 UserId 来源

- **不再从请求体读取**：所有 DTO 中的 `UserId` 字段已移除
- **统一从 JWT 读取**：endpoint 内通过 `User.GetRequiredUserId()` 获取
- 数据库 `tag.created_by` / `question.user_id` / `knowledge.created_by` / `knowledge.updated_by` 字段值由服务端写入，客户端无法伪造

## 3. 错误响应

### 3.1 401 Unauthorized

未提供 JWT 或 JWT 无效/过期时，由 ASP.NET Core 认证中间件直接返回：

```json
{
  // 默认 WWW-Authenticate: Bearer error="invalid_token"
}
```

> 注：401 响应体由认证中间件控制，不经过 `ExceptionHandlingMiddleware`。

### 3.2 403 Forbidden

已认证但角色不匹配、或归属校验失败时，由 endpoint 抛出 `ForbiddenException`，经 `ExceptionHandlingMiddleware` 转换：

```json
{
  "success": false,
  "message": "Only staff can create tags",
  "errorCode": "QUAESTURA_FORBIDDEN"
}
```

| errorCode | HTTP | 触发场景 |
|-----------|------|---------|
| `QUAESTURA_FORBIDDEN` | 403 | 角色不足（如学生调用写接口） |
| `QUAESTURA_FORBIDDEN_NOT_OWNER` | 403 | 归属校验失败（修改/删除他人创建的 tag） |

## 4. 测试

### 4.1 单元测试

- `ClaimsPrincipalExtensions`、角色映射、Issuer/Audience/签名/时间边界和 Cookie/Header 优先级由 `Quaestura.Common`（自 `ruoyu.common` 复制的认证共享组件）提供
- `JwtBearerApiTests` 通过实际 JwtBearer 管线覆盖受保护 API 的有效 Token、未知 Issuer 401，以及 `/health` 匿名访问

### 4.2 集成测试

API 级鉴权测试使用 `WebApplicationFactory<Program>` 和测试签名密钥，不替换 JwtBearer Handler；业务 Controller 的细粒度授权测试仍可使用测试认证 Handler。

## 5. 历史背景

- **本次改造前**：服务零认证，userId 从请求体自报，可任意伪造
- **本次改造后**：接入 JWT Bearer，userId 从 JWT 读取，Tag CRUD 加角色 + 归属校验
- **未做的事**：Question/Knowledge 端点的角色细化、question-tag 关联的 question 归属校验（作为后续任务）

## 6. Admin login

SignaCore has no login page, so the admin frontend signs in through Quaestura:

1. The admin frontend sends the username and password to `POST /admin/auth/login`.
2. Quaestura calls SignaCore `POST {IdentityService:Authority}/api/auth/token` with the password grant (`{"grantType":"password","username":"...","password":"..."}`), presenting its own `X-Admin-AppId` / `X-Admin-AppSecret` headers. The AppSecret never leaves the server.
3. While issuing the token, SignaCore calls the application's registered callback, `POST /admin/auth/callback`, with `{"user_id":"..."}`. Quaestura returns `{"roles":["admin"]}` when the user is listed in `AdminPortal:AdminUserIds` (case-insensitive), otherwise `{"roles":[]}`. SignaCore adds the returned roles to the JWT, so whitelisted users get `role: admin`, a staff role (see §2.2).
4. Quaestura returns only `success`, `message`, `accessToken`, `expiresIn`, and `expiresAt` to the frontend. The refresh token and user info are discarded; the admin signs in again after the token expires. No cookie is set.

Request and response formats are in [ApiSpec.md §2.6](../overview/ApiSpec.md#26-admin-authentication-adminauth).

### 6.1 Configuration

| Configuration key | Environment variable | Required | Description |
|--------|---------|------|------|
| `IdentityService:AppId` | `IdentityService__AppId` | For login | AppId of the Quaestura application registered in SignaCore |
| `IdentityService:AppSecret` | `IdentityService__AppSecret` | For login | AppSecret of that application; inject through environment variables or Consul only |
| `AdminPortal:AdminUserIds` | `AdminPortal__AdminUserIds__0`, `__1`, ... | No | SignaCore user IDs that receive the `admin` role; empty by default |

`IdentityService:Authority` is shared with JWT validation (§1.2). When `Authority`, `AppId`, or `AppSecret` is missing, login returns 503 and no request is sent to SignaCore.

### 6.2 Sensitive values

| Value | Flow | Rule |
|------|------|------|
| Password | Browser → Quaestura → SignaCore (request bodies) | Forwarded in memory only, never logged; transport security between the browser and Quaestura is the deployment's responsibility |
| AppSecret | Environment variable or Consul → Quaestura → SignaCore request header | Never returned in a response, never logged, never committed |
| Access token | SignaCore → Quaestura → browser | Never logged |
| Refresh token, user info | SignaCore → Quaestura | Discarded |
| Callback `user_id` | SignaCore → Quaestura | Only compared against the whitelist; logged when the admin role is granted |

Login failures are logged without the username.

### 6.3 Not guaranteed

- Brute-force protection: Quaestura does not rate-limit login. SignaCore sees every attempt as coming from Quaestura, so IP-based limiting in SignaCore can affect all admins at once.
- Callback caller authentication: anyone who can reach `/admin/auth/callback` can ask whether a user ID is whitelisted. It never returns a token and cannot grant privileges by itself.
- Silent token renewal after expiry.
