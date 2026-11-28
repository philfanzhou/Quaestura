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
            userId = (string?)null, studentId = (string?)null, mistakeId = (string?)null, subject = 2, grade = 3,
            createdAt = "2026-01-02T02:00:00.123Z", updatedAt = "2026-01-03T02:00:00.123Z"
        };
        var detail = await client.GetFromJsonAsync<JsonObject>($"/admin/questions/{question.Id}");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(new { success = true, data = expected }), detail));

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
