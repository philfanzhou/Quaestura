# Ruoyu.Study.Grpc.QuestionBank 代码审计报告

## 待修复问题总览

| # | 问题 | 状态 | 修改文件 |
|---|---|---|---|
| 1 | 对象映射（MappingConfig）中的硬编码逻辑隐患 | ✅ 已完成 | `MappingConfig.cs`、`RequestValidators.cs` |

---

## 1. 对象映射（MappingConfig）中的硬编码逻辑隐患

**修复状态**: ✅ 已完成

**修改文件**:
- `src/Service/Mapping/MappingConfig.cs`
- `src/Service/Validation/RequestValidators.cs`

**修复内容**:

**移除映射层的业务清洗逻辑**：`MappingConfig` 中的 `MapEmptyString`（空字符串→null）和 `MapZeroToNull`（零值→null）方法被移除，`KnowledgeDto → Knowledge` 映射改为直接字段拷贝，不对 `Subject`、`Grade`、`ParentId`、`UpdatedBy` 做任何值转换：

- `MapZeroToNull(src.Subject)` → `src.Subject`（直接拷贝）
- `MapZeroToNull(src.Grade)` → `src.Grade`（直接拷贝）
- `MapEmptyString(src.ParentId)` → `src.ParentId`（直接拷贝）
- `MapEmptyString(src.UpdatedBy)` → `src.UpdatedBy`（直接拷贝）

**在 FluentValidation 层补强约束**：由于移除了映射层的零值过滤，新增 `KnowledgeValidator` 对 `Knowledge` 实体的 `Subject > 0`、`Grade > 0`、`Name` 非空进行校验。若客户端发送 `0`，验证器直接拒绝请求，业务值不会到达映射层或数据库。映射层只做纯粹的字段搬运，职责边界更清晰。

---

## 历史已修复问题存档

- ✅ 文件上传体积与类型安全校验缺失已修复（利用 `ImageValidationHelper` 校验大小和 Magic Number）
- ✅ 全局异常处理与日志记录缺失已修复（利用 `GrpcExceptionInterceptor` 统一拦截）
- ✅ 生产环境安全与硬编码敏感信息已修复（移除弱密码，改用 `UpdateDatabase()` 迁移）
- ✅ 不合理的缩略图前置上传设计已修复（彻底移除缩略图字段和独立上传逻辑）
- ✅ 跨服务操作（DB + OSS）缺乏事务与回滚机制已修复（增加 catch 补偿删除 OSS 逻辑）
- ✅ 缓存版本号自增非线程安全已修复（改用 `Interlocked.Increment`）
- ✅ 未释放的 MemoryStream 资源已修复（使用 `using` 确保释放）
- ✅ 知识点 IsReferenced 状态更新存在竞态条件与性能问题 (N+1) 已修复（改为批量查询和原子更新）
- ✅ GetQuestionsByKnowledge 分页获取逻辑有性能隐患已修复（下推至数据库层执行）
- ✅ 批量移除题目关联时未恢复知识点 IsReferenced 状态已修复
- ✅ 删除图片文件时未能同步清理 OSS 存储中的文件已修复
- ✅ Question 和 Mistake 的图片地址获取逻辑缺失已修复
- ✅ Mistake 服务图片/缩略图上传缺失已修复
- ✅ Knowledge 父级死循环检测已修复
- ✅ FluentValidation 验证器未生效已修复
- ✅ MinIO 配置项容错性弱已修复
- ✅ Knowledge Name 全局唯一约束错误已修复
- ✅ 分页参数无防御导致异常已修复
