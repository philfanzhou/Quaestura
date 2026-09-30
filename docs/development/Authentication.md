# Authentication and Authorization

This document defines the authentication and authorization rules for the `Quaestura` service. Every HTTP endpoint must follow these rules.

## 1. Authentication mechanism

### 1.1 JWT Bearer

The service uses **JWT Bearer Token** authentication and trusts JWTs issued by the standalone
[SignaCore](https://github.com/philfanzhou/SignaCore)
service.

| Item | Value |
|----|----|
| Algorithm | RS256 (RSA 2048, asymmetric) |
| Issuer | `https://identity.test.ruoyu.study` (configured per environment) |
| Audience | `QuantumZhou.microservices` |
| Public key distribution | OIDC discovery (`/.well-known/openid-configuration`) + JWKS (`/.well-known/jwks`) |
| Expiry tolerance | 30 seconds (ClockSkew) |

### 1.2 Configuration

Configured through `appsettings.json` or environment variables:

| Configuration key | Environment variable | Required | Description |
|--------|---------|------|------|
| `IdentityService:Authority` | `IdentityService__Authority` | Yes | OIDC metadata URL; HTTPS by default, HTTP deployments must opt in explicitly |
| `IdentityService:Issuer` | `IdentityService__Issuer` | Yes | Exact Issuer of new tokens; not derived from Authority |
| `IdentityService:AdditionalValidIssuers` | `IdentityService__AdditionalValidIssuers__0` | No | Legacy Issuers accepted during a migration window; remove once the migration ends |
| `IdentityService:Audience` | `IdentityService__Audience` | Yes | Must match the Shared Audience of the SignaCore App |
| `IdentityService:RequireHttpsMetadata` | `IdentityService__RequireHttpsMetadata` | Yes | Defaults to `true`; set to `false` only when SignaCore also explicitly allows HTTP |
| `IdentityService:ClockSkewSeconds` | `IdentityService__ClockSkewSeconds` | Yes | Tolerance for token time validation, currently 30 seconds |

### 1.3 JWT claims

The service reads the following claims from the JWT:

| Claim Key | Purpose | How it is read |
|-----------|------|---------|
| `sub` / `ClaimTypes.NameIdentifier` | Current user ID | `User.GetRequiredUserId()` (from `ClaimsPrincipalExtensions`) |
| `role` / `ClaimTypes.Role` | Roles (teacher/assistant/admin/student) | `User.GetRoles()` / `User.IsInRole(...)` / `User.IsStaff()` |

> The extraction methods are provided by the [`Quaestura.Common` shared authentication components](../../src/Common/Authentication/).

## 2. Authorization rules

### 2.1 Global rules

- Every `/admin/*` endpoint requires authentication (`FallbackPolicy = RequireAuthenticatedUser`)
- The `/health` endpoint does not require authentication (health check)
- Static files and the SPA fallback do not require authentication (frontend assets)
- Swagger UI is only exposed in the Development environment and does not require authentication
- `POST /admin/auth/login` and `POST /admin/auth/callback` are the only `/admin/*` endpoints marked `AllowAnonymous` (see [§6 Admin login](#6-admin-login))

### 2.2 Tag endpoint permission matrix

| Endpoint | Required role | Ownership check | Notes |
|------|---------|---------|------|
| `GET /admin/tags` | Any authenticated user (including student) | None | Students need the tag list to tag their mistakes |
| `GET /admin/tags/{id}` | Any authenticated user | None | Same as above |
| `POST /admin/tags` (create) | teacher / assistant / admin | None | Students cannot create tags |
| `POST /admin/tags` (update) | teacher / assistant / admin | **Requires `tag.CreatedBy == current userId`** | Strict ownership |
| `DELETE /admin/tags/{id}` | teacher / assistant / admin | **Requires `tag.CreatedBy == current userId`** | Strict ownership |

### 2.3 Other endpoints (currently only require sign-in)

| Endpoint group | Required role | Notes |
|--------|---------|------|
| `/admin/questions/*` | Any authenticated user | May later be refined to teacher write, student read |
| `/admin/knowledges/*` | Any authenticated user | Same as above |
| `/admin/question-knowledges/*` | Any authenticated user | Same as above |
| `/admin/question-tags/*` | Any authenticated user | Students can tag the questions behind their own mistakes |

### 2.4 Source of UserId

- **No longer read from the request body**: the `UserId` field has been removed from all DTOs
- **Always read from the JWT**: endpoints obtain it through `User.GetRequiredUserId()`
- The database fields `tag.created_by` / `question.user_id` / `knowledge.created_by` / `knowledge.updated_by` are written by the server and cannot be forged by clients

## 3. Error responses

### 3.1 401 Unauthorized

When no JWT is provided, or the JWT is invalid or expired, the ASP.NET Core authentication middleware responds directly:

```json
{
  // Default WWW-Authenticate: Bearer error="invalid_token"
}
```

> Note: the 401 response body is controlled by the authentication middleware and does not pass through `ExceptionHandlingMiddleware`.

### 3.2 403 Forbidden

When the user is authenticated but lacks the required role, or the ownership check fails, the endpoint throws `ForbiddenException`, which `ExceptionHandlingMiddleware` converts into:

```json
{
  "success": false,
  "message": "Only staff can create tags",
  "errorCode": "QUAESTURA_FORBIDDEN"
}
```

| errorCode | HTTP | Trigger |
|-----------|------|---------|
| `QUAESTURA_FORBIDDEN` | 403 | Insufficient role (for example, a student calling a write endpoint) |
| `QUAESTURA_FORBIDDEN_NOT_OWNER` | 403 | Ownership check failed (updating/deleting a tag created by someone else) |

## 4. Testing

### 4.1 Unit tests

- `ClaimsPrincipalExtensions`, role mapping, Issuer/Audience/signature/time boundaries, and Cookie/Header precedence are provided by `Quaestura.Common` (the shared authentication components copied from `ruoyu.common`)
- `JwtBearerApiTests` exercises the real JwtBearer pipeline to cover valid tokens on protected APIs, 401 for unknown Issuers, and anonymous access to `/health`

### 4.2 Integration tests

API-level authentication tests use `WebApplicationFactory<Program>` with a test signing key and do not replace the JwtBearer handler; fine-grained authorization tests for business controllers may still use a test authentication handler.

## 5. History

- **Before this change**: the service had no authentication; the userId was self-reported in the request body and could be forged freely
- **After this change**: JWT Bearer is in place, the userId is read from the JWT, and Tag CRUD has role and ownership checks
- **Not done yet**: finer-grained roles for the Question/Knowledge endpoints, and a question ownership check for question-tag associations (follow-up work)

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
| Callback `user_id` | SignaCore → Quaestura | Only compared against the whitelist; never logged (the role-grant event is logged without any user identifier) |

Login failures are logged without the username.

### 6.3 Response security headers

`POST /admin/auth/login` and `POST /admin/auth/callback` — and only these two JSON endpoints — carry a fixed security response-header baseline, applied by the endpoint-marked ServiceMantle security response-header middleware (registered through the single ServiceMantle composition point, placed after explicit routing and outside the existing exception handler):

```
Cache-Control: no-store
Pragma: no-cache
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'
```

- Success and error responses share the same baseline, including the 500 `QUAESTURA_INTERNAL_ERROR` envelope converted by the existing exception handler and the login 400/502/503 branches; each header is set to exactly one value, and an endpoint attempting to write the same header still ends at the baseline.
- The token response is therefore explicitly non-cacheable; callers (SignaCore for the callback, browsers for login) must not rely on cached copies.
- Unmarked surfaces — the rest of `/admin`, `/health`, dev Swagger, the SPA, and static assets — never receive this API-only policy (no `default-src 'none'`).
- If the response has already started, sent headers are not rewritten; cancellation propagates unchanged.
- Not covered: TLS termination, browser/proxy compliance with `no-store`, HSTS, and CORS remain the deployment's responsibility. Removing the middleware and the endpoint metadata is the complete rollback; nothing is persisted.

### 6.4 Not guaranteed

- Brute-force protection: Quaestura does not rate-limit login. SignaCore sees every attempt as coming from Quaestura, so IP-based limiting in SignaCore can affect all admins at once.
- Callback caller authentication: anyone who can reach `/admin/auth/callback` can ask whether a user ID is whitelisted. It never returns a token and cannot grant privileges by itself.
- Silent token renewal after expiry.
