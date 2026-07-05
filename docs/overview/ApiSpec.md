# QuestionBank API 规范

本文档定义 `ruoyu.questionBank` 服务的完整 HTTP API。所有端点统一返回结构化 JSON 响应，遵循 [`docs/development/ErrorHandling.md`](../development/ErrorHandling.md) 规范。

## 1. 通用约定

### 1.1 认证

所有 `/admin/*` 端点要求 JWT Bearer Token 认证（详见 [Authentication.md](../development/Authentication.md)）：

```
Authorization: Bearer <jwt-token>
```

JWT 由 QuantumZhou.Identity 签发，包含 `sub`（userId）和 `role`（teacher/assistant/admin/student）claim。服务端从 JWT 读取 userId，**请求体不再包含 userId 字段**。

未提供或提供无效 JWT → 401 Unauthorized（由认证中间件返回）。

### 1.2 响应格式

**成功响应（单条）**：

```json
{
  "success": true,
  "data": { ... }
}
```

**成功响应（列表/分页）**：

```json
{
  "success": true,
  "data": [ ... ],
  "total": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10
}
```

**错误响应**：

```json
{
  "success": false,
  "message": "Human-readable error message in English",
  "errorCode": "QUESTIONBANK_XXX_YYY"
}
```

### 1.3 通用字段

- 所有 ID 字段为 UUID 字符串（如 `00000000-0000-0000-0000-000000000001`）
- 所有时间戳为 ISO 8601 UTC 字符串（如 `2026-07-02T10:00:00.000Z`）
- `subject`（学科）和 `grade`（年级）为整数，由 [`ruoyu.common` 共享常量](../../../../services/ruoyu.common/) 定义
- 所有消息/错误信息使用英文

### 1.4 通用查询参数

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|------|------|------|--------|------|
| `page` | int | 否 | 1 | 页码（从 1 开始） |
| `size` | int | 否 | 10 | 每页数量（上限 100） |

## 2. 端点清单

### 2.1 题目管理 `/admin/questions`

#### GET `/admin/questions`

搜索/分页查询题目。

**查询参数**：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `keyword` | string | 否 | 题干关键字（匹配 `question_content.content`） |
| `level` | int | 否 | 难度等级（> 0 生效） |
| `type` | int | 否 | 题目类型（> 0 生效） |
| `subject` | int | **是** | 学科 |
| `grade` | int | **是** | 年级 |
| `tagId` | UUID | 否 | 仅返回已打该标签的题目 |
| `page` | int | 否 | 默认 1 |
| `size` | int | 否 | 默认 10，上限 100 |

**响应 200**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "level": 1,
      "type": 1,
      "width": 800,
      "height": 600,
      "picturePaths": ["https://...presigned-url-1", "https://...presigned-url-2"],
      "content": "题干内容",
      "correctAnswer": "正确答案",
      "analysis": "解析",
      "userId": "uuid",
      "studentId": "uuid",
      "mistakeId": "uuid",
      "subject": 1,
      "grade": 7,
      "createdAt": "2026-07-02T10:00:00.000Z",
      "updatedAt": "2026-07-02T10:00:00.000Z"
    }
  ],
  "total": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — subject/grade 缺失或非法

#### GET `/admin/questions/{id}`

获取题目详情。

**路径参数**：

| 参数 | 类型 | 说明 |
|------|------|------|
| `id` | UUID | 题目 ID |

**响应 200**：

```json
{
  "success": true,
  "data": {
    "id": "uuid",
    "level": 1,
    "type": 1,
    "width": 800,
    "height": 600,
    "picturePaths": ["https://...presigned-url"],
    "content": "题干内容",
    "correctAnswer": "正确答案",
    "analysis": "解析",
    "userId": "uuid",
    "studentId": "uuid",
    "mistakeId": "uuid",
    "subject": 1,
    "grade": 7,
    "createdAt": "2026-07-02T10:00:00.000Z",
    "updatedAt": "2026-07-02T10:00:00.000Z"
  }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID` — ID 格式错误
- 404 `QUESTIONBANK_QUESTION_NOT_FOUND` — 题目不存在

#### POST `/admin/questions`

上传/更新题目（multipart/form-data）。

**Form 字段**：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `question` | string (JSON) | **是** | `QuestionPayload` JSON 字符串 |
| `content` | string (JSON) | 否 | `QuestionContentPayload` JSON 字符串 |
| `subject` | int (form) | **是** | 学科 |
| `grade` | int (form) | **是** | 年级 |
| `pictures` | file[] | 否 | 图片文件（支持 JPEG/PNG/GIF/WebP/BMP，单张 ≤ 10MB） |

> `userId` 从 JWT 读取（`sub` claim），不再从 form 字段获取。

**QuestionPayload**：

