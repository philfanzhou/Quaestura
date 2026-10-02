# Quaestura Admin Frontend Spec

This document defines the tech stack, directory structure, routes, and backend API integration of the question-bank admin frontend.

## 1. Overview

The admin frontend is a Vue 3 single-page application that runs in the **same Docker container** as the Quaestura backend (port 5007 serves both the API and the SPA). **Authentication is required**: users sign in on `/login` through a top-level SignaCore hosted-login navigation, and same-origin API requests use the server session Cookie and CSRF (see §5.1–§5.2).

**Location**: `frontend/` (next to the backend service, following the doclibrary frontend layout).

## 2. Tech stack

| Item | Technology |
|------|------|
| Framework | Vue 3.5 + Composition API (`<script setup>`) |
| Language | TypeScript |
| Build tool | Vite |
| UI approach | **Hand-written CSS** (no UI component library; reuses the doclibrary design system) |
| Routing | vue-router (history mode) |
| HTTP client | Axios |

### Removed dependencies (legacy)

- `element-plus` / `@element-plus/icons-vue` — all UI moved to hand-written CSS, and ElMessage was replaced by a native toast
- Earlier versions imported all of Element Plus; it has been removed to keep the frontend lightweight

## 3. Directory structure

```
frontend/     # Next to the backend service
├── package.json
├── package-lock.json
├── vite.config.ts                            # Port 8091, proxy /admin → :5007
├── tsconfig.json / .app.json / .node.json
├── index.html                                # Contains <title>__APP_TITLE__</title>
├── public/favicon.svg
├── docs/admin-frontend-spec.md               # This file
└── src/
    ├── main.ts                               # createApp + router (no ElementPlus)
    ├── App.vue                               # Layout (hand-written sidebar + router-view)
    ├── style.css                             # Global styles (reuses the doclibrary design system)
    ├── env.d.ts                              # import.meta.env types
    ├── router/index.ts                       # 4 routes + auth guard
    ├── views/
    │   ├── LoginView.vue                     # Login page (public route)
    │   ├── QuestionView.vue                  # Question management (list + search + view + delete)
    │   ├── KnowledgeView.vue                 # Knowledge point management (list + create/update/delete)
    │   └── TagView.vue                       # Tag management (list + create/update/delete)
    ├── services/
    │   ├── auth.ts                           # In-memory session/CSRF observations and hosted navigation
    │   ├── httpClient.ts                     # Shared axios instance (Cookie + CSRF + 401 interceptors)
    │   ├── questionApi.ts
    │   ├── knowledgeApi.ts
    │   └── tagApi.ts
    ├── types/
    │   └── index.ts                          # Response DTO types
    └── components/
        └── ApiErrorHandler.ts                # getApiErrorMessage utility
```

## 4. Styling conventions

### 4.1 Reuse the doclibrary design system

`style.css` reuses doclibrary's CSS variables and shared component classes, including:

- CSS variables: `--primary-color`, `--text-primary`, `--card-bg`, `--border-color`, `--radius-md`, `--shadow-sm`, etc.
- Shared classes: `.btn`/`.btn-primary`/`.btn-secondary`/`.btn-danger`/`.btn-small`, `.card`/`.card-header`/`.card-body`, `.data-table`, `.pagination-bar`, `.tag`, `.form-group`/`.input-wrap`/`.select-wrap`, `.empty-state`, `.spinner`, `.status-badge`
- Layout classes: `.admin-layout`/`.sidebar`/`.main-content`/`.top-header`/`.content-area`/`.page-header`

### 4.2 No inline styles

Apart from dynamic bindings (`:style` for color swatches, progress bar widths, and similar), static inline `style="..."` attributes are not allowed.

### 4.3 Native toast instead of ElMessage

After removing Element Plus, `ElMessage`/`ElMessageBox` were replaced by:
- Messages: a native toast function (creates a DOM element attached to body that disappears after 3 seconds)
- Confirmation dialogs: a custom ConfirmDialog component (overlay + card + confirm/cancel buttons)

## 5. Routes

| Path | View | meta.title |
|------|------|-----------|
| `/` | redirect to `/questions` | — |
| `/login` | LoginView | `登录` (Login, `meta.public: true`) |
| `/questions` | QuestionView | `题目管理` (Question management) |
| `/knowledges` | KnowledgeView | `知识点管理` (Knowledge point management) |
| `/tags` | TagView | `标签管理` (Tag management) |

`router.beforeEach` sets `document.title = ${to.meta.title} - ${__APP_TITLE__}`.

### 5.1 Login flow (route guard)

`router.beforeEach` reads `GET /admin/auth/oidc/session` once per document, sharing in-flight
reads with concurrent guards. Only `authenticated=true && authorization=0` (Allowed) admits an
administrator. Refreshes and deep links re-read the server. Missing, unknown, expired, and
restarted sessions all require a new explicit sign-in; `requiresReauthentication` does not prove
that this browser was previously signed in.

- Protected routes redirect to `/login?redirect=<safe path>`; Allowed sessions on `/login` return
  to that path. Allowed targets are `/questions`, `/knowledges`, and `/tags` (or `/` normalized to
  `/questions`) with legal query/hash. Repeated redirect values, external addresses, controls,
  backslashes, auth parameters, and login/auth loops fall back to `/questions`.
- The login page preserves its layout and provides one “使用 SignaCore 登录” button. A navigation
  latch permits at most one top-level `/admin/auth/oidc/start` navigation per document; the SPA
  never fetches authorization/callback URLs or automatically starts a login loop.
