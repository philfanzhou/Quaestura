# Quaestura Admin Frontend Spec

本文档定义题库管理前端的技术栈、目录结构、路由、与后端 API 的集成方式。

## 1. 概述

管理前端是 Vue 3 单页应用,与 Quaestura 后端服务在**同一个 Docker 容器**中运行(端口 5007 同时提供 API 和 SPA)。**未集成鉴权**(沿用内网环境假设)。

**目录位置**:`frontend/`(与后端服务同目录,参考 doclibrary 前端布局)。

## 2. 技术栈

| 项目 | 技术 |
|------|------|
| 框架 | Vue 3.5 + Composition API (`<script setup>`) |
| 语言 | TypeScript |
| 构建工具 | Vite |
| UI 方案 | **纯手写 CSS**(不使用 UI 组件库,复用 doclibrary 设计系统) |
| 路由 | vue-router(history 模式) |
| HTTP 客户端 | Axios |

### 已移除的依赖(历史遗留)

- `element-plus` / `@element-plus/icons-vue` — 全部 UI 改为手写 CSS,ElMessage 改为原生 toast
- 历史版本曾全量引入 Element Plus,现已移除以保持轻量

## 3. 目录结构

```
frontend/     # 与后端服务同目录
├── package.json
├── package-lock.json
├── vite.config.ts                            # 端口 8091,proxy /admin → :5007
├── tsconfig.json / .app.json / .node.json
├── index.html                                # 含 <title>__APP_TITLE__</title>
├── public/favicon.svg
├── docs/admin-frontend-spec.md               # 本文件
└── src/
    ├── main.ts                               # createApp + router(无 ElementPlus)
    ├── App.vue                               # 布局(手写侧边栏 + router-view)
    ├── style.css                             # 全局样式(复用 doclibrary 设计系统)
    ├── env.d.ts                              # import.meta.env 类型
    ├── router/index.ts                       # 3 个路由
    ├── views/
    │   ├── QuestionView.vue                  # 题目管理(列表+搜索+查看+删除)
    │   ├── KnowledgeView.vue                 # 知识点管理(列表+增删改)
    │   └── TagView.vue                       # 标签管理(列表+增删改)
    ├── services/
    │   ├── questionApi.ts
    │   ├── knowledgeApi.ts
    │   └── tagApi.ts
    ├── types/
    │   └── index.ts                          # 响应 DTO 类型
    └── components/
        └── ApiErrorHandler.ts                # getApiErrorMessage 工具函数
```

## 4. 样式规范

### 4.1 复用 doclibrary 设计系统

`style.css` 复用 doclibrary 的 CSS 变量 + 通用组件类,包括:

- CSS 变量:`--primary-color`、`--text-primary`、`--card-bg`、`--border-color`、`--radius-md`、`--shadow-sm` 等
- 通用类:`.btn`/`.btn-primary`/`.btn-secondary`/`.btn-danger`/`.btn-small`、`.card`/`.card-header`/`.card-body`、`.data-table`、`.pagination-bar`、`.tag`、`.form-group`/`.input-wrap`/`.select-wrap`、`.empty-state`、`.spinner`、`.status-badge`
- 布局类:`.admin-layout`/`.sidebar`/`.main-content`/`.top-header`/`.content-area`/`.page-header`

### 4.2 禁止内联样式

除动态绑定(`:style` 用于颜色色块、进度条宽度等)外,禁止使用 `style="..."` 静态内联样式。

### 4.3 原生 toast 替代 ElMessage

移除 Element Plus 后,`ElMessage`/`ElMessageBox` 改为:
- 消息提示:原生 toast 函数(创建 DOM 元素挂到 body,3 秒自动消失)
- 确认弹窗:自实现 ConfirmDialog 组件(遮罩 + 卡片 + 确认/取消按钮)

## 5. 路由

| 路径 | 视图 | meta.title |
|------|------|-----------|
| `/` | redirect to `/questions` | — |
| `/questions` | QuestionView | `题目管理` |
| `/knowledges` | KnowledgeView | `知识点管理` |
| `/tags` | TagView | `标签管理` |

`router.beforeEach` 设置 `document.title = ${to.meta.title} - ${__APP_TITLE__}`。

## 6. 与后端 API 的集成

### 6.1 API 路径约定

前端代码使用相对路径 `/admin/...`,dev 走 vite proxy,prod 走后端同源静态服务。

### 6.2 Vite 代理(dev)

```ts
server: {
  port: 8091,
  proxy: {
    '/admin': { target: 'http://localhost:5007', changeOrigin: true },
  },
}
```

### 6.3 后端集成(生产)

由 `Program.cs` 的 `MapWhen` 块提供静态文件服务(排除 `/admin`、`/health`、`/swagger`),`Dockerfile` 第 2 阶段把 `dist/` 复制到 `Host/wwwroot/`。

## 7. 页面功能

### 7.1 QuestionView(题目管理)

- 列表(搜索:subject、grade、level、type、keyword、tagId)
- 详情:弹窗显示题干/答案/解析(`pre` 原文)+ 图片(OSS presigned URL)
- 删除(确认弹窗)
- 不做新增/编辑(v1 范围)

### 7.2 KnowledgeView(知识点管理)

- 列表(扁平展示,按 parent_id 层级缩进)
- 新增/编辑:弹窗表单(name、parentId、subject、grade、description、userId)
- 删除:仅当 is_referenced == false(后端 422 拒绝)

### 7.3 TagView(标签管理)

- 列表(按 name 升序 或 usageCount 降序)
- 模糊搜索
- 新增/编辑:弹窗表单(name、color、description、userId)
- 颜色:用 `<input type="color">`
- 删除:仅当 usageCount == 0(后端 422 拒绝)

## 8. 错误处理

`ApiErrorHandler.ts` 暴露 `getApiErrorMessage(error: unknown): string`,从 `error.response?.data?.message` 提取后端消息。视图层用原生 toast 显示。

## 9. 构建与运行

### 9.1 Dev

```bash
cd frontend
npm install
npm run dev   # Vite dev server :8091,代理 /admin 到 :5007
```

### 9.2 生产构建

```bash
cd frontend
npm run build  # 输出 dist/,由 Dockerfile 复制到 Host/wwwroot/
```

### 9.3 Docker

```bash
docker build -t quaestura:latest .
# 镜像:quaestura:<tag>
# Dockerfile COPY 路径:frontend/
```

## 10. 环境变量

| 变量 | 默认 | 说明 |
|------|------|------|
| `APP_TITLE` | `Quaestura Admin` | 浏览器 tab 标题 |

## 11. 重构记录(2026-07-04)

- 路径迁移:迁出到 Quaestura 独立仓库后,前端固定在仓库根 `frontend/`(此前在 monorepo 内历经 `src/questionbank_portal/frontend/` → `src/services/ruoyu.questionBank/frontend/`)
- 移除 element-plus / @element-plus/icons-vue 依赖
- style.css 从 10 行扩展为完整设计系统(复用 doclibrary CSS 变量 + 通用组件类)
- App.vue 重写:el-container/el-menu → 手写侧边栏 + router-view
- 3 个视图重写:el-table/el-form/el-dialog/el-pagination → 手写 data-table/form/confirm-dialog/pagination-bar
- ElMessage/ElMessageBox → 原生 toast + 自实现 ConfirmDialog
- 清理死代码:3 个 API 文件未使用的单例导出、QuestionView 死导入、ErrorResponse 未使用类型、KnowledgeView tree-props 半成品
- 统一 API timeout 为 20000ms
- 保留 vue-router(history 模式,3 路由)
