# Quaestura Admin Frontend Spec

This document defines the tech stack, directory structure, routes, and backend API integration of the question-bank admin frontend.

## 1. Overview

The admin frontend is a Vue 3 single-page application that runs in the **same Docker container** as the Quaestura backend (port 5007 serves both the API and the SPA). **Authentication is required**: users sign in on `/login` through `POST /admin/auth/login`, and every API request carries the issued JWT (see §5.1–§5.2).

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
    │   ├── auth.ts                           # login/getAuthToken/isAuthenticated/clearAuth
    │   ├── httpClient.ts                     # Shared axios instance (Bearer + 401 interceptors)
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

`router.beforeEach` enforces authentication (state table authoritative in issue #24):

- Not signed in (no token, or `expiresAt` passed — an expired token is cleared) and navigating to any non-public route → redirect to `/login?redirect=<original fullPath>`.
- Not signed in and navigating to `/login` → allowed; `App.vue` renders public routes without the sidebar layout.
- Signed in and navigating to `/login` → redirect to `/questions`.
- Successful login → store token + expiry, then navigate to the `redirect` query value when it is a same-site path starting with `/` (and not `//`); otherwise navigate to `/questions`.
- Failed login (400/502/503 or network error) → stay on the login page and show the backend message via `getApiErrorMessage`; nothing is stored.
- Any API call answering 401 → `httpClient` clears the stored token and sends the browser to `/login` via `window.location` (no redirect while already on `/login`). 403 responses do **not** clear the token; the view shows the error and the user stays signed in.
- The "退出登录" button in the top header clears the token and navigates to `/login`.

### 5.2 Token storage and shared HTTP client

- `services/auth.ts` stores the access token in `localStorage` under `quaesturaAuthToken` and its expiry (Unix seconds, SignaCore contract) under `quaesturaAuthExpiresAt`; both are written in one synchronous step and removed together by `clearAuth()`. `isAuthenticated()` requires a token **and** a not-yet-passed `expiresAt`.
- The password only exists in the login form's in-memory state; it is wiped from the form right after submit and is never written to storage or logged.
- `services/httpClient.ts` exports the single shared axios instance (`timeout: 20000`). Its request interceptor attaches `Authorization: Bearer <token>`; its response interceptor handles 401 as described above. All API clients (`questionApi` / `knowledgeApi` / `tagApi`) must reuse this instance — instances created per client via `axios.create()` would bypass the interceptors and send requests without the token.
- The token is sent only via the `Authorization` header; no cookies are used. XSS protection of `localStorage` is out of scope (same trade-off as Ruoyu.Admin); deploy behind TLS or restrict the admin frontend to a trusted network.

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
