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

## PostgreSQL target-preparation integration tests

`src/Tests/Database/` contains real-database tests for the startup target-preparation stage (`QuaesturaDatabaseTargetPreparer`, built on the shared ServiceMantle PostgreSQL provider). They use `Testcontainers.PostgreSql` to start one shared `postgres:16-alpine` container per test collection (`TargetPreparationIntegrationCollection`, serialized) with unique database and role names per test:

- `PostgreSqlTargetPreparationFixture.cs`: container fixture with helpers for databases, roles (with and without `CREATEDB`), ownership queries, and a per-run secret canary.
- `TargetPreparationTests.cs`: existing targets are never prepared or modified, missing targets are created only when allowed (with the real-lock migration orchestration afterwards producing the six tables and both history rows), `AllowCreate` parsing, permission/reachability/identity/owner-conflict refusals, concurrent dual-instance convergence, cancellation, and canary non-leakage.
- `HostStartupTargetPreparationTests.cs`: the real `Program.cs` host creating a missing database end to end, and refusing startup for `Database:AllowCreate=false` or an invalid boolean.

Requirements: a working Docker environment (local machine or GitHub-hosted runner). The tests run in CI as part of the normal `dotnet test` step and are never skipped by default. The existing InMemory-based `QuaesturaApiFactory` endpoint tests do not touch PostgreSQL and are unchanged.

## PostgreSQL migration integration tests

`src/Tests/Database/` contains real-database tests for the strict migration executor (`QuaesturaMigrationExecutor`) and the shared migration orchestration (real PostgreSQL advisory lock) used at startup. They use `Testcontainers.PostgreSql` to start one shared `postgres:16-alpine` container per test collection (`MigrationIntegrationCollection`, serialized) and create an isolated database per test:

- `PostgreSqlMigrationFixture.cs`: container fixture, golden-state builders (migrated, Initial-history-only, EnsureCreated legacy, corrupted variants, synthetic business rows), and a secret-canary log capture.
- `MigrationOrchestration.cs`: the production-shaped orchestration builder — real executor, real `PostgreSqlMigrationLockProvider`, shared orchestrator, production service id; there is no lock-free path in these tests.
- `MigrationInspectionTests.cs`: read-only classification of every semantic state (empty, missing catalog, known history prefixes, legacy takeover candidates, corrupt/unknown/partial states, authentication failure), each rejection proven zero-write.
- `MigrationExecutionTests.cs`: orchestration-driven migration execution, legacy takeover with data preservation, cancellation between phases with recovery on the next orchestration, second-run skip over a current database, orchestration refusals with stable error codes, and canary non-leakage.
- `MigrationOrchestrationTests.cs`: the real-lock behavior — two concurrent hosts converge to exactly one execution, a held lock times out safely and recovers after release, caller cancellation while waiting propagates on the caller's own token, and controlled executor failures, final-state mismatches, and lost leases never report success while the lock stays acquirable for the next session.
- `HostStartupMigrationTests.cs`: the real `Program.cs` host starting against an empty PostgreSQL database through the full lock-orchestrated startup chain.

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


## Hosted-login browser tests

Run the required Release build/test and frontend build, then:

```bash
cd frontend
npx playwright install chromium
npm test
```

The only added dev dependency is `@playwright/test 1.63.0` (Node >=20). CI installs Chromium with
`npx playwright install --with-deps chromium` and actually runs `npm test`. Docker builds only the
SPA artifacts; Chromium is not required for image builds or shipped at runtime.

`hosted-login.spec.ts` drives Chromium through the actual Quaestura `Program`/Kestrel (production HTTP listener on 5007 plus a test-only self-signed
HTTPS listener on 5009), the
official SignaCore client, a synthetic HTTP authority on 5008, and the built SPA. It covers deep
links, callback admission, all management pages, tag CRUD, Cookie/CSRF, fixed failure presentation,
expiry, prepared/local-only logout, and return failure. The authority uses generated test RSA keys
and synthetic identities only. `BrowserHostFixture` is explicitly skipped in ordinary solution
tests; Playwright enables and owns that process fixture with `QUAESTURA_BROWSER_WEBROOT`, then
stops it in teardown. Ports 5007, 5008, 5009, and the Vite consumer-test port 8091 must be free.

`session-consumer.spec.ts` uses the same source modules in a real Chromium/Vite document with
intercepted HTTP responses to deterministically exercise single-flight guards/CSRF, unsafe methods,
401/403, cancellation/network failures, logout reconciliation, generation races, redirect/reason
validation, and throwing storage. These consumer tests complement, rather than replace, the real
Host tests. `HostedLoginTests`/`HostedLoginPresentationTests` retain API, authorization, zero-effect
CSRF, cookie/one-time-return, cancellation, security headers, and real-log/span canary checks.
No protocol URLs, tokens, secrets, screenshots, traces, or production personal data are recorded in
browser test artifacts. Real registered SignaCore image acceptance remains a separate parent task.


`AdminAuthEndpointsTests` and `RetiredPasswordPipelineTests` prove the retired password POST returns
fixed 410 for malformed/empty/non-JSON input and old credentials, with throw-on-read request bodies
on the actual HTTPS Program pipeline, including valid opaque/antiforgery cookies, path case and
trailing slash. Other unsafe Cookie writes remain 403 with zero effects. `HostedLoginConfigurationTests`
starts real Program for each null/empty/whitespace required field, checks zero official state/schemes,
SPA/health/Bearer availability, fixed 503 and key-only logs, ignored Enabled, and missing-field
precedence over invalid optional settings. Complete illegal configuration retains startup failure.
The cold-metadata test proves a supplied valid JWT on the retired route fetches no configuration,
while a subsequent business Bearer still fetches/validates it; configured and missing modes also
preserve the original message-received event only on other routes.
Retained callback, Bearer, cookie/CSRF/state/logout and observability tests run normally; the only
ordinary-solution skip remains the opt-in Playwright process launcher described above.
