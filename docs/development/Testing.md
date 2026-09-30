# Unit Testing

This document describes the unit test project structure, coverage, and conventions of the `Quaestura` service.

## Test project structure

| Project | Path | Coverage |
|------|------|---------|
| `Quaestura.Tests` | `src/Tests/` | Domain-layer services + Service-layer validation components + endpoint integration tests (optional) |

## Test frameworks and dependencies

- xUnit 2.9.3
- Moq 4.21.0
- FluentAssertions 7.2.2
- FluentValidation 12.1.1 (for testing RequestValidators)
- Microsoft.EntityFrameworkCore.InMemory 10.0.12 (Domain-layer integration tests + endpoint integration tests)

## Domain-layer test coverage

| Test file | Service covered |
|---------|---------|
| `Services/KnowledgeServiceTests.cs` | `IKnowledgeService` main paths |
| `Services/KnowledgeServiceAdditionalTests.cs` | `IKnowledgeService` edge cases and error paths |
| `Services/QuestionServiceTests.cs` | `IQuestionService` main paths |
| `Services/QuestionServiceAdditionalTests.cs` | `IQuestionService` edge cases and error paths |
| `Services/QuestionKnowledgeServiceTests.cs` | `IQuestionKnowledgeService` main paths |
| `Services/TagServiceTests.cs` | `ITagService` main paths (CRUD, duplicate checks, reference constraints, usage_count increment/decrement) |
| `Services/TagServiceAdditionalTests.cs` | `ITagService` edge cases and error paths |
| `Services/QuestionTagServiceTests.cs` | `IQuestionTagService` main paths (batch tagging, removal, queries, count synchronization) |

Domain-layer tests use the EF Core InMemory database provided by `TestBase` and do not depend on the WebAPI layer.

## Validation component test coverage

### ImageValidationHelper test scenarios

| Scenario | Expected |
|------|------|
| null byte array | `InvalidOperationException("Image data is empty")` |
| Empty byte array | `InvalidOperationException("Image data is empty")` |
| Larger than 10MB | `InvalidOperationException("Image size exceeds 10MB")` |
| JPEG magic number | Passes |
| PNG magic number | Passes |
| GIF87a magic number | Passes |
| GIF89a magic number | Passes |
| WebP (RIFF) magic number | Passes |
| BMP magic number | Passes |
| Unsupported format | `InvalidOperationException("Image format not supported")` |
| Data shorter than the magic number | `InvalidOperationException("Image format not supported")` |
| null image list | Passes (returns immediately) |
| Empty image list | Passes (returns immediately) |
| Batch validation with one invalid image | `InvalidOperationException` |
| Batch validation with all images valid | Passes |

### RequestValidators test scenarios

#### CreateQuestionRequestValidator

| Scenario | Expected |
|------|------|
| Pictures empty | Validation fails with "At least one picture is required" |
| Level negative | Validation fails with "Level must not be negative" |
| Type negative | Validation fails with "Type must not be negative" |
| All fields valid | Validation passes |

> Note: `subject` / `grade` are validated separately as form fields (inside the endpoint) and are outside the scope of `CreateQuestionRequestValidator`. `userId` has been removed from the DTO and is read from the JWT instead.

#### CreateKnowledgeRequestValidator

| Scenario | Expected |
|------|------|
| Subject <= 0 | Validation fails with "Subject must be greater than 0" |
| Grade <= 0 | Validation fails with "Grade must be greater than 0" |
| Name empty | Validation fails with "Name is required" |
| All fields valid | Validation passes |

#### BatchTagKnowledgeRequestValidator

| Scenario | Expected |
|------|------|
| QuestionIds empty | Validation fails with "At least one question id is required" |
| KnowledgeId invalid | Validation fails with "KnowledgeId must be a valid UUID" |
| Subject <= 0 | Validation fails with "Subject must be greater than 0" |
| Grade <= 0 | Validation fails with "Grade must be greater than 0" |

#### CreateTagRequestValidator

