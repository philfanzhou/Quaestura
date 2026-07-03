using System;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;

namespace QuestionBank.Test;

public class TestBase : IDisposable
{
    protected readonly QuestionBankDbContext _dbContext;

    public TestBase()
    {
        var options = new DbContextOptionsBuilder<QuestionBankDbContext>()
            .UseInMemoryDatabase($"QuestionBankTest_{Guid.NewGuid()}")
            .Options;

        _dbContext = new QuestionBankDbContext(options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
