using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Quaestura.Database;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Quaestura.Tests.Authentication;
using Xunit;

namespace Quaestura.Tests.Integration;

public sealed class QuestionResponseTests(QuaesturaApiFactory factory) : IClassFixture<QuaesturaApiFactory>
{
    [Fact]
    public async Task BatchGet_PreservesRequestedOrderIncludingMissingAndRepeatedIds()
    {
        var first = new Question();
        var second = new Question();
        var third = new Question();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
            db.AddRange(first, second, third);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        var missing = Guid.NewGuid();
        var ids = new[] { third.Id, missing, first.Id, third.Id, second.Id, missing };
        using var response = await client.PostAsJsonAsync("/admin/questions/batch-get", new { ids });
        response.EnsureSuccessStatusCode();
        var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(batch.GetProperty("success").GetBoolean());
        var entries = batch.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(ids, entries.Select(entry => entry.GetProperty("id").GetGuid()));
        foreach (var (entry, id) in entries.Zip(ids))
        {
            var question = entry.GetProperty("question");
            if (id == missing)
                Assert.Equal(JsonValueKind.Null, question.ValueKind);
            else
                Assert.Equal(id, question.GetProperty("id").GetGuid());
        }
    }

    [Fact]
    public async Task BatchGet_AcceptsOneHundredMissingEntriesAndRejectsInvalidLists()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        using var response = await client.PostAsJsonAsync("/admin/questions/batch-get", new { ids });
        response.EnsureSuccessStatusCode();
        var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = batch.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(ids, entries.Select(entry => entry.GetProperty("id").GetGuid()));
        Assert.All(entries, entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("question").ValueKind));

        foreach (var body in new object[] { new { }, new { ids = Array.Empty<Guid>() },
                     new { ids = ids.Append(ids[0]) }, new { ids = new[] { "invalid" } } })
        {
            using var invalid = await client.PostAsJsonAsync("/admin/questions/batch-get", body);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Upload_IgnoresLegacyMistakeId()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study", "teacher"));
        using var form = new MultipartFormDataContent
        {
            { new StringContent("""{"studentId":"example-student","mistakeId":"obsolete"}"""), "question" },
            { new StringContent("""{"content":"What is 2 + 2?"}"""), "content" },
            { new StringContent("2"), "subject" },
            { new StringContent("7"), "grade" }
        };
        using var response = await client.PostAsync("/admin/questions", form);
        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = upload.GetProperty("data").GetProperty("id").GetGuid();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/admin/questions/{id}");
        var question = detail.GetProperty("data");
        Assert.Equal("example-student", question.GetProperty("studentId").GetString());
        Assert.False(question.TryGetProperty("mistakeId", out _));
    }

    [Fact]
    public async Task DetailSearchAndRelations_ReturnTheSameQuestionFields()
    {
        var pictures = new Mock<IOssQuestionService>();
        pictures.Setup(s => s.GetPictureUrls(It.Is<IEnumerable<string>>(paths =>
                paths.SequenceEqual(new[] { "questions/image.png" }))))
            .ReturnsAsync(["/signed/image.png"]);
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(pictures.Object)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));

        var timestamp = new DateTimeOffset(2026, 1, 2, 10, 0, 0, 123, TimeSpan.FromHours(8));
        var question = new Question
        {
            CreatedAt = timestamp, UpdatedAt = timestamp.AddDays(1), Subject = 2, Grade = 3,
            Level = 4, Type = 5, Width = 60, Height = 70, PicturePaths = "[\"questions/image.png\"]"
        };
        var tag = new Tag { Name = "example" };
        var knowledge = new Knowledge { Name = "example" };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
            db.AddRange(question, tag, knowledge,
                new QuestionContent { QuestionId = question.Id, Content = "text", CorrectAnswer = "answer" },
                new QuestionTag { QuestionId = question.Id, TagId = tag.Id },
                new QuestionKnowledge { QuestionId = question.Id, KnowledgeId = knowledge.Id });
            await db.SaveChangesAsync();
        }

        var expected = new
        {
            id = question.Id.ToString(), level = 4, type = 5, width = 60, height = 70,
            picturePaths = new[] { "/signed/image.png" }, content = "text", correctAnswer = "answer", analysis = "",
            userId = (string?)null, studentId = (string?)null, subject = 2, grade = 3,
            createdAt = "2026-01-02T02:00:00.123Z", updatedAt = "2026-01-03T02:00:00.123Z"
        };
        var detail = await client.GetFromJsonAsync<JsonObject>($"/admin/questions/{question.Id}");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(new { success = true, data = expected }), detail));

        using var batchResponse = await client.PostAsJsonAsync("/admin/questions/batch-get", new { ids = new[] { question.Id } });
        batchResponse.EnsureSuccessStatusCode();
        var batch = await batchResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(JsonNode.DeepEquals(detail!["data"], batch!["data"]![0]!["question"]));

        var expectedPage = JsonSerializer.SerializeToNode(new
        {
            success = true, data = new[] { expected }, total = 1, page = 1, size = 10, totalPages = 1
        });
        foreach (var path in new[]
        {
            "/admin/questions?subject=2&grade=3",
            $"/admin/question-tags?tagId={tag.Id}",
            $"/admin/question-knowledges?knowledgeId={knowledge.Id}"
        })
        {
            var page = await client.GetFromJsonAsync<JsonObject>(path);
            Assert.True(JsonNode.DeepEquals(expectedPage, page), path);
        }
    }
}
