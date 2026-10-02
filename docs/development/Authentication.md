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
- The health probes (`/health/live`, `/health/ready`, and the `/health` readiness alias) do not require authentication
- Static files and the SPA fallback do not require authentication (frontend assets)
- Swagger UI is only exposed in the Development environment and does not require authentication
- `POST /admin/auth/login`, `POST /admin/auth/callback`, and the optional hosted-auth group `/admin/auth/oidc/*` are marked `AllowAnonymous` (see [§6](#6-admin-login) and [§7](#7-optional-hosted-administrator-login))

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

> Note: the 401 response body is controlled by the authentication middleware and is never converted by the ServiceMantle Problem Details boundary.

### 3.2 403 Forbidden

When the user is authenticated but lacks the required role, or the ownership check fails, the endpoint throws `ForbiddenException`, which the ServiceMantle Problem Details boundary converts into `application/problem+json`:

```json
{
  "type": "urn:servicemantle:error:quaestura.forbidden",
  "title": "You are not allowed to perform this operation.",
  "status": 403,
  "correlationId": "0123456789abcdef0123456789abcdef",
  "errorCode": "quaestura.forbidden",
  "quaesturaErrorCode": "QUAESTURA_FORBIDDEN"
}
```

| `quaesturaErrorCode` | HTTP | Trigger |
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

The legacy password endpoints remain temporarily for compatibility pending [#58](https://github.com/philfanzhou/Quaestura/issues/58). The current admin SPA uses hosted sign-in (§7). Legacy callers use this flow:

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

`POST /admin/auth/login`, `POST /admin/auth/callback`, and the hosted-auth endpoints (§7) carry a fixed security response-header baseline, applied by the endpoint-marked ServiceMantle security response-header middleware (registered through the single ServiceMantle composition point, placed after explicit routing and outside the existing exception handler):

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

## 7. Optional hosted administrator login

`SignaCore.Client.AspNetCore` **0.1.11-rc.5** is the official NuGet client. `AdminOidc:Enabled`
defaults to `false`: legacy password endpoints, role callback, and Bearer authorization remain
available, and every `/admin/auth/oidc/*` entry answers 503. **The current SPA requires explicit
enabling**; with it disabled, the login page shows a fixed unavailable message. Legacy backend
retirement is tracked by [#58](https://github.com/philfanzhou/Quaestura/issues/58).

When enabled, register a **Confidential** SignaCore application with Authorization Code and PKCE
S256, `openid profile`, a **PerApplication** access-token audience equal to `ClientId`, and these
exact URIs (replace the origin with your public HTTPS origin):

- Callback: `https://quaestura.example/admin/auth/oidc/callback`
- PostLogout: `https://quaestura.example/admin/auth/oidc/logout/return`

The role issuance callback remains **POST `/admin/auth/callback`** with the existing whitelist.
It is separate from the **GET OIDC callback**. Every hosted sign-in requires an access token with
`admin`: before delegating to the official ticket store, Quaestura validates its RS256 signature
using the existing Bearer Discovery/JWKS manager, issuer, `aud = ClientId`, lifetime, and unique
nonempty `iss`/`sub` matching the ID-token identity that the package verified. Rejected admission
answers fixed 403 without storing a ticket or setting a session cookie. The session retains only
verified ID-token identity and access-token roles, and expires at the earlier package/access-token
expiry. The cookie may remain in the browser after the server ticket expires; it cannot authenticate.

| Key (environment variables replace `:` with `__`) | Requirement |
| --- | --- |
| `AdminOidc:Enabled` | Optional; `false` by default |
| `AdminOidc:Authority` | Required when enabled; same identity authority/JWKS as the resource server |
| `AdminOidc:ClientId` | Required; confidential application's registered id |
| `AdminOidc:ClientSecret` | Required; inject through environment or Consul only |
| `AdminOidc:RedirectUri` | Required; exact public callback URI above |
| `AdminOidc:PostLogoutRedirectUri` | Exact registered PostLogout URI; optional if no return is wanted |
| `AdminOidc:PostLogoutReturnPath` | Package default `/`; this SPA deployment must set `/login?reason=signed_out` |
| `AdminOidc:Scope` | Defaults to `openid profile`; must include `openid` |
| `AdminOidc:AntiforgeryHeaderName` | Keep default `X-SignaCore-CSRF` for the SPA; do not customize |
| `AdminOidc:TicketCapacity` | Official in-memory store capacity; defaults to 10000 |

Invalid or missing enabled configuration fails startup; diagnostics contain option names, never
submitted values. HTTPS is required; the official package accepts explicit loopback HTTP only in
Development/Testing. Keep the authority/JWKS consistent with `IdentityService:Authority`.
`IdentityService:Audience` remains the existing Bearer contract: do not silently change it to the
hosted client's audience. Coordinate any downstream migration from Shared to PerApplication
with its callers before updating that resource-server setting.

An explicit `Authorization` header always selects the original Bearer handler, including malformed
or empty headers, even when a valid session cookie is present. Bearer keeps the original role and
ownership rules and needs no CSRF header. Without that header, the official session handler owns
authentication and validates **all unsafe methods** against its browser-bound antiforgery token.
Missing/wrong CSRF answers 403 with no business effect. Fetch the pair from
`GET /admin/auth/oidc/csrf`, retain its cookie, and send the JSON `token` in `X-SignaCore-CSRF` on
writes. Unauthenticated or expired browser business API requests answer 401 without redirecting.

The official package owns all OIDC requests, state/nonce/PKCE, ID-token checks, ticket keys,
capacity/expiry cleanup, logout locks, prepared-logout URI validation, and return correlation.
`POST /admin/auth/oidc/logout` first revokes the local ticket, then makes at most one preparation
attempt. Quaestura projects only the package's already validated 302 Location into
`200 {"outcome":"prepared","logoutUrl":"..."}` while preserving its cookie changes; the
browser may navigate once at top level. Upstream failure, timeout, or forged URI leaves the local
session revoked and returns `200 {"outcome":"local_only"}`. Missing/wrong logout CSRF answers
403 without revoking or preparing. Cancellation does not restore a revoked ticket, retry, or promise
a JSON response to a disconnected client. Logout return remains the package's one-time flow and
always navigates to the fixed local landing path. Issued downstream tokens remain valid to expiry.

The SPA observes session status with same-origin fetch and shared axios Cookie requests. Only
`authenticated=true && authorization=0` admits management pages. CSRF stays in memory; the shared
client sends it on every unsafe method. Old storage credentials are only removed, never read.
401 clears observations and navigates once to login with a safe management-route redirect; 403
retains the session and never retries the request. Logout failure/lost response triggers one fresh
session check, with a fixed failure or local-only warning; an unknown result blocks further business
requests until explicit recheck. Generations prevent stale responses restoring old observations.

Only exact GET `start`, `callback`, `signin-failed`, and `logout/return` routes, with `Accept:
text/html` at positive quality plus `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document`,
project completed failures to `/login?reason=<fixed value>`. No input query is copied. AccessDenied
maps to `cancelled`, AuthorityUnreachable to `provider_unavailable`, RequiresReauthentication to
`requires_reauthentication`, admission rejection to `denied`, other sign-in failures to
`signin_failed`, and failed one-time logout return to `logout_failed`. Non-HTML/API failures keep
400/403/503 Problem Details and correlation codes; logout POST keeps its JSON contract. The return
adapter runs after package correlation handling, retaining Cookie deletions. Security headers and
no-store remain. The SPA also recognizes `signed_out` and `logout_local_only`; arbitrary/repeated
reasons select only a generic fixed message and never authenticate.

Tokens, ID tokens, and client secrets stay server-side; no new JavaScript token storage is added.
OAuth-required code/state/nonce appear only on top-level authorization/callback navigation, never
in SPA data, error bodies, logs, or spans. Incoming hosted-auth traces are filtered from the
existing instrumentation; the official backchannel disables logging and tracing. Do not enable
unsanitized reverse-proxy access logs on these URLs. Every hosted-auth endpoint receives the
ServiceMantle single-value security-header baseline and fixed safe Problem Details for consumer
failures. Return URLs must be local absolute paths and cannot loop into hosted-auth routes.

Storage is **in-process, single-instance only**. Restarting loses pending sign-ins, sessions, and
logout-return state; users must sign in again. There is no refresh, shared store, database migration,
or downstream access-token revocation. Roll back by restoring the previous (#55-stage) image and its legacy login configuration, then
disabling `AdminOidc:Enabled`. Disabling the flag alone with the new SPA cannot restore login. Stub integration tests exercise the real
Quaestura Host composition, but do not substitute for registered-client, real SignaCore image
acceptance tracked by [#19](https://github.com/philfanzhou/Quaestura/issues/19).
