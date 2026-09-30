# Error Handling

> **Breaking change (problem details migration):** failures raised through the exception path —
> previously `{success, message, errorCode}` JSON with an `application/json` content type — now
> return RFC 7807 `application/problem+json` responses produced by the shared ServiceMantle
> Problem Details middleware. HTTP status codes are unchanged. Endpoint-owned responses that
> return their failures explicitly (for example the admin login endpoint) keep the legacy
> business envelope below. Callers that branch on error codes must read the `errorCode` member
> (stable lowercase `quaestura.*` code) or the `quaesturaErrorCode` extension field (the legacy
> uppercase instance code); callers must not depend on any `message` text on the exception path.

## Response formats

### Success response

Every successful endpoint returns:

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

### Explicit endpoint failures (legacy business envelope)

Endpoints that return a failure deliberately (instead of throwing) keep the envelope below with
`application/json`:

```json
{
  "success": false,
  "message": "Human-readable error message in English",
  "errorCode": "QUAESTURA_XXX_YYY"
}
```

### Exception-path failures (ServiceMantle Problem Details)

Any exception escaping an `/admin/*` endpoint is converted by the ServiceMantle Problem Details
middleware (wired as a `/admin`-only branch in `Program.cs`, with the exception mappings
registered in `src/Host/ServiceMantleComposition.cs`) into `application/problem+json`:

```json
{
  "type": "urn:servicemantle:error:quaestura.entity_not_found",
  "title": "The requested resource was not found.",
  "status": 404,
  "correlationId": "0123456789abcdef0123456789abcdef",
  "errorCode": "quaestura.entity_not_found",
  "quaesturaErrorCode": "QUAESTURA_QUESTION_NOT_FOUND"
}
```

- `type`, `title`, `status`, `correlationId`, `errorCode` are always present. The
  `correlationId` is identical to the `x-correlation-id` response header and the log scope, so
  every error response is traceable to its log entry.
- `errorCode` is the mapping's stable lowercase ASCII code (`quaestura.*`), also embedded in
  `type`.
- `quaesturaErrorCode` (extension field) projects the instance-level legacy uppercase
  `QUAESTURA_*` code of the thrown domain exception, preserving the pre-migration code
  contract for callers.
- `quaesturaValidationErrors` (extension field, validation failures only) carries the
  developer-declared validation messages joined with `; `. No other exception content is ever
  projected: exception messages of non-validation exceptions, stack traces, and type names
  never appear in a response.
- Unmapped exceptions return the library's fixed safe 500: `errorCode`
  `http.internal_server_error`, `title` "An unexpected error occurred.". The legacy
  `QUAESTURA_INTERNAL_ERROR` value is retired; callers must not depend on any 500-path error
  code text.
- Once a response has started, an exception no longer rewrites it (documented non-guarantee).
  Caller cancellation is propagated, not converted.

## HTTP status code rules

| Status code | When to use | Error code contract | Examples |
|--------|----------|---------------|------|
| `200 OK` | Request succeeded | — | Successful query, create, update, delete |
| `400 Bad Request` | Request parameter validation failed | `quaestura.validation_failed` / `quaestura.validation_invalid_image` (+ projected `QUAESTURA_VALIDATION_*`) | Invalid ID format, required field empty, invalid image upload |
| `401 Unauthorized` | JWT missing, invalid, or expired | — (returned by the authentication middleware, never converted) | Expired token, invalid signature |
| `403 Forbidden` | Authenticated but insufficient role or failed ownership check | `quaestura.forbidden` (+ projected `QUAESTURA_FORBIDDEN` / `QUAESTURA_FORBIDDEN_NOT_OWNER`) | Student calling a tag write endpoint, updating a tag created by someone else |
| `404 Not Found` | Resource does not exist | `quaestura.entity_not_found` (+ projected `QUAESTURA_<RESOURCE>_NOT_FOUND`) | Question not found |
| `409 Conflict` | Resource conflict | `quaestura.conflict` (+ projected duplicate code) | Duplicate creation |
| `422 Unprocessable Entity` | Business precondition not met | `quaestura.business_precondition_failed` (+ projected precondition code) | Deleting a referenced knowledge point |
| `500 Internal Server Error` | Internal service error | Declared domain failures: `quaestura.domain_error`; `InvalidOperationException`: `quaestura.invalid_operation`; anything unmapped: `http.internal_server_error` | Database exception, unhandled exception |
| `503 Service Unavailable` | Login endpoint not configured | Explicit business JSON envelope (not the exception path) | Admin login without IdentityService configuration |

