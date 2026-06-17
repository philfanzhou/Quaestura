# 单元测试规范

本文件描述 `ruoyu.questionBank` 服务的单元测试项目结构、覆盖范围与约定。

## 测试项目结构

| 项目 | 路径 | 覆盖范围 |
|------|------|---------|
| `QuestionBank.Test` | `src/Tests/` | Domain 层服务 + Service 层 Validation 组件 |

## 测试框架与依赖

- xUnit 2.6.2
- Moq 4.20.70
- FluentAssertions 6.12.0
- FluentValidation 11.9.0（测试 RequestValidators）
- Grpc.AspNetCore 2.60.0（测试 Interceptor，提供 `ServerCallContext`）
- Microsoft.EntityFrameworkCore.Sqlite 8.0.11（Domain 层集成测试）
- coverlet.collector 6.0.0

## Validation 组件测试覆盖范围

### 测试辅助类

- `TestServerCallContextImpl`：继承 `Grpc.Core.ServerCallContext`，提供 gRPC 2.60.0 兼容的最小测试上下文实现。静态工厂 `Create()` 返回可用于 Interceptor 方法的 `ServerCallContext` 实例。

### ImageValidationHelper 测试场景

| 场景 | 期望 |
|------|------|
| null 字节数组 | `RpcException(InvalidArgument, "图片数据为空")` |
| 空字节数组 | `RpcException(InvalidArgument, "图片数据为空")` |
| 超过 10MB | `RpcException(InvalidArgument, "单张图片大小不能超过 10MB")` |
| JPEG magic number | 通过 |
| PNG magic number | 通过 |
| GIF87a magic number | 通过 |
| GIF89a magic number | 通过 |
| WebP (RIFF) magic number | 通过 |
| BMP magic number | 通过 |
| 不支持的格式 | `RpcException(InvalidArgument, "图片格式不被支持")` |
| 数据短于 magic number 长度 | `RpcException(InvalidArgument)` |
| null 图片列表 | 通过（直接返回） |
| 空图片列表 | 通过（直接返回） |
| 批量校验含一张非法 | `RpcException(InvalidArgument)` |
| 批量校验全部合法 | 通过 |

### GrpcValidationInterceptor 测试场景

| 场景 | 期望 |
|------|------|
| null 请求 | `RpcException(InvalidArgument, "Request cannot be null")` |
| 无注册验证器 | 调用 continuation |
| 验证通过 | 调用 continuation，返回其结果 |
| 验证失败 | `RpcException(InvalidArgument)`，Detail 含错误消息 |

### GrpcExceptionInterceptor 测试场景

| 场景 | 期望 |
|------|------|
| continuation 成功 | 返回 continuation 结果 |
| continuation 抛 RpcException | 原样 rethrow |
| continuation 抛普通异常 | `RpcException(Internal, "服务器内部错误")`，记录日志 |

### RequestValidators 测试场景

#### QuestionValidator

| 场景 | 期望 |
|------|------|
| PicturePaths 为空 | 验证失败，含"题目图片必须至少有一张" |
| Level 为负数 | 验证失败，含"难度等级不能为负数" |
| Type 为负数 | 验证失败，含"题目类型不能为负数" |
| Subject <= 0 | 验证失败，含"学科必须大于0" |
| Grade <= 0 | 验证失败，含"年级必须大于0" |
| 全部字段合法 | 验证通过 |

#### KnowledgeValidator

| 场景 | 期望 |
|------|------|
| Subject <= 0 | 验证失败，含"学科必须大于0" |
| Grade <= 0 | 验证失败，含"年级必须大于0" |
| Name 为空 | 验证失败，含"知识点名称不能为空" |
| 全部字段合法 | 验证通过 |

## 运行方式

```bash
cd src/services/ruoyu.questionBank/src
dotnet test Ruoyu.Study.QuestionBank.sln --configuration Release
```

## 约定

- Validation 组件测试置于 `src/Tests/Validation/` 目录下。
- 静态类（如 `ImageValidationHelper`）直接调用，无需 Mock。
- Interceptor 测试使用自定义 `TestServerCallContextImpl` 构造 `ServerCallContext`。
- 断言使用 FluentAssertions。
- 不修改被测代码以适配测试；如需可测试性改进，先更新本文档再改代码。
