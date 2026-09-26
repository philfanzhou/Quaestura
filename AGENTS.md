# Quaestura 协作规范

Quaestura 是通用的题库与知识点管理服务：.NET 8 最小 API、Vue 3 管理前端、单容器部署（API + SPA 同进程，容器内监听 5007）。它于 2026-09-25 从 Ruoyu.Study monorepo 的 `ruoyu.questionBank` 迁出（保留子树提交历史），采用 MIT License。

## 维护方式

- 本文件是 AI 协作流程、项目边界和验证约束的统一入口。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- CI 尚未建立；推送前必须本地运行「验证」一节的全部命令。

## 文档与沟通语言

- 流程与约束文档、issue/PR 正文使用中文；PR 标题和 commit message 使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` 等）。
- 面向使用者的根 `README.md` 保持英文。
- `docs/` 与 `CONTEXT.md` 是从 Ruoyu.Study monorepo 继承的中文文档，保持中文；翻译为英文是独立后续项，不与功能改动混在同一提交。
- 引用代码、命令、路径、JSON 字段和配置键时保持原样。

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

- 删除、移动或批量改写前先验证精确目标。
- 配置中的凭据（数据库、Consul token、S3 密钥）一律经环境变量或 Consul KV 注入，不得写入仓库。
