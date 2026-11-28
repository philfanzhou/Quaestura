using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quaestura.Tests.Authentication;
using Xunit;

namespace Quaestura.Tests.Integration;

public sealed class QuestionUploadTests(QuaesturaApiFactory factory) : IClassFixture<QuaesturaApiFactory>
{
    [Fact]
    public async Task InvalidEditId_LeavesTheCatalogUnchanged_AndValidEditUpdatesTheOriginal()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study", "teacher"));

        using var created = await UploadAsync(client, new { level = 1 }, "Original stem");
        created.EnsureSuccessStatusCode();
        var result = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("data").GetProperty("id").GetString()!;
        var original = await client.GetFromJsonAsync<JsonObject>($"/admin/questions/{id}");
        const string search = "/admin/questions?subject=2&grade=7";

        foreach (var question in new object[] { new { id = id[..^1] }, new { id = 123 } })
        {
            using var invalid = await UploadAsync(client, question, "Replacement stem");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("QUAESTURA_VALIDATION_FAILED", problem.GetProperty("quaesturaErrorCode").GetString());
            var page = await client.GetFromJsonAsync<JsonElement>(search);
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            var detail = await client.GetFromJsonAsync<JsonObject>($"/admin/questions/{id}");
            Assert.True(JsonNode.DeepEquals(original, detail));
        }

        using var updated = await UploadAsync(client, new { id }, "Replacement stem");
        updated.EnsureSuccessStatusCode();
        var updatedResult = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, updatedResult.GetProperty("data").GetProperty("id").GetString());
        var finalPage = await client.GetFromJsonAsync<JsonElement>(search);
        Assert.Equal(1, finalPage.GetProperty("total").GetInt32());
        var onlyQuestion = Assert.Single(finalPage.GetProperty("data").EnumerateArray());
        Assert.Equal(id, onlyQuestion.GetProperty("id").GetString());
        Assert.Equal("Replacement stem", onlyQuestion.GetProperty("content").GetString());
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, object question, string stem)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(question)), "question" },
            { new StringContent(JsonSerializer.Serialize(new { content = stem })), "content" },
            { new StringContent("2"), "subject" },
            { new StringContent("7"), "grade" }
        };
        return await client.PostAsync("/admin/questions", form);
    }
}
