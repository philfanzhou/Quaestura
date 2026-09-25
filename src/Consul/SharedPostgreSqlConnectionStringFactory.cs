using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace Quaestura.Consul;

public static class SharedPostgreSqlConnectionStringFactory
{
    public static string? BuildOrFallback(IConfiguration configuration, string? fallbackConnectionString = null)
    {
        var host = configuration["PostgreSql:Host"];
        var username = configuration["PostgreSql:Username"];
        var password = configuration["PostgreSql:Password"];
        var databaseName = configuration["Database:Name"];

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(databaseName))
        {
            return fallbackConnectionString;
        }

        var port = configuration["PostgreSql:Port"];
        var builder = new DbConnectionStringBuilder
        {
            ["Host"] = host,
            ["Port"] = string.IsNullOrWhiteSpace(port) ? "5432" : port,
            ["Database"] = databaseName,
            ["Username"] = username
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            builder["Password"] = password;
        }

        return builder.ConnectionString;
    }
}
