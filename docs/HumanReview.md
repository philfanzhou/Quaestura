# HumanReview — ruoyu.questionBank

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

### HR-01: DeleteQuestion fire-and-forget Task.Run 且空 catch

- **问题**：QuestionServiceImpl.cs DeleteQuestion 使用 `_ = Task.Run(...)` 且 catch 块为空，异常丢失
- **A** (推荐)：改为 await 异步删除，异常记录日志并返回错误
- **B**：使用后台队列 + 重试机制处理删除
- **C**：暂不处理，当前删除频率低
- **批复**：

### HR-02: SearchAsync N+1 查询 + 内存分页

- **问题**：QuestionServiceImpl.cs SearchAsync 先获取全量数据再内存分页，且循环内逐条查询关联数据
- **A** (推荐)：改用 EF Core 服务端分页（Skip/Take）+ Include 预加载
- **B**：编写原生 SQL 分页查询
- **C**：暂不处理，当前数据量小
- **批复**：

## P1 — 应尽快修复

### HR-03: 中文验证消息

- **问题**：多处验证消息使用中文
- **A** (推荐)：统一改为英文，中文仅保留用户可见的 DisplayNames
- **B**：保持中文，在规范中允许 gRPC 错误消息使用中文
- **C**：暂不处理，不影响功能
- **批复**：

### HR-04: 单文件包含多个类型

- **问题**：多个文件一个文件定义多个类/接口
- **A** (推荐)：每个类型拆分到独立文件，遵循 C# 约定
- **B**：按功能分组，每组一个文件
- **C**：暂不处理，不影响功能
- **批复**：

### HR-05: Guid.Parse 无 try-catch

- **问题**：多处使用 Guid.Parse 解析 ID，非法 ID 导致未处理异常
- **A** (推荐)：使用 Guid.TryParse，失败返回 InvalidArgument
- **B**：添加全局 gRPC 异常拦截器统一处理
- **C**：暂不处理，当前调用方传值规范
- **批复**：

### HR-06: AddOrUpdateQuestion 硬编码 .jpg 和 image/jpeg

- **问题**：QuestionServiceImpl.cs AddOrUpdateQuestion 硬编码文件扩展名和 MIME 类型
- **A** (推荐)：从上传文件名提取扩展名，使用 MimeTypeMap 推断 content-type
- **B**：添加参数由调用方传入 content-type
- **C**：暂不处理，当前仅处理图片
- **批复**：

### HR-07: WouldCreateCycle 未加载 Parent 导航属性

- **问题**：QuestionServiceImpl.cs WouldCreateCycle 检测循环依赖时未加载 Parent 导航属性，检测失效
- **A** (推荐)：使用原始 SQL 递归查询检测循环（WITH RECURSIVE）
- **B**：预加载完整祖先链后在内存检测
- **C**：暂不处理，当前层级浅
- **批复**：

### HR-08: 生产环境 EnableDetailedErrors = true

- **问题**：Program.cs 生产环境启用 EnableDetailedErrors，泄露 gRPC 内部异常信息
- **A** (推荐)：仅开发环境启用，生产环境设为 false
- **B**：完全移除该配置，使用默认值 false
- **C**：暂不处理，当前仅内网使用
- **批复**：
