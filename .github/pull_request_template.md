> 正文一律用中文填写。标题使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` …）。

## 概述

说明要解决的问题和本次改动。

Closes #

## 范围

引用所链接 issue 的“范围”，并逐项交代：

- **范围内，已完成：**
- **本次刻意不修：** 实施中发现的既有缺陷，确认没有写“无”。

## 接口、行为和兼容性影响

- **能保证什么、不能保证什么：** 是否与 issue 一致；不适用写“无”。
- **HTTP API、JSON、JWT 与授权：**
- **PostgreSQL schema、migration 与数据：**
- **对象存储、配置与 Consul 键：**
- **管理端、容器与部署：**
- **英文文档：**

没有影响的项目写“无”。

## 状态、数据变化和异常情况说明

复杂状态、持久化或安全设计填写；不适用写明理由：

- **状态和处理规则：** 链接定义状态、数据与文件变化、外部输入和敏感数据流的文档或代码。
- **端到端场景：** 列出本 PR 逐步检查或测试过的场景，说明每种情况下应出现的确定结果，以及实际检查或测试的结果。

## 验证

列出实际执行的命令、结果和跳过原因：

- [ ] `dotnet build src/Quaestura.sln --configuration Release` 通过
- [ ] `dotnet test src/Quaestura.sln --configuration Release --no-build` 通过
- [ ] `cd frontend && npm ci && npm run build` 通过
- [ ] 涉及 schema 时，migration 已生成并审阅
- [ ] 涉及容器或部署时，镜像构建与启动验证通过
- [ ] 行为、配置或用法变化时，英文文档已同步
- [ ] 不包含密钥、连接字符串、凭据、token 或私有数据