- A session-service failure shows a fixed unavailable message and an explicit recheck button.
  The UI requires all four nonblank hosted fields (Authority, ClientId, ClientSecret, RedirectUri).
  `AdminOidc:Enabled` is ignored; the retired password API always returns 410.
- `reason` selects only fixed messages for `cancelled`, `denied`, `signin_failed`,
  `provider_unavailable`, `requires_reauthentication`, `signed_out`, `logout_local_only`, and
  `logout_failed`. Unknown/repeated reasons produce a generic message and never change admission.

### 5.2 Session, CSRF, and shared HTTP client

`services/auth.ts` retains observations and the CSRF token in JS memory only. On loading it removes
`quaesturaAuthToken` and `quaesturaAuthExpiresAt` without reading their values; storage exceptions
cannot block login. The `qbSidebarCollapsed` preference remains optional persistent UI state.

All API services reuse `httpClient` (`timeout: 20000`, `withCredentials: true`). It does not inject
Authorization. Every unsafe method obtains the single-flight `GET /admin/auth/oidc/csrf` pair and
sends `X-SignaCore-CSRF`; GET/HEAD/OPTIONS/TRACE do not require it. The deployed SPA uses this
default header, so do not customize `AdminOidc:AntiforgeryHeaderName`. CSRF acquisition failure or
caller cancellation dispatches no business write. No 401/403/network/cancellation branch retries
a write. A generation change makes old session/CSRF responses unusable after logout or 401.

Business 401 invalidates observations and navigates at most once to
`/login?reason=requires_reauthentication&redirect=<safe current route>`; on `/login` it stays put.
Business/CSRF 403 remains a page error and retains the session. SignaCore tokens and secrets never
enter SPA data or browser storage. OAuth code/state/nonce belong only to top-level protocol URLs.

### 5.3 Logout

The header button shares one in-flight logout and is disabled while pending. A valid-CSRF POST to
`/admin/auth/oidc/logout` returns either `prepared` (navigate once to the package-validated
`logoutUrl`) or `local_only` (show the fixed warning that the SignaCore session may still exist).
Deploy with `PostLogoutReturnPath=/login?reason=signed_out`.

A CSRF refusal, cancellation, or lost response does not claim successful logout or replay the
POST. One new session read reconciles the result: Allowed retains access with a fixed failure
message; unauthenticated shows the local-only warning; an unavailable result blocks business
access until an explicit recheck. Server revocation is never undone by the SPA. Cross-tab immediate
synchronization, upstream confirmation after disconnect, and token refresh are not guaranteed.

## 6. Backend API integration

### 6.1 API path convention

Frontend code uses relative `/admin/...` paths; in dev they go through the Vite proxy, in production they are served same-origin by the backend.

### 6.2 Vite proxy (dev)

```ts
server: {
  port: 8091,
  proxy: {
    '/admin': { target: 'http://localhost:5007', changeOrigin: true },
  },
}
```

### 6.3 Backend integration (production)

Static files are served by the `MapWhen` block in `Program.cs` (excluding `/admin`, `/health`, `/swagger`), and stage 2 of the `Dockerfile` copies `dist/` into `Host/wwwroot/`.

## 7. Page features

### 7.1 QuestionView (question management)

- List (search by subject, grade, level, type, keyword, tagId)
- Details: a dialog showing the stem/answer/explanation (raw text in `pre`) + images (OSS presigned URLs)
- Delete (with a confirmation dialog)
- No create/edit (v1 scope)

### 7.2 KnowledgeView (knowledge point management)

- List (flat, indented by parent_id hierarchy)
- Create/edit: dialog form (name, parentId, subject, grade, description, userId)
- Delete: only when is_referenced == false (the backend rejects with 422)

### 7.3 TagView (tag management)

- List (by name ascending or usageCount descending)
- Fuzzy search
- Create/edit: dialog form (name, color, description, userId)
- Color: uses `<input type="color">`
- Delete: only when usageCount == 0 (the backend rejects with 422)

## 8. Error handling

`ApiErrorHandler.ts` exposes `getApiErrorMessage(error: unknown): string`, which extracts the backend message from `error.response?.data?.message`. Views display it with the native toast.

## 9. Build and run

### 9.1 Dev

```bash
cd frontend
npm install
npm run dev   # Vite dev server :8091, proxies /admin to :5007
```

### 9.2 Production build

```bash
cd frontend
npm run build  # Outputs dist/, copied into Host/wwwroot/ by the Dockerfile
```

### 9.3 Docker

```bash
docker build -t quaestura:latest .
# Image: quaestura:<tag>
# Dockerfile COPY path: frontend/
```

## 10. Environment variables

| Variable | Default | Description |
|------|------|------|
| `APP_TITLE` | `Quaestura Admin` | Browser tab title |

## 11. Refactoring log (2026-07-04)

- Path migration: after moving out into the standalone Quaestura repository, the frontend lives at the repository root in `frontend/` (inside the monorepo it had moved from `src/questionbank_portal/frontend/` → `src/services/ruoyu.questionBank/frontend/`)
- Removed the element-plus / @element-plus/icons-vue dependencies
- Expanded style.css from 10 lines into a complete design system (reusing doclibrary CSS variables + shared component classes)
- Rewrote App.vue: el-container/el-menu → hand-written sidebar + router-view
- Rewrote the 3 views: el-table/el-form/el-dialog/el-pagination → hand-written data-table/form/confirm-dialog/pagination-bar
- ElMessage/ElMessageBox → native toast + custom ConfirmDialog
- Removed dead code: unused singleton exports in the 3 API files, a dead import in QuestionView, the unused ErrorResponse type, and the half-finished tree-props in KnowledgeView
- Unified the API timeout to 20000ms
- Kept vue-router (history mode, 3 routes)