```json
{
  "id": "uuid (optional, present = update, absent = create)",
  "level": 1,
  "type": 1,
  "width": 800,
  "height": 600,
  "studentId": "uuid (optional)",
  "mistakeId": "uuid (optional)"
}
```

**QuestionContentPayload**：

```json
{
  "content": "题干内容",
  "correctAnswer": "正确答案",
  "analysis": "解析"
}
```

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — 字段验证失败
- 400 `QUESTIONBANK_VALIDATION_INVALID_IMAGE` — 图片格式/大小不符

#### DELETE `/admin/questions/{id}`

删除题目。

**路径参数**：

| 参数 | 类型 | 说明 |
|------|------|------|
| `id` | UUID | 题目 ID |

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID` — ID 格式错误
- 404 `QUESTIONBANK_QUESTION_NOT_FOUND` — 题目不存在

### 2.2 知识点管理 `/admin/knowledges`

#### GET `/admin/knowledges`

知识点列表/搜索/分页。

**查询参数**：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `parentId` | UUID | 否 | 父节点 ID（不传则返回顶级节点） |
| `grade` | int | 否 | 按年级筛选 |
| `subject` | int | 否 | 按学科筛选（需与 `grade` 配合使用） |
| `name` | string | 否 | 按名称模糊搜索（LIKE） |
| `page` | int | 否 | 默认 1 |
| `size` | int | 否 | 默认 10，上限 100 |

**响应 200**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "parentId": "uuid or null",
      "name": "一般现在时",
      "description": "描述",
      "createdBy": "uuid",
      "createdAt": "2026-07-02T10:00:00.000Z",
      "isReferenced": false,
      "subject": 1,
      "grade": 7,
      "updatedBy": "uuid",
      "updatedAt": "2026-07-02T10:00:00.000Z"
    }
  ],
  "total": 50,
  "page": 1,
  "pageSize": 10,
  "totalPages": 5
}
```

#### GET `/admin/knowledges/{id}`

知识点详情。

**响应 200**：

```json
{
  "success": true,
  "data": {
    "id": "uuid",
    "parentId": "uuid",
    "name": "...",
    ...
  }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID`
- 404 `QUESTIONBANK_KNOWLEDGE_NOT_FOUND`

#### POST `/admin/knowledges`

新增或更新知识点（id 存在则更新，空则新增）。

**请求体**：

```json
{
  "id": "uuid (optional)",
  "parentId": "uuid (optional)",
  "name": "知识点名称",
  "description": "描述 (optional)",
  "subject": 1,
  "grade": 7
}
```

> `userId` 从 JWT 读取，写入 `created_by` / `updated_by`。

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED`
- 409 `QUESTIONBANK_KNOWLEDGE_DUPLICATE` — 同 (subject, grade, name) 已存在
- 422 `QUESTIONBANK_KNOWLEDGE_REFERENCED` — 已被题目引用，不能更新
- 422 `QUESTIONBANK_KNOWLEDGE_CYCLE` — 父节点变更会形成环

#### DELETE `/admin/knowledges/{id}`

删除知识点。

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID`
- 404 `QUESTIONBANK_KNOWLEDGE_NOT_FOUND`
- 422 `QUESTIONBANK_KNOWLEDGE_REFERENCED`

### 2.3 题目-知识点关联 `/admin/question-knowledges`

#### POST `/admin/question-knowledges/batch-tag`

批量给多个题目打同一个知识点标签。

**请求体**：

```json
{
  "questionIds": ["uuid", "uuid"],
  "knowledgeId": "uuid",
  "subject": 1,
  "grade": 7
}
```

**响应 200**：

```json
{
  "success": true,
  "data": { "taggedCount": 2 }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED`
- 404 `QUESTIONBANK_KNOWLEDGE_NOT_FOUND` — 知识点不存在或 subject/grade 不匹配

#### GET `/admin/question-knowledges`

查询关联（二选一）。

**查询参数**（必须提供 `questionId` 或 `knowledgeId` 之一）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `questionId` | UUID | 条件 | 查询某题目的所有知识点关联 |
| `knowledgeId` | UUID | 条件 | 查询某知识点的所有题目 ID（分页） |
| `page` | int | 否 | 默认 1（仅 `knowledgeId` 模式） |
| `size` | int | 否 | 默认 10（仅 `knowledgeId` 模式） |

