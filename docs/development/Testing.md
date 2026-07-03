# 单元测试规范

本文件描述 `ruoyu.questionBank` 服务的单元测试项目结构、覆盖范围与约定。

## 测试项目结构

| 项目 | 路径 | 覆盖范围 |
|------|------|---------|
| `QuestionBank.Test` | `src/Tests/` | Domain 层服务 + Service 层 Validation 组件 + 端点集成测试（可选） |

## 测试框架与依赖

- xUnit 2.6.2
- Moq 4.20.70
- FluentAssertions 6.12.0
- FluentValidation 11.9.0（测试 RequestValidators）
- Microsoft.EntityFrameworkCore.InMemory 8.0.11（Domain 层集成测试 + 端点集成测试）

## Domain 层测试覆盖范围

| 测试文件 | 覆盖服务 |
|---------|---------|
| `Services/KnowledgeServiceTests.cs` | `IKnowledgeService` 主要路径 |
| `Services/KnowledgeServiceAdditionalTests.cs` | `IKnowledgeService` 边界与异常路径 |
| `Services/QuestionServiceTests.cs` | `IQuestionService` 主要路径 |
| `Services/QuestionServiceAdditionalTests.cs` | `IQuestionService` 边界与异常路径 |
| `Services/QuestionKnowledgeServiceTests.cs` | `IQuestionKnowledgeService` 主要路径 |
| `Services/TagServiceTests.cs` | `ITagService` 主要路径（CRUD、重复检查、引用约束、usage_count 增/减） |
| `Services/TagServiceAdditionalTests.cs` | `ITagService` 边界与异常路径 |
| `Services/QuestionTagServiceTests.cs` | `IQuestionTagService` 主要路径（批量打标、移除、查询、计数同步） |

Domain 层测试使用 `TestBase` 提供的 EF Core InMemory 数据库，与 WebAPI 层无关。

## Validation 组件测试覆盖范围

### ImageValidationHelper 测试场景

| 场景 | 期望 |
|------|------|
| null 字节数组 | `InvalidOperationException("Image data is empty")` |
| 空字节数组 | `InvalidOperationException("Image data is empty")` |
| 超过 10MB | `InvalidOperationException("Image size exceeds 10MB")` |
| JPEG magic number | 通过 |
| PNG magic number | 通过 |
| GIF87a magic number | 通过 |
| GIF89a magic number | 通过 |
| WebP (RIFF) magic number | 通过 |
| BMP magic number | 通过 |
| 不支持的格式 | `InvalidOperationException("Image format not supported")` |
| 数据短于 magic number 长度 | `InvalidOperationException("Image format not supported")` |
| null 图片列表 | 通过（直接返回） |
| 空图片列表 | 通过（直接返回） |
| 批量校验含一张非法 | `InvalidOperationException` |
| 批量校验全部合法 | 通过 |

### RequestValidators 测试场景

#### CreateQuestionRequestValidator

| 场景 | 期望 |
|------|------|
| Pictures 为空 | 验证失败，含 "At least one picture is required" |
| Level 为负数 | 验证失败，含 "Level must not be negative" |
| Type 为负数 | 验证失败，含 "Type must not be negative" |
| Subject <= 0 | 验证失败，含 "Subject must be greater than 0" |
| Grade <= 0 | 验证失败，含 "Grade must be greater than 0" |
| UserId 为空 | 验证失败，含 "UserId is required" |
| 全部字段合法 | 验证通过 |

#### CreateKnowledgeRequestValidator

| 场景 | 期望 |
|------|------|
| Subject <= 0 | 验证失败，含 "Subject must be greater than 0" |
| Grade <= 0 | 验证失败，含 "Grade must be greater than 0" |
| Name 为空 | 验证失败，含 "Name is required" |
| 全部字段合法 | 验证通过 |

#### BatchTagKnowledgeRequestValidator

| 场景 | 期望 |
|------|------|
| QuestionIds 为空 | 验证失败，含 "At least one question id is required" |
| KnowledgeId 无效 | 验证失败，含 "KnowledgeId must be a valid UUID" |
| Subject <= 0 | 验证失败，含 "Subject must be greater than 0" |
| Grade <= 0 | 验证失败，含 "Grade must be greater than 0" |
| UserId 为空 | 验证失败，含 "UserId is required" |

#### CreateTagRequestValidator

| 场景 | 期望 |
|------|------|
| Name 为空 | 验证失败，含 "Name is required" |
| Name 超过 100 字符 | 验证失败，含 "Name must not exceed 100 characters" |
| UserId 为空 | 验证失败，含 "UserId is required" |
| Color 格式非 HEX | 验证失败，含 "Color must be a valid HEX color (e.g., #FF6B6B)" |
| Color 缺失 | 验证通过（color 可选） |
| 全部字段合法 | 验证通过 |

#### BatchTagQuestionRequestValidator

| 场景 | 期望 |
|------|------|
| QuestionIds 为空 | 验证失败，含 "At least one question id is required" |
| TagIds 为空 | 验证失败，含 "At least one tag id is required" |
| UserId 为空 | 验证失败，含 "UserId is required" |
| 全部字段合法 | 验证通过 |

## 端点集成测试（可选）

如需对 HTTP 端点做端到端测试，可使用 `WebApplicationFactory<Program>`：

```csharp
public class QuestionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public QuestionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetQuestion_ReturnsQuestion_WhenExists()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/admin/questions/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

注意：集成测试需要把 DbContext 切换到 EF Core InMemory 数据库，通过 `WebApplicationFactory.ConfigureServices` 注入。

## 运行方式

```bash
cd src/services/ruoyu.questionBank/src
dotnet test Tests/QuestionBank.Test.csproj --configuration Release
```

## 约定

- Domain 层测试置于 `src/Tests/Services/` 目录下
- Validation 组件测试置于 `src/Tests/Validation/` 目录下
- 端点测试置于 `src/Tests/Endpoints/` 目录下（可选）
- 静态类（如 `ImageValidationHelper`）直接调用，无需 Mock
- 异常断言使用 `FluentAssertions`
- 错误消息断言使用英文（按 `30-backend-routing.md` 编码规范）
- 不修改被测代码以适配测试；如需可测试性改进，先更新本文档再改代码
