# Quaestura API Specification

This document defines the complete HTTP API of the `Quaestura` service. Every endpoint returns a structured JSON response and follows [`docs/development/ErrorHandling.md`](../development/ErrorHandling.md).

## 1. General conventions

### 1.1 Authentication

Every `/admin/*` endpoint requires JWT Bearer Token authentication (see [Authentication.md](../development/Authentication.md)):

```
Authorization: Bearer <jwt-token>
```

The JWT is issued by SignaCore and contains the `sub` (userId) and `role` (teacher/assistant/admin/student) claims. The server reads the userId from the JWT; **request bodies no longer contain a userId field**.

A missing or invalid JWT → 401 Unauthorized (returned by the authentication middleware).

> Exception: `POST /admin/auth/login` and `POST /admin/auth/callback` are anonymous (see [§2.6](#26-admin-authentication-adminauth)).

### 1.2 Response format

**Success response (single record)**:

```json
{
  "success": true,
  "data": { ... }
}
```

**Success response (list/paginated)**:

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

**Error response**:

```json
{
  "success": false,
  "message": "Human-readable error message in English",
  "errorCode": "QUAESTURA_XXX_YYY"
}
```

### 1.3 Common fields

- All ID fields are UUID strings (such as `00000000-0000-0000-0000-000000000001`)
- All timestamps are ISO 8601 UTC strings (such as `2026-07-02T10:00:00.000Z`)
- `subject` and `grade` are integers defined by the [`Quaestura.Common` shared constants](../../src/Common/Constants/)
- All messages and error messages are in English

### 1.4 Common query parameters

| Parameter | Type | Required | Default | Description |
|------|------|------|--------|------|
| `page` | int | No | 1 | Page number (starting at 1) |
| `size` | int | No | 10 | Page size (maximum 100) |

## 2. Endpoints

### 2.1 Question management `/admin/questions`

#### GET `/admin/questions`

Search/paginate questions.

**Query parameters**:

| Parameter | Type | Required | Description |
|------|------|------|------|
| `keyword` | string | No | Stem keyword (matches `question_content.content`) |
| `level` | int | No | Difficulty level (applied when > 0) |
| `type` | int | No | Question type (applied when > 0) |
| `subject` | int | **Yes** | Subject |
| `grade` | int | **Yes** | Grade |
| `tagId` | UUID | No | Only return questions with this tag |
| `page` | int | No | Default 1 |
| `size` | int | No | Default 10, maximum 100 |

**Response 200**:

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
      "content": "Question stem",
      "correctAnswer": "Correct answer",
      "analysis": "Explanation",
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

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — subject/grade missing or invalid

#### GET `/admin/questions/{id}`

Get question details.

**Path parameters**:

| Parameter | Type | Description |
|------|------|------|
| `id` | UUID | Question ID |

**Response 200**:

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
    "content": "Question stem",
    "correctAnswer": "Correct answer",
    "analysis": "Explanation",
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

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID` — invalid ID format
- 404 `QUAESTURA_QUESTION_NOT_FOUND` — question does not exist

#### POST `/admin/questions`

Upload/update a question (multipart/form-data).

**Form fields**:

| Field | Type | Required | Description |
|------|------|------|------|
| `question` | string (JSON) | **Yes** | `QuestionPayload` JSON string |
| `content` | string (JSON) | No | `QuestionContentPayload` JSON string |
| `subject` | int (form) | **Yes** | Subject |
| `grade` | int (form) | **Yes** | Grade |
| `pictures` | file[] | No | Image files (JPEG/PNG/GIF/WebP/BMP, ≤ 10MB each) |

> `userId` is read from the JWT (`sub` claim), no longer from a form field.

**QuestionPayload**:

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

**QuestionContentPayload**:

```json
{
  "content": "Question stem",
  "correctAnswer": "Correct answer",
  "analysis": "Explanation"
}
```

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — field validation failed
- 400 `QUAESTURA_VALIDATION_INVALID_IMAGE` — invalid image format/size

#### DELETE `/admin/questions/{id}`

Delete a question.

**Path parameters**:

| Parameter | Type | Description |
|------|------|------|
| `id` | UUID | Question ID |

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID` — invalid ID format
- 404 `QUAESTURA_QUESTION_NOT_FOUND` — question does not exist

### 2.2 Knowledge point management `/admin/knowledges`

#### GET `/admin/knowledges`

List/search/paginate knowledge points.

**Query parameters**:

| Parameter | Type | Required | Description |
|------|------|------|------|
| `parentId` | UUID | No | Parent node ID (top-level nodes are returned when omitted) |
| `grade` | int | No | Filter by grade |
| `subject` | int | No | Filter by subject (must be used together with `grade`) |
| `name` | string | No | Fuzzy search by name (LIKE) |
| `page` | int | No | Default 1 |
| `size` | int | No | Default 10, maximum 100 |

**Response 200**:

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "parentId": "uuid or null",
      "name": "Simple present tense",
      "description": "Description",
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

Knowledge point details.

**Response 200**:

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

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID`
- 404 `QUAESTURA_KNOWLEDGE_NOT_FOUND`

#### POST `/admin/knowledges`

Create or update a knowledge point (updates when id is present, creates when empty).

**Request body**:

```json
{
  "id": "uuid (optional)",
  "parentId": "uuid (optional)",
  "name": "Knowledge point name",
  "description": "Description (optional)",
  "subject": 1,
  "grade": 7
}
```

> `userId` is read from the JWT and written to `created_by` / `updated_by`.

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED`
- 409 `QUAESTURA_KNOWLEDGE_DUPLICATE` — the same (subject, grade, name) already exists
- 422 `QUAESTURA_KNOWLEDGE_REFERENCED` — referenced by questions, cannot be updated
- 422 `QUAESTURA_KNOWLEDGE_CYCLE` — the parent change would create a cycle

#### DELETE `/admin/knowledges/{id}`

Delete a knowledge point.

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID`
- 404 `QUAESTURA_KNOWLEDGE_NOT_FOUND`
- 422 `QUAESTURA_KNOWLEDGE_REFERENCED`

### 2.3 Question–knowledge point associations `/admin/question-knowledges`

#### POST `/admin/question-knowledges/batch-tag`

Tag multiple questions with the same knowledge point in one batch.

**Request body**:

```json
{
  "questionIds": ["uuid", "uuid"],
  "knowledgeId": "uuid",
  "subject": 1,
  "grade": 7
}
```

**Response 200**:

```json
{
  "success": true,
  "data": { "taggedCount": 2 }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED`
- 404 `QUAESTURA_KNOWLEDGE_NOT_FOUND` — knowledge point does not exist or subject/grade does not match

#### GET `/admin/question-knowledges`

Query associations (one of two modes).

**Query parameters** (exactly one of `questionId` or `knowledgeId` must be provided):

| Parameter | Type | Required | Description |
|------|------|------|------|
| `questionId` | UUID | Conditional | Query all knowledge point associations of a question |
| `knowledgeId` | UUID | Conditional | Query all question IDs of a knowledge point (paginated) |
| `page` | int | No | Default 1 (`knowledgeId` mode only) |
| `size` | int | No | Default 10 (`knowledgeId` mode only) |

**Response 200 (questionId mode)**:

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

**Response 200 (knowledgeId mode)**:

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

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — neither questionId nor knowledgeId provided

#### DELETE `/admin/question-knowledges`

Remove all knowledge point associations of a question.

**Query parameters**:

| Parameter | Type | Required | Description |
|------|------|------|------|
| `questionId` | UUID | **Yes** | Question ID |

**Response 200**:

```json
{
  "success": true,
  "data": { "questionId": "uuid", "removed": true }
}
```

### 2.4 Tag management `/admin/tags`

A Tag is a free-form label parallel to Knowledge (no hierarchy, not bound to subject/grade). It is meant for ad-hoc tagging of questions by teachers ("Midterm focus", "Frequently tested", "Error-prone", and so on).

#### Permission matrix

| Endpoint | Required role | Ownership check |
|------|---------|---------|
| `GET /admin/tags` | Any authenticated user (including student) | None |
| `GET /admin/tags/{id}` | Any authenticated user | None |
| `POST /admin/tags` (create) | teacher / assistant / admin | None |
| `POST /admin/tags` (update) | teacher / assistant / admin | Requires `CreatedBy == current userId` |
| `DELETE /admin/tags/{id}` | teacher / assistant / admin | Requires `CreatedBy == current userId` |

#### GET `/admin/tags`

List/search tags.

**Query parameters**:

| Parameter | Type | Required | Description |
|------|------|------|------|
| `name` | string | No | Fuzzy search by name (`Contains` match, portable across databases) |
| `sortBy` | string | No | `usageCount` (descending by reference count) or empty (ascending by `name`) |
| `page` | int | No | Default 1 |
| `size` | int | No | Default 10, maximum 100 |

**Response 200**:

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "name": "Midterm focus",
      "color": "#FF6B6B",
      "description": "Key points for the midterm exam",
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

Tag details.

**Response 200**:

```json
{
  "success": true,
  "data": {
    "id": "uuid",
    "name": "Midterm focus",
    "color": "#FF6B6B",
    "description": "Key points for the midterm exam",
    "createdBy": "uuid",
    "createdAt": "2026-07-02T10:00:00.000Z",
    "usageCount": 12
  }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID`
- 404 `QUAESTURA_TAG_NOT_FOUND`

#### POST `/admin/tags`

Create or update a tag (updates when id is present, creates when empty).

**Role**: teacher / assistant / admin (students cannot call it; returns 403)

**Ownership**: in update mode, only a user with `tag.CreatedBy == current userId` can update it; otherwise 403 is returned

**Request body**:

```json
{
  "id": "uuid (optional)",
  "name": "Midterm focus",
  "color": "#FF6B6B (optional, HEX format)",
  "description": "Key points for the midterm exam (optional)"
}
```

> `userId` is read from the JWT and written to `created_by`.

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid" }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — name missing or invalid color format
- 403 `QUAESTURA_FORBIDDEN` — called by a student
- 403 `QUAESTURA_FORBIDDEN_NOT_OWNER` — updating a tag created by someone else
- 409 `QUAESTURA_TAG_DUPLICATE` — a tag with the same name already exists

#### DELETE `/admin/tags/{id}`

Delete a tag (only allowed when `usageCount == 0`).

**Role**: teacher / assistant / admin

**Ownership**: only a user with `tag.CreatedBy == current userId` can delete it; otherwise 403 is returned

**Response 200**:

```json
{
  "success": true,
  "data": { "id": "uuid", "deleted": true }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_INVALID_ID`
- 403 `QUAESTURA_FORBIDDEN` — called by a student
- 403 `QUAESTURA_FORBIDDEN_NOT_OWNER` — deleting a tag created by someone else
- 404 `QUAESTURA_TAG_NOT_FOUND`
- 422 `QUAESTURA_TAG_REFERENCED` — the tag is still referenced by questions

### 2.5 Question–tag associations `/admin/question-tags`

#### POST `/admin/question-tags/batch-tag`

Tag multiple questions with multiple tags in one batch (existing associations are skipped automatically).

**Role**: any authenticated user (students can call it to tag the questions behind their own mistakes)

**Request body**:

```json
{
  "questionIds": ["uuid", "uuid"],
  "tagIds": ["uuid", "uuid"]
}
```

**Response 200**:

```json
{
  "success": true,
  "data": { "taggedCount": 4 }
}
```

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — questionIds/tagIds empty
- 404 `QUAESTURA_TAG_NOT_FOUND` — any tagId does not exist

#### GET `/admin/question-tags`

Query associations (one of two modes).

**Query parameters** (exactly one of `questionId` or `tagId` must be provided):

| Parameter | Type | Required | Description |
|------|------|------|------|
| `questionId` | UUID | Conditional | Query all tag associations of a question |
| `tagId` | UUID | Conditional | Query all questions associated with a tag (paginated) |
| `page` | int | No | Default 1 (`tagId` mode only) |
| `size` | int | No | Default 10 (`tagId` mode only) |

**Response 200 (questionId mode)**:

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "questionId": "uuid",
      "tagId": "uuid",
      "tagName": "Midterm focus",
      "tagColor": "#FF6B6B",
      "createdAt": "2026-07-02T10:00:00.000Z"
    }
  ]
}
```

**Response 200 (tagId mode)**:

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

**Errors**:
- 400 `QUAESTURA_VALIDATION_FAILED` — neither questionId nor tagId provided

#### DELETE `/admin/question-tags`

Remove associations.

**Query parameters**:

| Parameter | Type | Required | Description |
|------|------|------|------|
| `questionId` | UUID | **Yes** | Question ID |
| `tagId` | UUID | No | When omitted, removes all tags of the question; when provided, removes only that tag |

**Response 200**:

```json
{
  "success": true,
  "data": { "questionId": "uuid", "tagId": "uuid (if specified)", "removed": true }
}
```

### 2.6 Admin authentication `/admin/auth`

Both endpoints are anonymous. See [Authentication.md §6](../development/Authentication.md#6-admin-login) for the flow and configuration. Unlike the other endpoints, failures return `{"success": false, "message": "..."}` without an `errorCode`.

#### POST `/admin/auth/login`

Exchanges an admin's username and password for a SignaCore access token. The username and password are forwarded to SignaCore as-is (no trimming or case folding).

**Request body**:

```json
{
  "username": "admin",
  "password": "********"
}
```

**Response 200**:

```json
{
  "success": true,
  "message": "Login successful",
  "accessToken": "<jwt>",
  "expiresIn": 3600,
  "expiresAt": 1790000000
}
```

`expiresIn` is in seconds; `expiresAt` is a Unix timestamp in seconds. No refresh token or user info is returned.

**Errors**:

| HTTP | message | When |
|------|---------|------|
| 400 | `Username and password are required.` | `username` or `password` is missing, empty, or whitespace |
| 400 | SignaCore's message, or `Login failed.` when it is empty | SignaCore rejected the credentials (`success: false`) |
| 502 | `Identity service unavailable.` | SignaCore returned a non-2xx status, an empty or unreadable body, or no access token; or the request failed or timed out (30 seconds) |
| 503 | `Admin login is not configured.` | `IdentityService:AppId` or `IdentityService:AppSecret` is not configured |

#### POST `/admin/auth/callback`

Role callback invoked by SignaCore while it issues a token for the Quaestura application.

**Request body** (as sent by SignaCore):

```json
{
  "user_id": "<SignaCore user id>"
}
```

**Response 200**:

```json
{
  "roles": ["admin"]
}
```

`roles` is `["admin"]` when `user_id` is listed in `AdminPortal:AdminUserIds` (case-insensitive); otherwise, including a missing body or empty `user_id`, it is `[]`.

## 3. Error code dictionary

| errorCode | HTTP status | Description |
|-----------|----------|------|
| `QUAESTURA_VALIDATION_FAILED` | 400 | Request parameter validation failed (FluentValidation) |
| `QUAESTURA_VALIDATION_INVALID_ID` | 400 | ID is not a UUID |
| `QUAESTURA_VALIDATION_INVALID_IMAGE` | 400 | Invalid image format/size |
| `QUAESTURA_FORBIDDEN` | 403 | Authenticated but insufficient role (for example, a student calling a tag write endpoint) |
| `QUAESTURA_FORBIDDEN_NOT_OWNER` | 403 | Ownership check failed (updating/deleting a tag created by someone else) |
| `QUAESTURA_QUESTION_NOT_FOUND` | 404 | Question does not exist |
| `QUAESTURA_KNOWLEDGE_NOT_FOUND` | 404 | Knowledge point does not exist |
| `QUAESTURA_KNOWLEDGE_DUPLICATE` | 409 | The same (subject, grade, name) already exists |
| `QUAESTURA_KNOWLEDGE_REFERENCED` | 422 | Knowledge point is referenced and cannot be updated/deleted |
| `QUAESTURA_KNOWLEDGE_CYCLE` | 422 | The parent change would create a circular reference |
| `QUAESTURA_TAG_NOT_FOUND` | 404 | Tag does not exist |
| `QUAESTURA_TAG_DUPLICATE` | 409 | A tag with the same name already exists |
| `QUAESTURA_TAG_REFERENCED` | 422 | Tag is still referenced by questions and cannot be deleted |
| `QUAESTURA_INTERNAL_ERROR` | 500 | Internal service error (redacted) |
| `QUAESTURA_UNAVAILABLE` | 503 | Dependency unavailable |

> 401 Unauthorized is returned directly by the authentication middleware, without an errorCode (empty response body).

## 4. Examples

> Every request must carry the `Authorization: Bearer <jwt-token>` header (all examples below include it).

### 4.1 Create a knowledge point

```bash
curl -X POST http://localhost:5007/admin/knowledges \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <jwt-token>" \
  -d '{
    "name": "Simple present tense",
    "subject": 1,
    "grade": 7
  }'
```

### 4.2 Search questions

```bash
curl "http://localhost:5007/admin/questions?subject=1&grade=7&keyword=present%20tense&page=1&size=10" \
  -H "Authorization: Bearer <jwt-token>"
```

### 4.3 Upload a question

```bash
curl -X POST http://localhost:5007/admin/questions \
  -H "Authorization: Bearer <jwt-token>" \
  -F 'question={"level":1,"type":1,"width":800,"height":600}' \
  -F 'content={"content":"Question content","correctAnswer":"Answer","analysis":"Explanation"}' \
  -F 'subject=1' \
  -F 'grade=7' \
  -F 'pictures=@/path/to/test.jpg'
```

### 4.4 Batch tagging

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
