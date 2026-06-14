# HumanReview — ruoyu.questionBank

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

| ID | 问题 | 方案 A | 方案 B | 方案 C | 批复 |
|----|------|--------|--------|--------|------|
| HR-01 | DeleteQuestion fire-and-forget Task.Run 且空 catch（QuestionServiceImpl.cs） | (推荐) 改为 await 异步删除，异常记录日志并返回错误 | 使用后台队列 + 重试机制处理删除 | 暂不处理，当前删除频率低 |
| HR-02 | SearchAsync N+1 查询 + 内存分页（QuestionServiceImpl.cs） | (推荐) 改用 EF Core 服务端分页（Skip/Take）+ Include 预加载 | 编写原生 SQL 分页查询 | 暂不处理，当前数据量小 |

## P1 — 应尽快修复

| ID | 问题 | 方案 A | 方案 B | 方案 C | 批复 |
|----|------|--------|--------|--------|------|
| HR-03 | 中文验证消息 | (推荐) 统一改为英文，中文仅保留用户可见的 DisplayNames | 保持中文，在规范中允许 gRPC 错误消息使用中文 | 暂不处理，不影响功能 |
| HR-04 | 单文件包含多个类型 | (推荐) 每个类型拆分到独立文件，遵循 C# 约定 | 按功能分组，每组一个文件 | 暂不处理，不影响功能 |
| HR-05 | Guid.Parse 无 try-catch，非法 ID 导致未处理异常 | (推荐) 使用 Guid.TryParse，失败返回 InvalidArgument | 添加全局 gRPC 异常拦截器统一处理 | 暂不处理，当前调用方传值规范 |
| HR-06 | AddOrUpdateQuestion 硬编码 .jpg 和 image/jpeg（QuestionServiceImpl.cs） | (推荐) 从上传文件名提取扩展名，使用 MimeTypeMap 推断 content-type | 添加参数由调用方传入 content-type | 暂不处理，当前仅处理图片 |
| HR-07 | WouldCreateCycle 未加载 Parent 导航属性，循环检测失效（QuestionServiceImpl.cs） | (推荐) 使用原始 SQL 递归查询检测循环（WITH RECURSIVE） | 预加载完整祖先链后在内存检测 | 暂不处理，当前层级浅 |
| HR-08 | 生产环境 EnableDetailedErrors = true，泄露内部信息（Program.cs） | (推荐) 仅开发环境启用，生产环境设为 false | 完全移除该配置，使用默认值 false | 暂不处理，当前仅内网使用 |
