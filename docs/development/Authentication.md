# 认证与授权规范

本文件定义 `ruoyu.questionBank` 服务的认证（Authentication）与授权（Authorization）规则。所有 HTTP 端点必须遵循本规范。

## 1. 认证机制

### 1.1 JWT Bearer

服务使用 **JWT Bearer Token** 认证，信任由 [QuantumZhou.Identity](../../../QuantumZhou.Identity/) 服务签发的 JWT。

| 项 | 值 |
|----|----|
| 算法 | RS256（RSA 2048 非对称） |
| Issuer | `QuantumZhou.Identity` |
| Audience | `QuantumZhou.microservices` |
| 公钥分发 | OIDC discovery（`/.well-known/openid-configuration`）+ JWKS（`/.well-known/jwks`） |
| 过期容差 | 30 秒（ClockSkew） |

### 1.2 配置项

通过 `appsettings.json` 或环境变量配置：

| 配置键 | 环境变量 | 必填 | 说明 |
|--------|---------|------|------|
| `IdentityService:Authority` | `IdentityService__Authority` | 是 | Identity 服务 URL（如 `http://ruoyu-identity:8080`） |
| `IdentityService:Audience` | `IdentityService__Audience` | 否 | 默认 `QuantumZhou.microservices` |
| `IdentityService:RequireHttpsMetadata` | `IdentityService__RequireHttpsMetadata` | 否 | 默认 `false` |

### 1.3 JWT Claim 规范

服务从 JWT 读取以下 claim：

| Claim Key | 用途 | 提取方式 |
|-----------|------|---------|
| `sub` / `ClaimTypes.NameIdentifier` | 当前用户 ID | `User.GetRequiredUserId()`（来自 `ClaimsPrincipalExtensions`） |
| `role` / `ClaimTypes.Role` | 角色（teacher/assistant/admin/student） | `User.GetRoles()` / `User.IsInRole(...)` / `User.IsStaff()` |

> 提取方法由 [`ruoyu.common` 认证共享组件](../../../../services/ruoyu.common/docs/authentication.md) 提供。

## 2. 授权规则

### 2.1 全局规则

- 所有 `/admin/*` 端点必须经过认证（`FallbackPolicy = RequireAuthenticatedUser`）
- `/health` 端点免认证（健康检查）
- 静态文件和 SPA fallback 免认证（前端资源）
- Swagger UI 仅在开发环境暴露，免认证

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
  "errorCode": "QUESTIONBANK_FORBIDDEN"
}
```

| errorCode | HTTP | 触发场景 |
|-----------|------|---------|
| `QUESTIONBANK_FORBIDDEN` | 403 | 角色不足（如学生调用写接口） |
| `QUESTIONBANK_FORBIDDEN_NOT_OWNER` | 403 | 归属校验失败（修改/删除他人创建的 tag） |

## 4. 测试

### 4.1 单元测试

- `ClaimsPrincipalExtensions` / `RoleConstants` 的测试由 `ruoyu.common` 项目覆盖（245 个 UT 全过）
- Tag endpoint 鉴权逻辑（角色校验 + 归属校验）目前通过编译时类型检查和代码 review 保证；端到端鉴权测试作为后续技术债

### 4.2 集成测试

如需端到端测试鉴权，使用 `WebApplicationFactory<Program>` + 测试用 JWT（参考 `ruoyu.common` 的 `TestAuthHandler`）。

## 5. 历史背景

- **本次改造前**：服务零认证，userId 从请求体自报，可任意伪造
- **本次改造后**：接入 JWT Bearer，userId 从 JWT 读取，Tag CRUD 加角色 + 归属校验
- **未做的事**：Question/Knowledge 端点的角色细化、question-tag 关联的 question 归属校验（作为后续任务）