## Exception handling

### Custom exception types

The domain exception types live in `src/Service/Middleware/DomainExceptions.cs`:

| Exception | Purpose | HTTP status code | Stable `errorCode` | Projected `quaesturaErrorCode` |
|------|------|-------------|-----------|-----------|
| `EntityNotFoundException` | Entity does not exist | 404 | `quaestura.entity_not_found` | `QUAESTURA_<RESOURCE>_NOT_FOUND` |
| `ValidationException` (FluentValidation) | Parameter validation failed | 400 | `quaestura.validation_failed` | `QUAESTURA_VALIDATION_FAILED` (+ `quaesturaValidationErrors`) |
| `InvalidOperationException` with "Image" in the message (thrown by `ImageValidationHelper`) | Invalid image upload | 400 | `quaestura.validation_invalid_image` | `QUAESTURA_VALIDATION_INVALID_IMAGE` (+ `quaesturaValidationErrors`) |
| `BusinessPreconditionException` | Business precondition not met | 422 | `quaestura.business_precondition_failed` | the instance-level code (e.g. `QUAESTURA_TAG_REFERENCED`) |
| `ForbiddenException` | Insufficient role or failed ownership check | 403 | `quaestura.forbidden` | `QUAESTURA_FORBIDDEN` or `QUAESTURA_FORBIDDEN_NOT_OWNER` |
| `DomainException` (base type thrown directly) with declared status 409 | Duplicate resource | 409 | `quaestura.conflict` | the instance-level code (e.g. `QUAESTURA_TAG_DUPLICATE`) |
| `DomainException` (base type thrown directly) with declared status 500 | Declared failure | 500 | `quaestura.domain_error` | the instance-level code (e.g. `QUAESTURA_TAG_CREATE_FAILED`) |
| `DomainException` with any other status (fail-closed fallback) | Undeclared status | 500 | `quaestura.unexpected_domain_status` | the instance-level code |
| Any other unhandled exception | Unmapped failure | 500 | `http.internal_server_error` (library default) | — |

### Global exception boundary

Every `/admin/*` endpoint is covered by the ServiceMantle Problem Details middleware
(`UseServiceMantleProblemDetails` in a `/admin`-only `UseWhen` branch in `Program.cs`):

- Mappings are exact-type and registered up front in `src/Host/ServiceMantleComposition.cs`;
  the library validates them at startup (`AddExceptionMapping` /
  `AddConditionalExceptionMapping` with ordered candidates for `DomainException` and
  `InvalidOperationException`).
- The exception message never enters the response except through the reviewed
  `quaesturaValidationErrors` whitelist field described above; 500 responses leak no details.
- `/health`, the SPA branch, static assets, and dev Swagger are outside the branch and keep
  their existing behavior.
- Minimal-API binding failures (malformed JSON body, wrong content type) are handled by the
  framework before the endpoint runs and are not converted into problem responses.

## Error message conventions

1. Error messages are in **English**
2. Error messages should be concise and clear, without technical details (such as SQL statements or stack traces)
3. The same kind of error uses the same wording across services
4. User-facing business hints may be localized, but API response messages are always in English
5. The fixed Problem Details titles are declared once in `ServiceMantleComposition.cs` and must
   stay public-safe: no exception content, no internal identifiers

## Parameter validation

- Use FluentValidation to validate HTTP request DTOs
- Validation failures throw `ValidationException` (converted to the 400
  `quaestura.validation_failed` problem by the global boundary, with the joined messages in
  `quaesturaValidationErrors`)
- IDs are parsed with `Guid.TryParse`; failures throw `ValidationException` with errorCode `QUAESTURA_VALIDATION_INVALID_ID`

## Logging conventions

- Use structured logging placeholders: `logger.LogInformation("Created question {QuestionId}", id)` rather than string interpolation
- Always pass the exception object: `logger.LogError(ex, "...")` rather than `logger.LogError(ex.Message, ...)`
- Expected `EntityNotFoundException`s are logged at `Warning` level
- The Problem Details middleware logs every converted failure with its stable error code and
  correlation id at `Error` level
- Do not log image binary content, tokens, passwords, or other sensitive information
- "Soft failures" such as a failed OSS delete use `Warning`; failures of the main flow use `Error`
