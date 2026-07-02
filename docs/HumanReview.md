# HumanReview — ruoyu.questionBank

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

### HR-02: SearchAsync N+1 查询 + 内存分页

- **问题**：`Domain/Services/QuestionService.cs:109-174` `SearchAsync` 先获取全量数据再内存分页，且循环内逐条查询关联数据
- **A** (推荐)：改用 EF Core 服务端分页（Skip/Take）+ Include 预加载
- **B**：编写原生 SQL 分页查询
- **C**：暂不处理，当前数据量小
- **批复**：

## P1 — 应尽快修复

### HR-04: 单文件包含多个类型

- **问题**：`Domain/Services/QuestionService.cs:204-280` 单文件定义 `QuestionWithContent` / `IOssQuestionService` / `OssQuestionService` 三个类型
- **A** (推荐)：每个类型拆分到独立文件
- **B**：按功能分组，每组一个文件
- **C**：暂不处理
- **批复**：

### HR-07: WouldCreateCycle 未加载 Parent 导航属性

- **问题**：`Domain/Services/KnowledgeService.cs:155-172` `WouldCreateCycle` 检测循环依赖时未加载 `Parent` 导航属性
- **A** (推荐)：使用原始 SQL 递归查询检测循环（WITH RECURSIVE）
- **B**：预加载完整祖先链后在内存检测
- **C**：暂不处理，当前层级浅
- **批复**：

---

## 已解决（阶段 1 gRPC → WebAPI 迁移中处理）

下列项已在阶段 1 迁移中通过重写对应代码层自动解决：

- **HR-01**（DeleteQuestion 用 `_ = Task.Run(...)` + 空 catch）：新 `QuestionEndpoints.DeleteQuestion` 使用 `await _ossQuestionService.DeletePicturesAsync(...)` 同步等待，异常由中间件统一处理
- **HR-03**（中文验证消息）：新 `Service/Validation/RequestValidators.cs` 全部使用英文错误消息
- **HR-05**（`Guid.Parse` 无 try-catch）：新端点代码全部使用 `Guid.TryParse`，失败抛出 `ValidationException` 转为 HTTP 400
- **HR-06**（硬编码 `.jpg` 和 `image/jpeg`）：新 `QuestionEndpoints.UploadQuestion` 从 `IFormFile.FileName` 提取扩展名、用 `IFormFile.ContentType` 取 MIME
- **HR-08**（`EnableDetailedErrors = true`）：新 `Host/Program.cs` 无 gRPC 配置，问题自动消除

## 阶段 1 额外变更

迁移过程中对 Domain 层做了一处必要修复（不在原 HR 列表中）：

- **`Domain/Services/KnowledgeService.cs:GetLikeAsync`**：原实现使用 `EF.Functions.ILike`（Npgsql 专属），SQLite 上会抛 `NotSupportedException`。改为 `Contains` 以支持双库自动识别。已通过端到端验证（SQLite + PostgreSQL 均可用）。