| Scenario | Expected |
|------|------|
| Name empty | Validation fails with "Name is required" |
| Name longer than 100 characters | Validation fails with "Name must not exceed 100 characters" |
| Color not in HEX format | Validation fails with "Color must be a valid HEX color (e.g., #FF6B6B)" |
| Color missing | Validation passes (color is optional) |
| All fields valid | Validation passes |

#### BatchTagQuestionRequestValidator

| Scenario | Expected |
|------|------|
| QuestionIds empty | Validation fails with "At least one question id is required" |
| TagIds empty | Validation fails with "At least one tag id is required" |
| All fields valid | Validation passes |

> Note: the `UserId` field has been removed from all DTOs (it is read from the JWT instead), and the related validation scenarios were removed accordingly.

## Authorization tests

### Tag endpoint permission tests

**These tests do not exist yet.** The table below records the expected coverage per the permission matrix in `Authentication.md` §2.2; writing the tests is tracked separately (see the "known adjacent issues" note of issue #26).

| Scenario | Expected |
|------|------|
| Student calls `POST /admin/tags` | 403 `QUAESTURA_FORBIDDEN` |
| Teacher calls `POST /admin/tags` (create) | 200 success |
| Assistant calls `POST /admin/tags` (create) | 200 success |
| Teacher updates a tag they created | 200 success |
| Teacher updates a tag created by someone else | 403 `QUAESTURA_FORBIDDEN_NOT_OWNER` |
| Assistant deletes a tag they created (usage_count=0) | 200 success |
| Assistant deletes a tag created by someone else | 403 `QUAESTURA_FORBIDDEN_NOT_OWNER` |
| Student calls `GET /admin/tags` | 200 success (students can query) |
| Student calls `POST /admin/question-tags/batch-tag` | 200 success (students can tag) |

## Endpoint integration tests (optional)

For end-to-end tests of HTTP endpoints, use `WebApplicationFactory<Program>`:

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

Note: integration tests must switch the DbContext to the EF Core InMemory database, injected through `WebApplicationFactory.ConfigureServices`.

## PostgreSQL migration integration tests

`src/Tests/Database/` contains real-database tests for the strict migration executor (`QuaesturaMigrationExecutor`) and the startup entry (`DatabaseInitializer`). They use `Testcontainers.PostgreSql` to start one shared `postgres:16-alpine` container per test collection (`MigrationIntegrationCollection`, serialized) and create an isolated database per test:

- `PostgreSqlMigrationFixture.cs`: container fixture, golden-state builders (migrated, Initial-history-only, EnsureCreated legacy, corrupted variants, synthetic business rows), and a secret-canary log capture.
- `MigrationInspectionTests.cs`: read-only classification of every semantic state (empty, missing catalog, known history prefixes, legacy takeover candidates, corrupt/unknown/partial states, authentication failure), each rejection proven zero-write.
- `MigrationExecutionTests.cs`: migration execution, legacy takeover with data preservation, cancellation between phases with recovery, idempotency, initializer refusals, and canary non-leakage.
- `HostStartupMigrationTests.cs`: the real `Program.cs` host starting against an empty PostgreSQL database.

Requirements: a working Docker environment (local machine or GitHub-hosted runner). The tests run in CI as part of the normal `dotnet test` step and are never skipped by default. The container password is randomly generated per run and doubles as the secret canary asserted absent from exceptions and logs.

## Running the tests

```bash
cd src
dotnet test Tests/Quaestura.Tests.csproj --configuration Release
```

## Conventions

- Domain-layer tests live in `src/Tests/Services/`
- Validation component tests live in `src/Tests/Validation/`
- Host/authentication endpoint tests live in `src/Tests/Authentication/`
- ServiceMantle observability tests (host identity, correlation header/scope, base instrumentation) live in `src/Tests/Observability/`
- Endpoint tests live in `src/Tests/Endpoints/` (optional)
- Static classes (such as `ImageValidationHelper`) are called directly without mocks
- Exception assertions use `FluentAssertions`
- Error message assertions use English (see the error message conventions in `ErrorHandling.md`)
- Do not change the code under test to suit the tests; if testability needs improving, update this document before changing the code
