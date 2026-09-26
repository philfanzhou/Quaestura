# Quaestura 协作规范

Quaestura 是通用的题库与知识点管理服务：.NET 8 最小 API、Vue 3 管理前端、单容器部署（API + SPA 同进程，容器内监听 5007）。它于 2026-09-25 从 Ruoyu.Study monorepo 的 `ruoyu.questionBank` 迁出（保留子树提交历史），采用 MIT License。

## 维护方式

- 本文件是 AI 协作流程、项目边界和验证约束的统一入口。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- `main` 受 ruleset 保护：禁止直接推送、强推和删除，只能通过 PR 合并，且必须通过 `Build & Test`、`Analyze (csharp)`、`Analyze (javascript-typescript)` 检查。
- CI 见 `.github/workflows/ci.yml` 与 `codeql.yml`；推送前仍须本地运行「验证」一节的全部命令，未运行或失败的验证必须如实说明，不得假定通过。
- 版本发布只通过推送 `MAJOR.MINOR.PATCH`（或 `-rc.NUMBER`）tag 触发，流程见 `docs/development/Deployment.md`「版本发布」一节；不得手工创建 Release 或推送镜像。

## 文档与沟通语言

- 流程与约束文档（本文件）、GitHub issue/PR 正文和 review 全程使用中文；Issue 标题使用中文，建议格式为 `[模块] 简明动作`。
- PR 标题和 commit message 使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` / `ci:` 等）；subject 说明做了什么，需要时用 body 说明原因。
- `README.md`、`CONTRIBUTING.md`、`SECURITY.md`、`docs/`、`CONTEXT.md`、代码注释、日志与 API/异常消息等面向使用者的文字使用英文。
- 从 Ruoyu.Study 继承的中文文档、注释与日志的英文化由 #3 跟踪：新增内容直接用英文；修改现有中文内容时不顺带整篇翻译，翻译不与功能改动混在同一提交。
- 业务数据值（如 `src/Common/Constants/*` 中的学科、年级取值）与管理端 UI 文案不属于上述翻译范围。
- 代码标识符、配置键、JWT claim、HTTP 路由、命令和路径保持原样。

## 贡献约定

- `CONTRIBUTING.md` 是所有贡献者的工程入口；漏洞通过 `SECURITY.md` 所述的私密渠道报告，不开公开 issue。
- Issue 使用 `.github/ISSUE_TEMPLATE/task.md`，PR 使用 `.github/pull_request_template.md`，按模板逐节填写，不适用的节写明“无”或“不适用”及理由。

## 项目边界与架构

- 领域语言见根目录 `CONTEXT.md`（Question Catalog：Question / Question Content / Knowledge Point / Tag）。
- `src/Common`（Quaestura.Common）与 `src/Consul`（Quaestura.Consul）是从 Ruoyu.Study 的 `ruoyu.common` **复制**而来的类库（2026-09-25 快照），没有编译期上游同步；上游修复需要人工评估是否回合。不得反向引用 `src/Host`。
- `src/Database`（EF Core + Migrations）、`src/Domain`（领域服务）、`src/Service`（最小 API 端点与校验）、`src/Host`（宿主组合、认证、Consul、Serilog/Loki）、`src/Tests`。依赖方向：Host → Service → Domain → Database → Common。
- 认证信任 SignaCore（或任何兼容 OIDC discovery/JWKS 的签发方）签发的 RS256 JWT；授权规则见 `docs/development/Authentication.md`。
- 数据库为 PostgreSQL（默认库名 `quaestura`），对象存储为 S3 兼容服务（题目图片存于 `questions/` 前缀）。
- API 监听端口 5007 硬编码在 `src/Host/Program.cs`；改端口属于部署契约变更，必须同步 `start.sh`、`Dockerfile` 与部署文档。

## 验证

在仓库根目录依次运行：

```bash
dotnet build src/Quaestura.sln --configuration Release
dotnet test src/Quaestura.sln --configuration Release --no-build
cd frontend && npm ci && npm run build
```

## 安全

- 保留并绕开无关的用户修改；不得擅自回退、覆盖或提交。
- 删除、移动或批量改写前先验证精确目标。
- 配置中的凭据（数据库、Consul token、S3 密钥）一律经环境变量或 Consul KV 注入，不得写入仓库。
- 绝不提交或记录连接字符串、S3 密钥、Consul token、JWT、authorization header 或个人数据；日志和测试输出必须脱敏。
- 增加依赖、改变公开 API、数据库、配置或部署方式前，先说明兼容性、迁移和回滚影响。
- 提交前检查文档链接、模板格式、secret 和仓库状态。
