# QuestionBank 题库核心服务

## 1. 项目概述

QuestionBank 是基于 **ASP.NET Core 最小 API (WebAPI)** 的统一题库管理微服务，专注于**题目管理**和**知识点管理**，为教育系统提供稳定的题库检索、组卷和基础静态数据服务。本服务在单一 docker 容器中同时提供 **WebAPI（端口 5007）** 和 **管理前端 SPA**。

**注：** 错题管理相关业务（包括学生上传错题、错因分析、错题重练等动态数据）已解耦并拆分至专门的 `ruoyu.mistake` 微服务中。

## 2. 文档导航

- [API 规范](docs/overview/ApiSpec.md) - 完整 HTTP 端点定义、请求/响应 schema、错误码字典
- [管理前端 spec](frontend/docs/admin-frontend-spec.md) - Vue 3 + 手写 CSS 管理前端技术栈、目录、路由、与后端集成
- [数据库与存储设计](docs/database/README.md) - 包含 PostgreSQL 表结构设计和 SeaweedFS 存储路径规范
- [部署与运行指南](docs/development/Deployment.md) - 包含环境依赖、启动要求、配置项说明以及 Docker 部署指南
- [错误处理规范](docs/development/ErrorHandling.md) - HTTP 错误响应格式与异常处理规范
- [单元测试规范](docs/development/Testing.md) - 测试项目结构、覆盖范围与约定
- [本地部署指南](src/Host/README.md) - 端口与端点清单

## 3. 技术栈

- **后端框架**: .NET 8 + ASP.NET Core 最小 API (WebAPI)
- **管理前端**: Vue 3 + TypeScript + Vite + 手写 CSS + Axios（位于 `frontend/`，与后端同目录）
- **数据库**: PostgreSQL
- **ORM**: Entity Framework Core 8.0 + Npgsql
- **对象存储**: SeaweedFS（S3 兼容，端口 8333）
- **校验**: FluentValidation
- **架构**: DDD（领域驱动设计）

## 4. 项目结构

```
ruoyu.questionBank/                     # 后端服务
├── frontend/                           # 管理前端（与后端同目录）
│   ├── package.json
│   ├── vite.config.ts
│   ├── index.html
│   ├── docs/admin-frontend-spec.md
│   └── src/
├── src/                                # .NET 源码（Database/Domain/Service/Host）
├── docs/                               # 后端正式文档
├── start.sh                            # Docker 启动脚本
└── README.md
```

## 5. 核心功能

### 5.1 题目管理（Question）

| 功能 | 端点 | 说明 |
|------|------|------|
| 查询题目 | `GET /admin/questions/{id}` | 根据 ID 获取题目详情 |
| 搜索题目 | `GET /admin/questions` | 按关键字、难度等级、题目类型筛选 |
| 上传题目 | `POST /admin/questions` | multipart/form-data，支持图片和文本内容 |
| 删除题目 | `DELETE /admin/questions/{id}` | 删除指定题目及关联 OSS 图片 |
| 列表查询 | `GET /admin/questions` | 按 level/type/subject/grade 分页 |

### 5.2 知识点管理（Knowledge）

| 功能 | 端点 | 说明 |
|------|------|------|
| 查询知识点 | `GET /admin/knowledges/{id}` | 根据 ID 获取详情 |
| 列表查询 | `GET /admin/knowledges` | 支持按 parentId 查子节点、按 grade/subject 分页、按 name 模糊搜索 |
| 新增/更新 | `POST /admin/knowledges` | id 存在则更新，空则新增 |
| 删除知识点 | `DELETE /admin/knowledges/{id}` | 被引用时拒绝删除 |

### 5.3 题目-知识点关联（QuestionKnowledge）

| 功能 | 端点 | 说明 |
|------|------|------|
| 批量打标签 | `POST /admin/question-knowledges/batch-tag` | 批量将知识点关联到题目 |
| 获取题目知识点 | `GET /admin/question-knowledges?questionId=` | 获取指定题目的所有关联 |
| 获取知识点题目 | `GET /admin/question-knowledges?knowledgeId=` | 获取关联到指定知识点的所有题目 |
| 移除关联 | `DELETE /admin/question-knowledges?questionId=` | 移除题目的所有知识点关联 |

### 5.4 Tag 管理

| 功能 | 端点 | 说明 |
|------|------|------|
| 列表查询 | `GET /admin/tags` | 支持 name 模糊搜索、sortBy=usageCount |
| 查询详情 | `GET /admin/tags/{id}` | 根据 ID 获取详情 |
| 新增/更新 | `POST /admin/tags` | id 存在则更新，空则新增 |
| 删除 Tag | `DELETE /admin/tags/{id}` | 被引用时拒绝删除 |

### 5.5 题目-Tag 关联

| 功能 | 端点 | 说明 |
|------|------|------|
| 批量打标签 | `POST /admin/question-tags/batch-tag` | 批量给多个题目打多个 Tag |
| 查询关联 | `GET /admin/question-tags` | 按 questionId 或 tagId 查询 |
| 移除关联 | `DELETE /admin/question-tags` | 移除单个或全部关联 |

## 6. 端口

- HTTP 5007（WebAPI + 管理前端 SPA，同进程）
