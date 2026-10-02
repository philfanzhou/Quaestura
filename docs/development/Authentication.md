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
- `POST /admin/auth/login`, `POST /admin/auth/callback`, and the hosted-auth group `/admin/auth/oidc/*` are marked `AllowAnonymous` (see [§6](#6-retired-password-login-and-role-callback) and [§7](#7-hosted-administrator-login))

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
- **Always read from the verified identity** (Bearer JWT or hosted session): endpoints obtain it through `User.GetRequiredUserId()`
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

## 6. Retired password login and role callback

**Breaking change:** `POST /admin/auth/login` no longer signs in or returns the legacy
`success/message/accessToken/expiresIn/expiresAt` envelope. It always returns anonymous **410**
`application/problem+json` with title `Password sign-in is no longer available.`,
`errorCode=quaestura.auth.password_login_retired`,
`quaesturaErrorCode=QUAESTURA_PASSWORD_LOGIN_RETIRED`, and the request correlation id.
Malformed JSON, forms, text, and empty bodies have the same outcome. Neither authentication
nor the endpoint reads or binds the body, forwards passwords, issues tokens, or changes a hosted
session. Only this mapped POST selects the existing Bearer handler before anonymous dispatch;
the retired metadata also returns NoResult before existing Bearer credential/JWKS processing, so
even a supplied JWT with a cold metadata cache causes no identity-service request. Other routes
retain the existing Bearer message-received event and unsafe Cookie CSRF. This does not promise that a client or proxy
never transmitted or recorded an obsolete body, or that a disconnected client receives 410.

The independent **POST `/admin/auth/callback`** remains anonymous and accepts SignaCore's
`{"user_id":"..."}` contract. It returns `{"roles":["admin"]}` for a case-insensitive match in
`AdminPortal:AdminUserIds`, otherwise `{"roles":[]}`. `userId` does not bind. Configure the whitelist
with `AdminPortal__AdminUserIds__0`, `__1`, etc. via environment or Consul. Its grant event never
logs the user identifier. This callback never returns a token or grants privileges by itself;
caller authentication and enumeration protection remain outside its contract. It is distinct
from the GET OIDC callback. Formats are in [ApiSpec §2.6](../overview/ApiSpec.md#26-admin-authentication-adminauth).

`IdentityService:AppId/AppSecret`, `IdentityService__AppId/AppSecret`, and the `start.sh`
`IDENTITY_APP_ID/IDENTITY_APP_SECRET` mapping are retired. Bearer trust still uses
`IdentityService:Authority/Issuer/Audience` (§1.2); the hosted confidential client uses
`AdminOidc:ClientId/ClientSecret` (§7). Do not remove either remaining trust contract.

### 6.1 Response security headers

The retired POST, role callback, and hosted-auth endpoints carry the endpoint-marked
ServiceMantle single-value baseline on success and errors, including safe Problem Details:

```
Cache-Control: no-store
Pragma: no-cache
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'
```

The middleware runs after routing and outside the exception boundary. Endpoint override attempts
lose to the baseline. Unmarked business APIs, health, Swagger, SPA, and assets receive no API-only
CSP. Started headers cannot be rewritten; cancellation propagates. TLS, proxy/browser compliance,
HSTS, and CORS are deployment responsibilities. No state is persisted by this response policy.

## 7. Hosted administrator login

`SignaCore.Client.AspNetCore` **0.1.11-rc.5** is the official NuGet client and hosted sign-in is
the sole administrator UI login. `AdminOidc:Enabled` is retired and completely ignored, including
`false` and malformed strings. With nonblank `Authority`, `ClientId`, `ClientSecret`, and
`RedirectUri`, the package is always registered. With any required field absent, empty, or
whitespace, the service still starts with anonymous SPA/health and independently configured
Bearer APIs; every `/admin/auth/oidc/*` entry returns fixed 503 Problem Details with title
`Hosted sign-in is not configured.`, `errorCode=quaestura.oidc.not_configured`,
`quaesturaErrorCode=QUAESTURA_OIDC_NOT_CONFIGURED`, and correlation id. No official protocol,
session/pending/logout-return state, schemes, or cleanup services are registered. Startup emits
one Error with missing key names only. Missing-field classification precedes optional validation.
The SPA shows a fixed unavailable message and an explicit recheck button.

For deployment, register a **Confidential** SignaCore application with Authorization Code and PKCE
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
| `AdminOidc:Authority` | Required; same identity authority/JWKS as the resource server |
| `AdminOidc:ClientId` | Required; confidential application's registered id |
| `AdminOidc:ClientSecret` | Required; inject through environment or Consul only |
| `AdminOidc:RedirectUri` | Required; exact public callback URI above |
| `AdminOidc:PostLogoutRedirectUri` | Exact registered PostLogout URI; optional if no return is wanted |
| `AdminOidc:PostLogoutReturnPath` | Package default `/`; this SPA deployment must set `/login?reason=signed_out` |
| `AdminOidc:Scope` | Defaults to `openid profile`; must include `openid` |
| `AdminOidc:AntiforgeryHeaderName` | Keep default `X-SignaCore-CSRF` for the SPA; do not customize |
| `AdminOidc:TicketCapacity` | Official in-memory store capacity; defaults to 10000 |

Complete but invalid configuration (URI, capacity, scope, or malformed numbers) still fails startup
through explicit parsing and official ValidateOnStart; diagnostics contain option names, never
submitted values. Missing required fields follow the safe 503 policy above. HTTPS is required; the official package accepts explicit loopback HTTP only in
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
or downstream access-token revocation.

### 7.1 Upgrade and rollback

Before upgrading, register the Confidential Code+PKCE application, its PerApplication audience,
exact callback/PostLogout URIs and role callback; set the whitelist and inject all four required
`AdminOidc` fields, plus `PostLogoutReturnPath=/login?reason=signed_out`. Stop password API callers
and remove obsolete AppId/AppSecret and Enabled environment/Consul keys. Keep Bearer trust
configured independently and keep existing `X-SignaCore-CSRF` and HTTPS security requirements.
Previously issued valid Bearer tokens remain usable through their original expiry; a JWT stored
as a Cookie cannot authenticate this Host. The new opaque session Cookie still works until its
server ticket expires or is revoked; the SPA removes old storage credentials without reading them.

Rollback restores the previous **whole image and its matching configuration**. To recover the
historical password UI, use the pre-#57 image and restore its password AppId/AppSecret settings
and old Enabled strategy. A hosted-only previous image needs its original hosted configuration.
Changing the ignored Enabled key alone cannot recover login. Restart loses process-local sessions
and requires sign-in; database, history, and object storage are unchanged, so never run Down/drop.
Stub and browser tests exercise the real Quaestura Host but do not replace actual registered-client
SignaCore image acceptance tracked by [#19](https://github.com/philfanzhou/Quaestura/issues/19)
and [#54](https://github.com/philfanzhou/Quaestura/issues/54).
