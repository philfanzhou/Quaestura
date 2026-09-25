# 错误处理规范

## 响应格式

所有 HTTP 端点统一返回以下结构：

### 成功响应

```json
{
  "success": true,
  "data": { ... } | [ ... ],
  "total": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10
}
```

- `data`：业务数据（对象或数组）
- `total`、`page`、`pageSize`、`totalPages`：仅在分页场景出现
- 单条记录查询场景：`{success: true, data: { ... }}`

### 错误响应

```json
{
  "success": false,
  "message": "Human-readable error message in English",
  "errorCode": "QUAESTURA_XXX_YYY"
}
```

错误码命名规范：`QUAESTURA_<资源>_<错误类型>`，全大写，下划线分隔。

## HTTP 状态码使用规则

| 状态码 | 使用场景 | errorCode 前缀 | 示例 |
|--------|----------|---------------|------|
| `200 OK` | 请求成功 | — | 成功查询、新增、更新、删除 |
| `400 Bad Request` | 请求参数验证失败 | `QUAESTURA_VALIDATION_*` | ID 格式无效、必填字段为空、文件过大 |
| `401 Unauthorized` | 未提供 JWT 或 JWT 无效/过期 | —（由认证中间件返回） | token 过期、签名无效 |
| `403 Forbidden` | 已认证但角色不足或归属校验失败 | `QUAESTURA_FORBIDDEN*` | 学生调用 tag 写接口、修改他人创建的 tag |
| `404 Not Found` | 资源不存在 | `QUAESTURA_<RESOURCE>_NOT_FOUND` | Question not found |
| `409 Conflict` | 资源冲突 | `QUAESTURA_<RESOURCE>_CONFLICT` | 重复创建 |
| `422 Unprocessable Entity` | 业务前置条件不满足 | `QUAESTURA_PRECONDITION_*` | 删除被引用的知识点 |
| `500 Internal Server Error` | 服务内部错误 | `QUAESTURA_INTERNAL_ERROR` | 数据库异常、未捕获异常 |
| `503 Service Unavailable` | 依赖不可用 | `QUAESTURA_UNAVAILABLE` | OSS 不可达 |

## 异常处理

### 自定义异常类型

| 异常 | 用途 | HTTP 状态码 | errorCode |
|------|------|-------------|-----------|
| `DomainException` (含子类) | 业务规则违反 | 视子类而定 | 视子类而定 |
| `EntityNotFoundException` | 实体不存在 | 404 | `QUAESTURA_<RESOURCE>_NOT_FOUND` |
| `ValidationException` | 参数验证失败 | 400 | `QUAESTURA_VALIDATION_*` |
| `BusinessPreconditionException` | 业务前置条件不满足 | 422 | `QUAESTURA_PRECONDITION_*` |
| `ForbiddenException` | 角色不足或归属校验失败 | 403 | `QUAESTURA_FORBIDDEN` 或 `QUAESTURA_FORBIDDEN_NOT_OWNER` |

### 全局异常中间件

所有 WebAPI 端点必须经 `ExceptionHandlingMiddleware`（位于 `src/Service/Middleware/ExceptionHandlingMiddleware.cs`）统一处理：

- `DomainException` 子类 → 映射为对应 HTTP 状态码 + 业务错误码
- `ValidationException`（FluentValidation） → 400 + `QUAESTURA_VALIDATION_FAILED`，message 为校验错误列表
- `InvalidOperationException`（由 `ImageValidationHelper` 抛出）→ 400 + `QUAESTURA_VALIDATION_INVALID_IMAGE`
- `BadHttpRequestException` → 400
- 其他未捕获异常 → 500 + `QUAESTURA_INTERNAL_ERROR`，原始 message 脱敏（仅显示 "Internal server error"），详细异常信息写入日志

## 错误信息规范

1. 错误信息使用**英文**（按 `30-backend-routing.md` 编码规范）
2. 错误信息应简洁明确，不包含技术细节（如 SQL 语句、堆栈）
3. 同一类错误在各服务中使用相同措辞
4. 用户可见的业务提示可以本地化，但 API 响应 message 一律英文

## 参数验证

- 使用 FluentValidation 验证 HTTP 请求 DTO
- 验证失败抛出 `ValidationException`（由全局中间件转为 400）
- ID 解析使用 `Guid.TryParse`，失败抛出 `ValidationException` 含 errorCode `QUAESTURA_VALIDATION_INVALID_ID`

## 日志规范

- 使用结构化日志占位符：`logger.LogInformation("Created question {QuestionId}", id)` 而非字符串插值
- 异常对象必须传入：`logger.LogError(ex, "...")` 而非 `logger.LogError(ex.Message, ...)`
- 预期内的 `EntityNotFoundException` 使用 `Warning` 级别
- 未捕获异常使用 `Error` 级别
- 不记录图片二进制内容、Token、密码等敏感信息
- OSS 删除失败等"软失败"使用 `Warning` 级别，主流程失败使用 `Error`