**响应 200（questionId 模式）**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "questionId": "uuid",
      "knowledgeId": "uuid",
      "weight": 1.0,
      "subject": 1,
      "grade": 7
    }
  ]
}
```

**响应 200（knowledgeId 模式）**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "level": 1,
      "type": 1,
      "picturePaths": ["https://..."],
      "content": "...",
      "subject": 1,
      "grade": 7,
      "createdAt": "2026-07-02T10:00:00.000Z",
      "updatedAt": "2026-07-02T10:00:00.000Z"
    }
  ],
  "total": 50,
  "page": 1,
  "pageSize": 10,
  "totalPages": 5
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — 未提供 questionId/knowledgeId

#### DELETE `/admin/question-knowledges`

移除某题目的所有知识点关联。

**查询参数**：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `questionId` | UUID | **是** | 题目 ID |

**响应 200**：

```json
{
  "success": true,
  "data": { "questionId": "uuid", "removed": true }
}
```

### 2.4 Tag 管理 `/admin/tags`

Tag 是与 Knowledge 平行的自由标签（无层级，不绑 subject/grade）。适用于教师对题目的临时打标（"期中重点"、"高频考点"、"易错题"等）。

#### 权限矩阵

| 端点 | 角色要求 | 归属校验 |
|------|---------|---------|
| `GET /admin/tags` | 任意认证用户（含 student） | 无 |
| `GET /admin/tags/{id}` | 任意认证用户 | 无 |
| `POST /admin/tags`（创建） | teacher / assistant / admin | 无 |
| `POST /admin/tags`（更新） | teacher / assistant / admin | 必须 `CreatedBy == 当前 userId` |
| `DELETE /admin/tags/{id}` | teacher / assistant / admin | 必须 `CreatedBy == 当前 userId` |

#### GET `/admin/tags`

列表/搜索 Tag。

**查询参数**：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `name` | string | 否 | 名称模糊搜索（`Contains` 匹配，跨库兼容） |
| `sortBy` | string | 否 | `usageCount`（按引用次数降序）或留空（按 `name` 升序） |
| `page` | int | 否 | 默认 1 |
| `size` | int | 否 | 默认 10，上限 100 |

**响应 200**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "name": "期中重点",
      "color": "#FF6B6B",
      "description": "期中考试重点",
      "createdBy": "uuid",
      "createdAt": "2026-07-02T10:00:00.000Z",
      "usageCount": 12
    }
  ],
  "total": 50,
  "page": 1,
  "size": 10,
  "totalPages": 5
}
```

#### GET `/admin/tags/{id}`

Tag 详情。

**响应 200**：

```json
{
  "success": true,
  "data": {
    "id": "uuid",
    "name": "期中重点",
    "color": "#FF6B6B",
    "description": "期中考试重点",
    "createdBy": "uuid",
    "createdAt": "2026-07-02T10:00:00.000Z",
    "usageCount": 12
  }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID`
- 404 `QUESTIONBANK_TAG_NOT_FOUND`

#### POST `/admin/tags`

新增或更新 Tag（id 存在则更新，空则新增）。

**角色**：teacher / assistant / admin（学生不可调用，返回 403）

**归属**：更新模式下，仅 `tag.CreatedBy == 当前 userId` 可修改，否则返回 403

**请求体**：

```json
{
  "id": "uuid (optional)",
  "name": "期中重点",
  "color": "#FF6B6B (optional, HEX format)",
  "description": "期中考试重点 (optional)"
}
```

> `userId` 从 JWT 读取，写入 `created_by`。

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — name 缺失或 color 格式非法
- 403 `QUESTIONBANK_FORBIDDEN` — 学生角色调用
- 403 `QUESTIONBANK_FORBIDDEN_NOT_OWNER` — 更新他人创建的 tag
- 409 `QUESTIONBANK_TAG_DUPLICATE` — 同名 Tag 已存在

#### DELETE `/admin/tags/{id}`

删除 Tag（仅当 `usageCount == 0` 时可删）。

**角色**：teacher / assistant / admin

**归属**：仅 `tag.CreatedBy == 当前 userId` 可删除，否则返回 403

**响应 200**：

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_INVALID_ID`
- 403 `QUESTIONBANK_FORBIDDEN` — 学生角色调用
- 403 `QUESTIONBANK_FORBIDDEN_NOT_OWNER` — 删除他人创建的 tag
- 404 `QUESTIONBANK_TAG_NOT_FOUND`
- 422 `QUESTIONBANK_TAG_REFERENCED` — Tag 仍被题目引用

### 2.5 题目-Tag 关联 `/admin/question-tags`

#### POST `/admin/question-tags/batch-tag`

批量给多个题打多个 Tag（已存在的关联自动跳过）。

**角色**：任意认证用户（学生可调用，给自己错题对应的 question 打标）

**请求体**：

```json
{
  "questionIds": ["uuid", "uuid"],
  "tagIds": ["uuid", "uuid"]
}
```

**响应 200**：

```json
{
  "success": true,
  "data": { "taggedCount": 4 }
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — questionIds/tagIds 为空
- 404 `QUESTIONBANK_TAG_NOT_FOUND` — 任一 tagId 不存在

#### GET `/admin/question-tags`

查询关联（二选一）。

**查询参数**（必须提供 `questionId` 或 `tagId` 之一）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `questionId` | UUID | 条件 | 查询某题目的所有 Tag 关联 |
| `tagId` | UUID | 条件 | 查询某 Tag 关联的所有题目（分页） |
| `page` | int | 否 | 默认 1（仅 `tagId` 模式） |
| `size` | int | 否 | 默认 10（仅 `tagId` 模式） |

**响应 200（questionId 模式）**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "questionId": "uuid",
      "tagId": "uuid",
      "tagName": "期中重点",
      "tagColor": "#FF6B6B",
      "createdAt": "2026-07-02T10:00:00.000Z"
    }
  ]
}
```

**响应 200（tagId 模式）**：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "level": 1,
      "type": 1,
      "picturePaths": ["https://..."],
      "content": "...",
      "subject": 1,
      "grade": 7,
      "createdAt": "2026-07-02T10:00:00.000Z",
      "updatedAt": "2026-07-02T10:00:00.000Z"
    }
  ],
  "total": 50,
  "page": 1,
  "size": 10,
  "totalPages": 5
}
```

