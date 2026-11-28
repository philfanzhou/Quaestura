using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaestura.Database;
using Quaestura.Database.Entity;
using Quaestura.Tests.Authentication;
using Xunit;

namespace Quaestura.Tests.Integration;

public sealed class QuestionTagSearchTests(QuaesturaApiFactory factory) : IClassFixture<QuaesturaApiFactory>
{
    [Fact]
    public async Task TaggedSearch_ReturnsEveryMatchOnceAcrossConsecutivePages()
    {
        var tag = new Tag { Name = "revision" };
        var matches = new List<Question>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
            db.Tags.Add(tag);
            for (var i = 0; i < 100; i++)
            {
                // Tied timestamps span a page boundary.
                var question = new Question { Subject = 2, Grade = 8, CreatedAt = DateTimeOffset.UnixEpoch.AddDays(i / 50) };
                db.Questions.Add(question);
                if (i % 4 == 0 && i < 92)
                {
                    db.QuestionTags.Add(new QuestionTag { Question = question, Tag = tag });
                    matches.Add(question);
                }
            }
            db.QuestionTags.Add(new QuestionTag { Question = new Question { Subject = 2, Grade = 7 }, Tag = tag });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        var expectedIds = matches.OrderByDescending(q => q.CreatedAt).ThenBy(q => q.Id).Select(q => q.Id).ToArray();
        var actualIds = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await client.GetFromJsonAsync<JsonElement>(
                $"/admin/questions?subject=2&grade=8&tagId={tag.Id}&page={page}&size=10");
            Assert.Equal(23, result.GetProperty("total").GetInt32());
            Assert.Equal(3, result.GetProperty("totalPages").GetInt32());
            Assert.Equal(page, result.GetProperty("page").GetInt32());
            Assert.Equal(10, result.GetProperty("size").GetInt32());
            var data = result.GetProperty("data");
            Assert.Equal(expectedIds.Skip((page - 1) * 10).Take(10).Count(), data.GetArrayLength());
            actualIds.AddRange(data.EnumerateArray().Select(q => q.GetProperty("id").GetGuid()));
        }
        Assert.Equal(expectedIds, actualIds.ToArray());
    }
}
