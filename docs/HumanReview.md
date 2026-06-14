# HumanReview — ruoyu.questionBank

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

| ID | 问题 | 位置 | 状态 | 批复 |
|----|------|------|------|------|
| HR-01 | DeleteQuestion fire-and-forget Task.Run 且空 catch | QuestionServiceImpl.cs | 待修复 | |
| HR-02 | SearchAsync N+1 查询 + 内存分页 | QuestionServiceImpl.cs | 待修复 | |

## P1 — 应尽快修复

| ID | 问题 | 位置 | 状态 | 批复 |
|----|------|------|------|------|
| HR-03 | 中文验证消息 | 多处 | 待修复 | |
| HR-04 | 单文件包含多个类型 | 多处 | 待修复 | |
| HR-05 | Guid.Parse 无 try-catch | 多处 | 待修复 | |
| HR-06 | AddOrUpdateQuestion 硬编码 .jpg 和 image/jpeg | QuestionServiceImpl.cs | 待修复 | |
| HR-07 | WouldCreateCycle 未加载 Parent 导航属性 | QuestionServiceImpl.cs | 待修复 | |
| HR-08 | 生产环境 EnableDetailedErrors = true | Program.cs | 待修复 | |
