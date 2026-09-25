using System;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;

namespace Quaestura.Tests;

public class TestBase : IDisposable
{
    protected readonly QuaesturaDbContext _dbContext;

    public TestBase()
    {
        var options = new DbContextOptionsBuilder<QuaesturaDbContext>()
            .UseInMemoryDatabase($"QuaesturaTest_{Guid.NewGuid()}")
            .Options;

        _dbContext = new QuaesturaDbContext(options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
