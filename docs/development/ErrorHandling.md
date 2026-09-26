# Error Handling

## Response format

Every HTTP endpoint returns the following structure:

### Success response

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

- `data`: business data (object or array)
- `total`, `page`, `pageSize`, `totalPages`: only present for paginated results
- Single-record queries: `{success: true, data: { ... }}`

### Error response

```json
{
  "success": false,
  "message": "Human-readable error message in English",
  "errorCode": "QUAESTURA_XXX_YYY"
}
```

Error code naming convention: `QUAESTURA_<RESOURCE>_<ERROR_TYPE>`, all uppercase, separated by underscores.

## HTTP status code rules

| Status code | When to use | errorCode prefix | Examples |
|--------|----------|---------------|------|
| `200 OK` | Request succeeded | — | Successful query, create, update, delete |
| `400 Bad Request` | Request parameter validation failed | `QUAESTURA_VALIDATION_*` | Invalid ID format, required field empty, file too large |
| `401 Unauthorized` | JWT missing, invalid, or expired | — (returned by the authentication middleware) | Expired token, invalid signature |
| `403 Forbidden` | Authenticated but insufficient role or failed ownership check | `QUAESTURA_FORBIDDEN*` | Student calling a tag write endpoint, updating a tag created by someone else |
| `404 Not Found` | Resource does not exist | `QUAESTURA_<RESOURCE>_NOT_FOUND` | Question not found |
| `409 Conflict` | Resource conflict | `QUAESTURA_<RESOURCE>_CONFLICT` | Duplicate creation |
| `422 Unprocessable Entity` | Business precondition not met | `QUAESTURA_PRECONDITION_*` | Deleting a referenced knowledge point |
| `500 Internal Server Error` | Internal service error | `QUAESTURA_INTERNAL_ERROR` | Database exception, unhandled exception |
| `503 Service Unavailable` | Dependency unavailable | `QUAESTURA_UNAVAILABLE` | OSS unreachable |

## Exception handling

### Custom exception types

| Exception | Purpose | HTTP status code | errorCode |
|------|------|-------------|-----------|
| `DomainException` (and subclasses) | Business rule violation | Depends on the subclass | Depends on the subclass |
| `EntityNotFoundException` | Entity does not exist | 404 | `QUAESTURA_<RESOURCE>_NOT_FOUND` |
| `ValidationException` | Parameter validation failed | 400 | `QUAESTURA_VALIDATION_*` |
| `BusinessPreconditionException` | Business precondition not met | 422 | `QUAESTURA_PRECONDITION_*` |
| `ForbiddenException` | Insufficient role or failed ownership check | 403 | `QUAESTURA_FORBIDDEN` or `QUAESTURA_FORBIDDEN_NOT_OWNER` |

### Global exception middleware

Every WebAPI endpoint must be handled by `ExceptionHandlingMiddleware` (in `src/Service/Middleware/ExceptionHandlingMiddleware.cs`):

- `DomainException` subclasses → mapped to the corresponding HTTP status code + business error code
- `ValidationException` (FluentValidation) → 400 + `QUAESTURA_VALIDATION_FAILED`; the message is the list of validation errors
- `InvalidOperationException` (thrown by `ImageValidationHelper`) → 400 + `QUAESTURA_VALIDATION_INVALID_IMAGE`
- `BadHttpRequestException` → 400
- Any other unhandled exception → 500 + `QUAESTURA_INTERNAL_ERROR`; the original message is redacted (only "Internal server error" is shown) and the exception details are written to the log

## Error message conventions

1. Error messages are in **English** (per the `30-backend-routing.md` coding conventions)
2. Error messages should be concise and clear, without technical details (such as SQL statements or stack traces)
3. The same kind of error uses the same wording across services
4. User-facing business hints may be localized, but API response messages are always in English

## Parameter validation

- Use FluentValidation to validate HTTP request DTOs
- Validation failures throw `ValidationException` (converted to 400 by the global middleware)
- IDs are parsed with `Guid.TryParse`; failures throw `ValidationException` with errorCode `QUAESTURA_VALIDATION_INVALID_ID`

## Logging conventions

- Use structured logging placeholders: `logger.LogInformation("Created question {QuestionId}", id)` rather than string interpolation
- Always pass the exception object: `logger.LogError(ex, "...")` rather than `logger.LogError(ex.Message, ...)`
- Expected `EntityNotFoundException`s are logged at `Warning` level
- Unhandled exceptions are logged at `Error` level
- Do not log image binary content, tokens, passwords, or other sensitive information
- "Soft failures" such as a failed OSS delete use `Warning`; failures of the main flow use `Error`
