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
