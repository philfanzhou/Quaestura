using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaestura.Database;
using Quaestura.Database.Entity;
using Quaestura.Tests.Authentication;
using Xunit;

namespace Quaestura.Tests.Integration;

public sealed class QuestionKnowledgeBindingTests(QuaesturaApiFactory factory) : IClassFixture<QuaesturaApiFactory>
{
    [Fact]
    public async Task MixedBatch_OnlyBindsMatchingQuestionsOnce()
    {
        var knowledge = new Knowledge { Name = "Equations", Subject = 2, Grade = 7 };
        var matching = new Question { Subject = 2, Grade = 7 };
        var otherSubject = new Question { Subject = 1, Grade = 7 };
        var otherGrade = new Question { Subject = 2, Grade = 8 };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
            db.AddRange(knowledge, matching, otherSubject, otherGrade);
            await db.SaveChangesAsync();
        }

        using var client = CreateClient();
        var batch = new
        {
            questionIds = new[] { otherSubject.Id, matching.Id, otherGrade.Id },
            knowledgeId = knowledge.Id, subject = knowledge.Subject, grade = knowledge.Grade
        };
        foreach (var expectedNewBindings in new[] { 1, 0 })
        {
            using var response = await client.PostAsJsonAsync("/admin/question-knowledges/batch-tag", batch);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedNewBindings, result.GetProperty("data").GetProperty("taggedCount").GetInt32());
        }

        var page = await client.GetFromJsonAsync<JsonElement>($"/admin/question-knowledges?knowledgeId={knowledge.Id}");
        Assert.Equal(matching.Id, Assert.Single(page.GetProperty("data").EnumerateArray()).GetProperty("id").GetGuid());

        var relations = await client.GetFromJsonAsync<JsonElement>($"/admin/question-knowledges?questionId={matching.Id}");
        var binding = Assert.Single(relations.GetProperty("data").EnumerateArray());
        Assert.Equal(knowledge.Id, binding.GetProperty("knowledgeId").GetGuid());
        Assert.Equal(matching.Subject, binding.GetProperty("subject").GetInt32());
        Assert.Equal(matching.Grade, binding.GetProperty("grade").GetInt32());
    }

    [Fact]
    public async Task ExistingMismatchedBinding_RemainsVisibleWithTheQuestionsSubjectAndGrade()
    {
        var question = new Question { Subject = 1, Grade = 8 };
        var knowledge = new Knowledge { Name = "Equations", Subject = 2, Grade = 7 };
        var existing = new QuestionKnowledge { Question = question, Knowledge = knowledge };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
            db.Add(existing);
            await db.SaveChangesAsync();
        }

        using var client = CreateClient();
        var relations = await client.GetFromJsonAsync<JsonElement>($"/admin/question-knowledges?questionId={question.Id}");
        var binding = Assert.Single(relations.GetProperty("data").EnumerateArray());
        Assert.Equal(existing.Id, binding.GetProperty("id").GetGuid());
        Assert.Equal(knowledge.Id, binding.GetProperty("knowledgeId").GetGuid());
        Assert.Equal(question.Subject, binding.GetProperty("subject").GetInt32());
        Assert.Equal(question.Grade, binding.GetProperty("grade").GetInt32());
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        return client;
    }
}