**错误**：
- 400 `QUESTIONBANK_VALIDATION_FAILED` — 未提供 questionId/tagId

#### DELETE `/admin/question-tags`

移除关联。

**查询参数**：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `questionId` | UUID | **是** | 题目 ID |
| `tagId` | UUID | 否 | 不传则移除该题目的所有 Tag；传值则仅移除指定 Tag |

**响应 200**：

```json
{
  "success": true,
  "data": { "questionId": "uuid", "tagId": "uuid (if specified)", "removed": true }
}
```

## 3. 错误码字典

| errorCode | HTTP 状态 | 说明 |
|-----------|----------|------|
| `QUESTIONBANK_VALIDATION_FAILED` | 400 | 请求参数验证失败（FluentValidation） |
| `QUESTIONBANK_VALIDATION_INVALID_ID` | 400 | ID 格式非 UUID |
| `QUESTIONBANK_VALIDATION_INVALID_IMAGE` | 400 | 图片格式/大小不符 |
| `QUESTIONBANK_FORBIDDEN` | 403 | 已认证但角色不足（如学生调用 tag 写接口） |
| `QUESTIONBANK_FORBIDDEN_NOT_OWNER` | 403 | 归属校验失败（修改/删除他人创建的 tag） |
| `QUESTIONBANK_QUESTION_NOT_FOUND` | 404 | 题目不存在 |
| `QUESTIONBANK_KNOWLEDGE_NOT_FOUND` | 404 | 知识点不存在 |
| `QUESTIONBANK_KNOWLEDGE_DUPLICATE` | 409 | 同 (subject, grade, name) 已存在 |
| `QUESTIONBANK_KNOWLEDGE_REFERENCED` | 422 | 知识点已被引用，不能修改/删除 |
| `QUESTIONBANK_KNOWLEDGE_CYCLE` | 422 | 父节点变更会形成循环引用 |
| `QUESTIONBANK_TAG_NOT_FOUND` | 404 | Tag 不存在 |
| `QUESTIONBANK_TAG_DUPLICATE` | 409 | 同名 Tag 已存在 |
| `QUESTIONBANK_TAG_REFERENCED` | 422 | Tag 仍被题目引用，不能删除 |
| `QUESTIONBANK_INTERNAL_ERROR` | 500 | 服务内部错误（已脱敏） |
| `QUESTIONBANK_UNAVAILABLE` | 503 | 依赖服务不可用 |

> 401 Unauthorized 由认证中间件直接返回，无 errorCode（响应体为空）。

## 4. 示例

> 所有请求需携带 `Authorization: Bearer <jwt-token>` 头（以下示例省略）。

### 4.1 创建知识点

```bash
curl -X POST http://localhost:5007/admin/knowledges \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <jwt-token>" \
  -d '{
    "name": "一般现在时",
    "subject": 1,
    "grade": 7
  }'
```

### 4.2 搜索题目

```bash
curl "http://localhost:5007/admin/questions?subject=1&grade=7&keyword=一般现在时&page=1&size=10" \
  -H "Authorization: Bearer <jwt-token>"
```

### 4.3 上传题目

```bash
curl -X POST http://localhost:5007/admin/questions \
  -H "Authorization: Bearer <jwt-token>" \
  -F 'question={"level":1,"type":1,"width":800,"height":600}' \
  -F 'content={"content":"题目内容","correctAnswer":"答案","analysis":"解析"}' \
  -F 'subject=1' \
  -F 'grade=7' \
  -F 'pictures=@/path/to/test.jpg'
```

### 4.4 批量打标签

```bash
curl -X POST http://localhost:5007/admin/question-knowledges/batch-tag \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <jwt-token>" \
  -d '{
    "questionIds": ["q1-uuid", "q2-uuid"],
    "knowledgeId": "k-uuid",
    "subject": 1,
    "grade": 7
  }'
```
