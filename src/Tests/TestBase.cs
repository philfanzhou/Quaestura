using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;

namespace QuestionBank.Test;

public class TestBase : IDisposable
{
    protected readonly QuestionBankDbContext _dbContext;

    public TestBase()
    {
        var options = new DbContextOptionsBuilder<QuestionBankDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _dbContext = new QuestionBankDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }
}
